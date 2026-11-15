using Microsoft.Extensions.Logging;

namespace Moderato.Effects.Hosting;

/// <summary>Reports diagnostics through the Host's standard logging providers.</summary>
public sealed class LoggerDiagnostics : IEffectDiagnostics
{
    private static readonly Action<ILogger, string, int, int, Exception?> defect = LoggerMessage.Define<string, int, int>(
        LogLevel.Error, new EventId(1001, "UnobservedDefect"),
        "Unobserved effect failure {FailureKind}; retained {SuppressedCount}, omitted {OmittedSuppressed}");
    private static readonly Action<ILogger, string, string?, Exception?> error = LoggerMessage.Define<string, string?>(
        LogLevel.Warning, new EventId(1002, "UnobservedError"), "Unobserved effect error {FailureKind} of type {ErrorType}");
    private static readonly Action<ILogger, int, Relation, string, string?, Exception?> suppressed = LoggerMessage.Define<int, Relation, string, string?>(
        LogLevel.Error, new EventId(1003, "SuppressedFailure"),
        "Suppressed effect failure {SuppressedIndex} with {Relation}: {FailureKind}, error type {ErrorType}");
    private readonly ILogger logger;

    /// <summary>Uses the factory without taking ownership of it.</summary>
    public LoggerDiagnostics(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        logger = loggerFactory.CreateLogger("Moderato.Effects.Hosting");
    }

    public void Report(ExitCase failure)
    {
        if (failure.Value is Failed failed)
        {
            if (logger.IsEnabled(LogLevel.Warning)) error(logger, "Failed", failed.Error?.GetType().Name, null);
            return;
        }
        if (!logger.IsEnabled(LogLevel.Error) || failure.Value is not Die die) return;
        defect(logger, "Die", die.Suppressed.Count, die.OmittedSuppressed, die.Exception);
        for (var index = 0; index < die.Suppressed.Count; index++)
        {
            var item = die.Suppressed[index];
            var value = item.Case.Value;
            var kind = value switch { Die => "Die", Failed => "Failed", _ => "Interrupted" };
            suppressed(logger, index, item.Relation, kind, (value as Failed)?.Error?.GetType().Name, (value as Die)?.Exception);
        }
    }
}
