using System.ComponentModel;
using System.Globalization;
using System.Text;
using Wyvern.Core.Platform;

namespace Wyvern.Core.Diagnostics;

/// <summary>
/// Appends timestamped entries to a local log file. When the file reaches the size limit it is
/// renamed to "&lt;name&gt;.1", replacing any earlier one, and a new file is started.
/// </summary>
/// <remarks>
/// An entry starts with a line of the form "2026-10-03T12:00:00Z LEVEL message". An error entry
/// names each exception type with its error numbers, and may be followed by stack frames, each
/// indented by four spaces. Nothing here ever throws.
/// Callers must never pass clipboard content, snippet bodies, script output or keys as the
/// message. Exception messages are never written, because whatever threw chose their text.
/// </remarks>
public sealed class FileLog
{
    public const long DefaultMaxBytes = 5 * 1024 * 1024;

    private readonly string _path;
    private readonly IClock _clock;
    private readonly long _maxBytes;
    private readonly Lock _gate = new();

    public FileLog(string path, IClock clock, long maxBytes = DefaultMaxBytes)
    {
        _path = path;
        _clock = clock;
        _maxBytes = maxBytes;
    }

    public void Info(string message) => Write("INFO", message, null);

    /// <summary>Logs the message with the exception's type names and stack frames, but not its message.</summary>
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        lock (_gate)
        {
            try
            {
                string timestamp = _clock.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                var entry = new StringBuilder();
                entry.Append(timestamp).Append(' ').Append(level).Append(' ').Append(OnOneLine(message));
                if (exception is not null)
                {
                    AppendException(entry, exception);
                }

                entry.Append(Environment.NewLine);

                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RollIfFull();
                File.AppendAllText(_path, entry.ToString());
            }
            catch (Exception)
            {
                // A logger has to be inert. It is called while another failure is being handled,
                // so it must not throw and replace that failure. An entry that cannot be
                // written is dropped.
            }
        }
    }

    private void RollIfFull()
    {
        try
        {
            var file = new FileInfo(_path);
            if (file.Exists && file.Length >= _maxBytes)
            {
                File.Move(_path, _path + ".1", overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another program is holding the old file. Keep appending to the full one rather
            // than lose entries; the roll is tried again on the next write.
        }
    }

    private static void AppendException(StringBuilder entry, Exception exception)
    {
        entry.Append(": ").Append(Describe(exception));
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            entry.Append(" <- ").Append(Describe(inner));
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current != exception)
            {
                entry.Append(Environment.NewLine).Append("    caused by ").Append(current.GetType().FullName);
            }

            foreach (string frame in (current.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                entry.Append(Environment.NewLine).Append("    ").Append(frame);
            }
        }
    }

    // The type plus its error numbers. The numbers say what went wrong (for example Win32 error
    // 5 is "access denied") without carrying any text chosen by whatever threw.
    private static string Describe(Exception exception)
    {
        string description = $"{exception.GetType().FullName} (0x{exception.HResult:X8}";
        if (exception is Win32Exception win32)
        {
            description += $", Win32 {win32.NativeErrorCode}";
        }

        return description + ")";
    }

    // Line breaks in a message would let it pass for the start of a new entry.
    private static string OnOneLine(string message) =>
        string.Join(' ', message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
}
