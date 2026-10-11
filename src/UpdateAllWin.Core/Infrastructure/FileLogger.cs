using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace UpdateAllWin.Core.Infrastructure;

/// <summary>
/// Minimal structured file logger: one file per day, background writer, retention cleanup.
/// Lines are "timestamp [level] category: message" with scope/state rendered by the template.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _folder;
    private readonly string _prefix;
    private readonly LogLevel _minimum;
    private readonly BlockingCollection<string> _queue = new(10_000);
    private readonly Thread _writer;

    public FileLoggerProvider(string folder, string prefix, LogLevel minimum = LogLevel.Information, int retentionDays = 30)
    {
        _folder = folder;
        _prefix = prefix;
        _minimum = minimum;
        Directory.CreateDirectory(folder);
        Cleanup(retentionDays);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "FileLogger" };
        _writer.Start();
    }

    public string CurrentFile => Path.Combine(_folder, $"{_prefix}-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortName(categoryName));

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(3));
    }

    internal bool IsEnabled(LogLevel level) => level >= _minimum && level != LogLevel.None;

    internal void Enqueue(string line) => _queue.TryAdd(line);

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                var sb = new StringBuilder(line).AppendLine();
                while (_queue.TryTake(out var more)) sb.AppendLine(more);
                File.AppendAllText(CurrentFile, sb.ToString());
            }
            catch
            {
                // Never let logging take the process down.
            }
        }
    }

    private void Cleanup(int retentionDays)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_folder, $"{_prefix}-*.log"))
                if (DateTime.Now - File.GetLastWriteTime(file) > TimeSpan.FromDays(Math.Max(1, retentionDays)))
                    File.Delete(file);
        }
        catch { }
    }

    private static string ShortName(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                _ => "CRT",
            };
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            provider.Enqueue(line);
        }
    }
}
