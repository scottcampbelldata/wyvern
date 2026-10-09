using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace Wyvern.App.Interop;

/// <summary>
/// A background thread that runs a message loop and nothing else. The keyboard hook of snippet
/// auto-expand is installed on one: Windows calls a hook on the thread that installed it, by
/// way of that thread's message loop, and every key press in every program waits for the call
/// to return. On a thread of its own the call never waits for anything Wyvern's UI thread is
/// busy with.
/// </summary>
/// <remarks>
/// <para>The thread runs <c>begin</c>, then its loop, and <c>end</c> from inside the loop when
/// it is stopped. Both run on this thread and nowhere else, so what one installs the other
/// removes on the same thread. No Windows call is made here: the loop is WPF's
/// (<c>Dispatcher.Run</c>, learn.microsoft.com/dotnet/api/system.windows.threading.dispatcher.run:
/// "The main execution frame will continue until the Dispatcher is shutdown"), and it is ended
/// with <c>Dispatcher.BeginInvokeShutdown</c>
/// (learn.microsoft.com/dotnet/api/system.windows.threading.dispatcher.begininvokeshutdown).
/// This class knows nothing of keys or hooks, and nothing in it reads or sends one.</para>
/// <para><see cref="Start"/> and <see cref="Stop"/> are for the thread that owns the object;
/// each waits for this thread, for a bounded time, with one exception: the last wait in
/// <see cref="Start"/> has no bound, and the comment above it says why. These waits are the
/// only waiting between the two threads. This thread never waits for the other.</para>
/// </remarks>
internal sealed class LoopThread
{
    /// <summary>How long <see cref="Start"/> waits for <c>begin</c> to have run.</summary>
    public static readonly TimeSpan StartWait = TimeSpan.FromSeconds(5);

    /// <summary>How long <see cref="Stop"/> waits for the thread to end.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);

    // _state: whether Start is still waiting for the thread. Exactly one of the two threads
    // moves it on from Starting, with Interlocked, so that a thread nobody waits for any more
    // never stays behind with something installed.
    private const int Starting = 0;
    private const int Running = 1;
    private const int Abandoned = 2;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _begun = new(false);
    private readonly Action _begin;
    private readonly Action _end;
    private int _state;

    // Written on the loop's thread before _begun is set, and read by the other only after it.
    private Dispatcher? _loop;
    private Exception? _beginError;

    private LoopThread(string name, Action begin, Action end)
    {
        _begin = begin;
        _end = end;

        // A background thread: it never keeps Wyvern from closing.
        _thread = new Thread(Run) { IsBackground = true, Name = name };

        // Every key press in every program waits for this thread to answer. It does a few
        // microseconds of work for each key, so the raised priority costs the rest of Wyvern
        // nothing, and Wyvern's own background work (indexing at a Reload) must not make it
        // wait for a turn.
        _thread.Priority = ThreadPriority.AboveNormal;
    }

    /// <summary>
    /// Starts the thread, and returns once <paramref name="begin"/> has run on it and its loop
    /// is about to run.
    /// </summary>
    /// <param name="end">Run on the thread, from inside its loop, when it is stopped.</param>
    /// <exception cref="Exception">What <paramref name="begin"/> threw. The thread has ended, and <paramref name="end"/> is not run.</exception>
    /// <exception cref="TimeoutException">
    /// The thread did not get as far in <see cref="StartWait"/>. Whenever it does, it runs
    /// <paramref name="end"/> straight after <paramref name="begin"/> and ends.
    /// </exception>
    public static LoopThread Start(string name, Action begin, Action end)
    {
        var made = new LoopThread(name, begin, end);
        made._thread.Start();
        if (!made._begun.Wait(StartWait)
            && Interlocked.CompareExchange(ref made._state, Abandoned, Starting) == Starting)
        {
            throw new TimeoutException("The thread of the keyboard hook did not start in time.");
        }

        // This wait has no bound, on purpose. It is reached only when the wait above was
        // answered, or when the thread has already won the exchange on _state, so that it is no
        // longer to be given up on, and is two statements away from setting _begun: all it has
        // left to do is store its error and set the event, and nothing there waits. A bound
        // here could end the wait before the event is set, and Start would then report a
        // failure for a thread that believes it was accepted, and goes on to run its loop with
        // its hook installed.
        made._begun.Wait();
        if (made._beginError is { } error)
        {
            made._thread.Join(StopWait);
            ExceptionDispatchInfo.Capture(error).Throw();
        }

        return made;
    }

    /// <summary>
    /// Has the thread run <c>end</c> from inside its loop, ends the loop, and waits for the
    /// thread to end. When this returns the thread has ended.
    /// </summary>
    /// <exception cref="TimeoutException">The thread did not end in <see cref="StopWait"/>. It may still be running.</exception>
    /// <exception cref="Exception">What <c>end</c> threw. The thread has ended all the same.</exception>
    public void Stop()
    {
        Exception? endError = null;

        // Dispatcher.BeginInvoke hands the work to the loop's thread and returns at once. On a
        // loop that has ended already it hands over nothing, and the wait below is over at once.
        _loop!.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            try
            {
                _end();
            }
            catch (Exception ex)
            {
                endError = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }
        }));

        if (!_thread.Join(StopWait))
        {
            throw new TimeoutException("The thread of the keyboard hook did not end in time.");
        }

        // The thread has ended, so what it wrote can be read here.
        if (endError is not null)
        {
            ExceptionDispatchInfo.Capture(endError).Throw();
        }
    }

    private void Run()
    {
        Exception? error = null;
        try
        {
            // Dispatcher.CurrentDispatcher: "creates a new Dispatcher if one is not already
            // associated with the thread". That gives this thread its message queue.
            _loop = Dispatcher.CurrentDispatcher;
            _begin();
        }
        catch (Exception ex)
        {
            error = ex;
        }

        bool waitedFor = Interlocked.CompareExchange(ref _state, Running, Starting) == Starting;
        if (error is null && !waitedFor)
        {
            // Start has given up on this thread. What was begun is undone here and now.
            try
            {
                _end();
            }
            catch (Exception)
            {
                // Nobody is left to tell.
            }
        }

        _beginError = error;
        _begun.Set();
        if (error is null && waitedFor)
        {
            // The message loop. Windows calls the hook from inside it.
            Dispatcher.Run();

            // For as long as the loop runs, this thread holds this LoopThread, and through it
            // the end action, the installed hook's object and the callback Windows was given,
            // whatever the UI side does with its references. A callback that was collected
            // while its hook is installed would crash the process at the next key.
            GC.KeepAlive(this);
        }
        else
        {
            _loop?.InvokeShutdown();
        }
    }
}
