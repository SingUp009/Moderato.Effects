using Microsoft.Extensions.Logging;

namespace Moderato.Effects.Http;

internal sealed class HttpDiagnostics(ILoggerFactory factory) : IDisposable
{
    private static readonly Action<ILogger, string?, Exception?> error = LoggerMessage.Define<string?>(
        LogLevel.Warning, new EventId(1002, "UnjoinedError"), "Unjoined effect error of type {ErrorType}");
    private static readonly Action<ILogger, int, int, Exception?> defect = LoggerMessage.Define<int, int>(
        LogLevel.Error, new EventId(1001, "UnobservedDefect"), "Unobserved defect; retained {SuppressedCount}, omitted {OmittedSuppressed}");
    private static readonly Action<ILogger, int, Relation, string?, Exception?> suppressed = LoggerMessage.Define<int, Relation, string?>(
        LogLevel.Error, new EventId(1003, "SuppressedFailure"), "Suppressed failure {Index} with {Relation}, error type {ErrorType}");
    private readonly object gate = new();
    private ILogger? logger = factory.CreateLogger("Moderato.Effects.Http");

    internal void Report(ExitCase failure)
    {
        lock (gate)
        {
            if (logger is { } current)
            {
                if (failure.Value is Failed failed)
                {
                    if (current.IsEnabled(LogLevel.Warning)) error(current, failed.Error?.GetType().Name, null);
                }
                else if (failure.Value is Die die && current.IsEnabled(LogLevel.Error))
                {
                    defect(current, die.Suppressed.Count, die.OmittedSuppressed, die.Exception);
                    for (var index = 0; index < die.Suppressed.Count; index++)
                    {
                        var item = die.Suppressed[index];
                        suppressed(current, index, item.Relation,
                            (item.Case.Value as Failed)?.Error?.GetType().Name,
                            (item.Case.Value as Die)?.Exception);
                    }
                }
                return;
            }
        }
        Fallback(failure);
    }

    internal static void Fallback(ExitCase failure)
    {
        if (EffectDiagnostics.UnobservedFailureHandler is { } handler)
        {
            handler(failure);
        }
        else
        {
            Console.Error.WriteLine(failure.Value is Die die
                ? $"Moderato.Effects.Http late diagnostic: {die.Exception}"
                : "Moderato.Effects.Http late diagnostic: unjoined error");
        }
    }

    public void Dispose()
    {
        lock (gate) logger = null;
    }
}
