using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Vessel.Tests;

public partial class StartupWarmupTestsSupport
{

    internal sealed record LogEntry(EventId Id, string Message, Exception? Exception);

    internal sealed class Logs : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> entries = new();
        public IReadOnlyList<LogEntry> Entries => [.. entries];
        public ILogger CreateLogger(string categoryName) => new Logger(entries);
        public void Dispose() { }

        private sealed class Logger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => entries.Enqueue(new(eventId, formatter(state, exception), exception));
        }
    }
}
