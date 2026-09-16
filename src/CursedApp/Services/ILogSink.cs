using System.Globalization;

namespace CursedApp.Services;

/// <summary>Minimal sink so services can report progress without pulling in a logging stack.</summary>
public interface ILogSink
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);
}

/// <summary>
/// Writes "yyyy-MM-dd.txt", one file per day, into a "logs" folder next to the
/// settings file, and mirrors everything to the debug output.
///
/// The date is re-evaluated on every write, so a session running past midnight
/// rolls onto the next day's file by itself.
/// </summary>
public sealed class FileLogSink : ILogSink
{
    private readonly Lock _gate = new();

    private DateOnly _currentDay;
    private string? _currentPath;

    public FileLogSink(string directory) => Directory = ResolveWritableDirectory(directory);

    /// <summary>Where log files are actually being written, or null if nowhere is writable.</summary>
    public string? Directory { get; }

    /// <summary>Path of today's log file, for showing the user where to look.</summary>
    public string? CurrentFile => Directory is null
        ? null
        : Path.Combine(Directory, FileNameFor(DateOnly.FromDateTime(DateTime.Now)));

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private static string FileNameFor(DateOnly day) =>
        day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".txt";

    private static string? ResolveWritableDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        try
        {
            System.IO.Directory.CreateDirectory(directory);
            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Logging is a convenience, never a reason to fail startup.
            return null;
        }
    }

    private void Write(string level, string message)
    {
        var now = DateTimeOffset.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);

        if (Directory is null)
            return;

        lock (_gate)
        {
            var today = DateOnly.FromDateTime(now.LocalDateTime);
            if (_currentPath is null || today != _currentDay)
            {
                _currentDay = today;
                _currentPath = Path.Combine(Directory, FileNameFor(today));
            }

            try
            {
                File.AppendAllText(_currentPath, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down.
            }
        }
    }
}
