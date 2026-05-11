// =============================================================================
//  FileLogger — appends JSON-Lines records to a log file. Drop-in
//  ILoggerProvider so MAF's LoggingAgent and the inner agent can both log
//  through it.
//
//  Why custom (vs Serilog/NLog): one teaching moment for the ILogger
//  contract, zero new dependencies. Workshop scope. A real deployment would
//  almost certainly switch to Serilog or OTel logs.
//
//  Output shape (one line per record):
//      {"ts":"2026-05-11T14:32:11.234Z","level":"Information","cat":"Microsoft.Agents.AI.LoggingAgent","msg":"..."}
//
//  Concurrency: a single Console.Out-style lock per writer. Adequate for one
//  REPL process; trivially upgradeable to a channel + background drain if
//  hot-path latency becomes a concern (it won't, for a chat REPL).
// =============================================================================

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ClaudeChat.Observability;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private readonly LogLevel _minLevel;

    public FileLoggerProvider(string path, LogLevel minLevel)
    {
        _minLevel = minLevel;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() => _writer.Dispose();

    internal void Write(string categoryName, LogLevel level, string message)
    {
        if (level < _minLevel) return;
        var record = JsonSerializer.Serialize(new
        {
            ts    = DateTime.UtcNow.ToString("O"),
            level = level.ToString(),
            cat   = categoryName,
            msg   = message,
        });
        lock (_lock) { _writer.WriteLine(record); }
    }

    internal bool IsEnabled(LogLevel level) => level >= _minLevel;

    private sealed class FileLogger(FileLoggerProvider parent, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => parent.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!parent.IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            if (exception is not null) msg += $" | exception: {exception.GetType().Name}: {exception.Message}";
            parent.Write(category, logLevel, msg);
        }
    }
}
