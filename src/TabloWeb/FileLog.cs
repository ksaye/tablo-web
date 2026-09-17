using System.Collections.Concurrent;
using System.Text;

namespace TabloWeb;

/// <summary>
/// Writes the log to a file as well as the console.
///
/// A Windows service has no console: everything the server says — why a multi-view stopped, which
/// ffmpeg it found, a failed sign-in — went nowhere, which left a Windows install with nothing to
/// look at when something went wrong. So on Windows the log is also kept in `logs` beside the
/// program, one file a day, the last week retained. Linux and Docker installs already have
/// journald and `docker logs`, so there it is off unless TABLOWEB_LOG_DIR names a folder.
/// </summary>
public sealed class FileLogProvider : ILoggerProvider
{
    private const int KeepDays = 7;

    // No byte-order mark: the file is read by people and by `type`/`tail`, not by a parser.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _dir;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private DateOnly _day = DateOnly.MinValue;

    private FileLogProvider(string dir) => _dir = dir;

    /// <summary>Where the log is kept, or null when nothing should be written to file.</summary>
    public static string? Directory
    {
        get
        {
            if (Environment.GetEnvironmentVariable("TABLOWEB_LOG_DIR") is { Length: > 0 } configured)
                return configured;
            if (Environment.GetEnvironmentVariable("TABLOWEB_LOG") is "0" or "false") return null;
            return OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "logs") : null;
        }
    }

    /// <summary>Add file logging, unless it is turned off or the folder cannot be written to.</summary>
    public static void AddTo(ILoggingBuilder logging)
    {
        if (Directory is not { } dir) return;
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".writable");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch
        {
            // Read-only install folder, say: the console log is all there is, which is what a
            // Windows install had before. Never let logging stop the server from starting.
            return;
        }
        logging.AddProvider(new FileLogProvider(dir));
    }

    public ILogger CreateLogger(string category) =>
        _loggers.GetOrAdd(category, c => new FileLogger(this, c));

    internal void Write(string line)
    {
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (today != _day)
            {
                _day = today;
                Prune();
            }
            try { File.AppendAllText(FileFor(today), line + Environment.NewLine, Utf8); }
            catch { /* a full disk must not take the server down */ }
        }
    }

    private string FileFor(DateOnly day) => Path.Combine(_dir, $"tabloweb-{day:yyyy-MM-dd}.log");

    private void Prune()
    {
        try
        {
            var keepFrom = DateTime.Now.AddDays(-KeepDays);
            foreach (var file in System.IO.Directory.GetFiles(_dir, "tabloweb-*.log"))
                if (File.GetLastWriteTime(file) < keepFrom)
                    File.Delete(file);
        }
        catch { /* tidying is best effort */ }
    }

    public void Dispose() => _loggers.Clear();

    private sealed class FileLogger(FileLogProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level != LogLevel.None;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
            Func<TState, Exception?, string> format)
        {
            if (!IsEnabled(level)) return;
            var text = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss "))
                .Append(Short(level)).Append(": ").Append(category).Append("[").Append(id.Id).Append("] ")
                .Append(format(state, error));
            if (error is not null) text.Append(' ').Append(error);
            owner.Write(text.ToString());
        }

        // The same short names the console log uses, so both read alike.
        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none"
        };
    }
}
