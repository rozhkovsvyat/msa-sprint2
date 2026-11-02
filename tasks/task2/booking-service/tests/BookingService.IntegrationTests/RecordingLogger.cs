using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Hotelio.Bookings.IntegrationTests;

/// <summary>Запоминает сообщения лога, чтобы тест мог дождаться нужного шага.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> messages = new();

    public bool Contains(string text) => messages.Any(m => m.Contains(text, StringComparison.Ordinal));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        messages.Enqueue(formatter(state, exception));
}
