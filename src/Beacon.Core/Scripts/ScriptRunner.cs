using System.Diagnostics;
using System.Globalization;
using System.Text;
using Beacon.Core.Diagnostics;
using Beacon.Core.Library;

namespace Beacon.Core.Scripts;

/// <summary>
/// Runs scripts as hidden processes under the owner's own account. Nothing is elevated.
/// </summary>
/// <remarks>
/// Each run has a thread of its own, which starts the program, waits for it to end and hands
/// over the result. The script's folder is its working folder. No window appears. Standard
/// input is closed, so a script that asks a question gets no answer instead of waiting for
/// one. What the script prints is read as UTF-8 and kept in memory only, never written to the
/// log. A script that runs for too long is stopped together with every process it started.
/// The same happens to every script still running when the runner is disposed.
/// </remarks>
public sealed class ScriptRunner : IScriptRunner, IDisposable
{
    /// <summary>
    /// The most that is kept of what a script wrote to standard output, and the same again for
    /// standard error: about a megabyte. When a script prints more, the oldest part is dropped.
    /// </summary>
    public const int MaxOutputChars = 1024 * 1024;

    /// <summary>
    /// How long is waited, once a script has ended, for the rest of what it printed. The wait
    /// is over at once in the ordinary case. It is used up only when the script started a
    /// program and left it running: that program holds on to the script's output, whose end
    /// then does not come until the program closes.
    /// </summary>
    public static readonly TimeSpan DrainTime = TimeSpan.FromSeconds(2);

    private readonly Func<ScriptOptions> _options;
    private readonly Action<string, Exception?> _report;
    private readonly TimeProvider _time;
    private readonly Func<string, string?> _find;
    private readonly int _maxOutputChars;
    private readonly TimeSpan _drainTime;
    private readonly Lock _gate = new();
    private readonly HashSet<Run> _running = [];
    private TaskCompletionSource? _idle;
    private bool _disposed;

    /// <param name="options">Gives the script settings in use. Called once for each run, on the thread that calls <see cref="Start"/>.</param>
    /// <param name="report">
    /// Receives one line for the log about each run: fixed words, the script's id made safe,
    /// its exit code and how long it took. Never what the script printed, the text typed for
    /// it, its title or its file name. The exception, when there is one, is logged by type
    /// only. It can be called on any thread. A callback that throws is ignored.
    /// </param>
    /// <param name="time">Supplies the timer for the timeout. Left out everywhere except in tests.</param>
    /// <param name="find">Finds a program file by name. Left out everywhere except in tests: it is then <see cref="ProgramFinder.Find"/>.</param>
    /// <param name="maxOutputChars">Left out everywhere except in tests.</param>
    /// <param name="drainTime">Left out everywhere except in tests: it is then <see cref="DrainTime"/>.</param>
    public ScriptRunner(
        Func<ScriptOptions> options,
        Action<string, Exception?>? report = null,
        TimeProvider? time = null,
        Func<string, string?>? find = null,
        int maxOutputChars = MaxOutputChars,
        TimeSpan? drainTime = null)
    {
        _options = options;
        _report = report ?? ((_, _) => { });
        _time = time ?? TimeProvider.System;
        _find = find ?? (program => ProgramFinder.Find(program));
        _maxOutputChars = maxOutputChars;
        _drainTime = drainTime ?? DrainTime;
    }

    /// <summary>How many scripts are running now.</summary>
    public int Running
    {
        get
        {
            lock (_gate)
            {
                return _running.Count;
            }
        }
    }

    /// <summary>Completes when no script is running and every result has been handed over.</summary>
    public Task Idle
    {
        get
        {
            lock (_gate)
            {
                return _idle?.Task ?? Task.CompletedTask;
            }
        }
    }

    public void Start(Script script, string? argument, Action<ScriptResult> finished)
    {
        // Before anything else, so that a runner that has been disposed gives one answer, the
        // exception, and never calls back, not even for a batch file it would have refused.
        ObjectDisposedException.ThrowIf(IsClosing(), this);

        string id = LogSafe.ItemId(script.Id);
        if (ScriptFile.IsBatch(script.FilePath))
        {
            // Decided before a thread or a process exists, so that nothing at all has run.
            if (BatchText.IsUnsafePath(script.FilePath))
            {
                Report($"Script {id} was not run: its path cannot be given to cmd safely", null);
                Finish(id, finished, new ScriptResult(ScriptOutcome.UnsafePath, null, "", "", TimeSpan.Zero));
                return;
            }

            if (argument is not null && BatchText.IsUnsafeArgument(argument))
            {
                Report($"Script {id} was not run: the text typed cannot be given to a batch file safely", null);
                Finish(id, finished, new ScriptResult(ScriptOutcome.UnsafeArgument, null, "", "", TimeSpan.Zero));
                return;
            }
        }
        else if (ScriptFile.IsPowerShell(script.FilePath) && PowerShellPath.IsUnsafe(script.FilePath))
        {
            // The same, for a path PowerShell would read as a pattern.
            Report($"Script {id} was not run: its path cannot be given to PowerShell safely", null);
            Finish(id, finished, new ScriptResult(ScriptOutcome.UnsafePath, null, "", "", TimeSpan.Zero));
            return;
        }

        var run = new Run(script, argument, finished, _options(), id);
        lock (_gate)
        {
            // Looked at again, under the lock: Dispose may have run since the first look.
            ObjectDisposedException.ThrowIf(_disposed, this);
            _running.Add(run);
            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            new Thread(() => Execute(run)) { IsBackground = true, Name = "Beacon script" }.Start();
        }
        catch
        {
            // Without its thread the run would be counted as running for ever.
            Ended(run);
            throw;
        }
    }

    /// <summary>Stops every script that is still running, with everything it started. Their results are not handed over.</summary>
    public void Dispose()
    {
        List<Run> running;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            running = [.. _running];
        }

        foreach (var run in running)
        {
            Kill(run, timedOut: false);
        }
    }

    // The whole of one run, on its own thread.
    private void Execute(Run run)
    {
        long started = _time.GetTimestamp();
        ScriptResult? result;
        try
        {
            result = RunToTheEnd(run, started);
        }
        catch (Exception ex)
        {
            // Nobody is above this on its own thread: an escaped exception would end the app.
            Report($"Script {run.Id} could not be run", ex);
            result = NotStarted(started);
        }

        // Handed over before the run is taken off the list, so that Idle means what it says.
        if (result is not null)
        {
            Finish(run.Id, run.Finished, result);
        }

        Ended(run);
    }

    /// <returns>Null when the script was stopped because Beacon is closing: there is nobody left to tell.</returns>
    private ScriptResult? RunToTheEnd(Run run, long started)
    {
        if (ScriptCommand.For(run.Script, run.Argument, run.Options, _find) is not { } command)
        {
            Report($"Script {run.Id} could not be started: the program that runs it was not found", null);
            return NotStarted(started);
        }

        var info = new ProcessStartInfo(command.Program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(run.Script.FilePath) ?? "",
        };

        // Each value is handed to Windows as an argument of its own. They are never joined
        // into one command line here, so no value can be read as two or as part of another.
        foreach (string argument in command.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (IsClosing())
        {
            return null;
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("No process was started.");
        }
        catch (Exception ex)
        {
            // The program is not one Windows can run, the script's folder has gone, or access was refused.
            Report($"Script {run.Id} could not be started", ex);
            return NotStarted(started);
        }

        try
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The script has ended already and its end of the pipe went with it.
            }

            var output = new CappedText(_maxOutputChars);
            var error = new CappedText(_maxOutputChars);
            var readingOutput = Read(process.StandardOutput, output);
            var readingError = Read(process.StandardError, error);

            lock (run.Gate)
            {
                run.Process = process;
            }

            ITimer? timeout = null;
            if (IsClosing())
            {
                // Dispose ran while the program was being started and so did not see it.
                Kill(run, timedOut: false);
            }
            else
            {
                timeout = _time.CreateTimer(_ => Kill(run, timedOut: true), null, run.Options.Timeout, Timeout.InfiniteTimeSpan);
            }

            int exitCode;
            bool timedOut;
            bool stopped;
            try
            {
                process.WaitForExit();
                exitCode = process.ExitCode;
            }
            finally
            {
                // From here on there is nothing left to stop: a timeout or a Dispose that
                // arrives now finds no process.
                lock (run.Gate)
                {
                    run.Process = null;
                    timedOut = run.TimedOut;
                    stopped = run.Stopped;
                }

                timeout?.Dispose();
            }

            // Taken here, when the script itself has ended, so that it does not include the
            // wait below for a program the script may have left running.
            var duration = _time.GetElapsedTime(started);
            string seconds = duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);

            WaitForTheRest(readingOutput, readingError);
            if (stopped)
            {
                Report($"Script {run.Id} was stopped because Beacon is closing", null);
                return null;
            }

            if (timedOut)
            {
                Report($"Script {run.Id} timed out and was stopped after {seconds} s", null);
                return new ScriptResult(ScriptOutcome.TimedOut, null, output.ToString(), error.ToString(), duration);
            }

            Report($"Script {run.Id} ran: exit code {exitCode.ToString(CultureInfo.InvariantCulture)} after {seconds} s", null);
            return new ScriptResult(ScriptOutcome.Exited, exitCode, output.ToString(), error.ToString(), duration);
        }
        catch
        {
            // Whatever went wrong here, the program has been started. It must not be left
            // running with nothing watching it, so it is stopped before the failure is passed on.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // It has ended already, or cannot be stopped. The failure being passed on is the one that matters.
            }

            throw;
        }
        finally
        {
            // The run must not point at a process that is about to be disposed. On the way
            // through above this was done already, but when creating the timeout's timer
            // threw, the run still held the process, and a Dispose of the runner or a timer
            // that was on its way would have used it after it was disposed.
            lock (run.Gate)
            {
                run.Process = null;
            }

            // This lets go of the process, not of the two output pipes: disposing a process
            // does not close the readers that were taken from it. Each reading thread closes its
            // own pipe when it ends. A program the script left running holds the other end of
            // a pipe open, so that thread goes on reading until the program closes it and then
            // closes the pipe itself. Nothing here needs to wait for that.
            process.Dispose();
        }
    }

    // Reads one of the script's two outputs on a thread of its own, so that a script which
    // fills one of them is never held up because the other is not being read. The thread owns
    // the reader and closes it, and with it the pipe, however the reading ends.
    private Thread Read(StreamReader reader, CappedText into)
    {
        var thread = new Thread(
            () =>
            {
                try
                {
                    using var owned = reader;
                    char[] buffer = new char[4096];
                    int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        into.Append(buffer.AsSpan(0, read));
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // The pipe was closed under the read. What was read so far is kept.
                }
                catch (Exception ex)
                {
                    // Nobody is above this on its own thread: an escaped exception would end the app.
                    Report("What a script printed could not be read", ex);
                }
            },
            maxStackSize: 256 * 1024)
        {
            IsBackground = true,
            Name = "Beacon script output",
        };
        thread.Start();
        return thread;
    }

    // Waits for both outputs to be read to their end, for no longer than the drain time together.
    private void WaitForTheRest(Thread readingOutput, Thread readingError)
    {
        long began = Stopwatch.GetTimestamp();
        readingOutput.Join(_drainTime);
        var left = _drainTime - Stopwatch.GetElapsedTime(began);
        readingError.Join(left > TimeSpan.Zero ? left : TimeSpan.Zero);
    }

    // Called from the timeout's timer, on a pool thread, and from Dispose. It must not throw
    // from either. The whole tree goes: a script that started other programs must not leave
    // them running after it has been stopped.
    private void Kill(Run run, bool timedOut)
    {
        lock (run.Gate)
        {
            if (run.Process is not { } process)
            {
                return;
            }

            try
            {
                if (process.HasExited)
                {
                    return;
                }

                if (timedOut)
                {
                    run.TimedOut = true;
                }
                else
                {
                    run.Stopped = true;
                }

                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                // The whole tree could not be stopped: the list of processes may not have been
                // readable, or a process in it may refuse to be stopped, or the script may have
                // ended by itself at this very moment. The timer does not fire again and Dispose
                // does not come twice, so the script alone is stopped here at least, before
                // anything is said about it.
                try
                {
                    process.Kill();
                }
                catch (Exception)
                {
                    // It has ended already, or cannot be stopped either. The report below is all that is left to do.
                }

                Report($"Script {run.Id} could not be stopped", ex);
            }
        }
    }

    private bool IsClosing()
    {
        lock (_gate)
        {
            return _disposed;
        }
    }

    private ScriptResult NotStarted(long started) =>
        new(ScriptOutcome.NotStarted, null, "", "", _time.GetElapsedTime(started));

    private void Ended(Run run)
    {
        lock (_gate)
        {
            _running.Remove(run);
            if (_running.Count == 0)
            {
                _idle?.TrySetResult();
                _idle = null;
            }
        }
    }

    // The callbacks are the caller's code. One that threw on a run's own thread would end the
    // app, and one that threw from Start would be taken for a script that could not be run.
    private void Finish(string id, Action<ScriptResult> finished, ScriptResult result)
    {
        try
        {
            finished(result);
        }
        catch (Exception ex)
        {
            Report($"The result of script {id} could not be shown", ex);
        }
    }

    private void Report(string message, Exception? error)
    {
        try
        {
            _report(message, error);
        }
        catch (Exception)
        {
            // Nothing more can be done about a log that is broken.
        }
    }

    /// <summary>One run of one script.</summary>
    private sealed class Run(Script script, string? argument, Action<ScriptResult> finished, ScriptOptions options, string id)
    {
        public Script Script { get; } = script;

        public string? Argument { get; } = argument;

        public Action<ScriptResult> Finished { get; } = finished;

        /// <summary>The settings as they were when the run was started. A Reload does not change a run under way.</summary>
        public ScriptOptions Options { get; } = options;

        /// <summary>The script's id, already made safe for the log.</summary>
        public string Id { get; } = id;

        /// <summary>Guards the three members below.</summary>
        public Lock Gate { get; } = new();

        /// <summary>The running process, or null before it has started and after it has ended.</summary>
        public Process? Process { get; set; }

        public bool TimedOut { get; set; }

        public bool Stopped { get; set; }
    }
}
