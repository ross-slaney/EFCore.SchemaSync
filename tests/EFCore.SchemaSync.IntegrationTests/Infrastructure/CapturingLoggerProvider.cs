using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace EFCore.SchemaSync.IntegrationTests.Infrastructure;

public sealed record LogEntry(LogLevel Level, string Category, string Message, Exception? Exception);

/// <summary>Collects everything the library logs so tests can assert on progress messages and on what is never logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public IEnumerable<LogEntry> Library => Entries.Where(e => e.Category == SchemaSync.LoggerCategory);

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => entries.Enqueue(new LogEntry(logLevel, category, formatter(state, exception), exception));
    }
}
