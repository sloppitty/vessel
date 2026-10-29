using Microsoft.Extensions.Logging;

namespace Vessel.Tests;

internal sealed class BackgroundWorkLogs : ILoggerProvider
{
    private readonly List<(string Category, LogLevel Level, string Text)> messages = [];
    public IReadOnlyList<(string Category, LogLevel Level, string Text)> Messages { get { lock (messages) return messages.ToArray(); } }
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    private sealed class Logger(BackgroundWorkLogs logs, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (logs.messages) logs.messages.Add((category, logLevel, formatter(state, exception)));
        }
    }
}
