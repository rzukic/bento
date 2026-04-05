using Microsoft.Extensions.Logging;

namespace Repere.Tests;

public sealed class TestLogger<T> : ILogger<T>
{
    public List<TestLogEntry> Entries { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (state is IEnumerable<KeyValuePair<string, object?>> structuredState)
        {
            foreach (var pair in structuredState)
            {
                properties[pair.Key] = pair.Value;
            }
        }

        Entries.Add(new TestLogEntry(logLevel, formatter(state, exception), properties));
    }
}

public sealed record TestLogEntry(
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, object?> Properties);

internal sealed class NullScope : IDisposable
{
    public static NullScope Instance { get; } = new();

    public void Dispose()
    {
    }
}
