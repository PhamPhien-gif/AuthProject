using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AuthProject.TestSupport;

internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> entries = new();
    public string[] Entries => entries.ToArray();
    public ILogger CreateLogger(string categoryName) => new Recorder(entries, categoryName);
    public void Dispose() { }

    private sealed class Recorder(ConcurrentQueue<string> entries, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => entries.Enqueue(category + ": " + formatter(state, exception) + exception);
    }
}
