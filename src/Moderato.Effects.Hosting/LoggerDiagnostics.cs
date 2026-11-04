using Microsoft.Extensions.Logging;

namespace Moderato.Effects.Hosting;

/// <summary>Reports diagnostics through the Host's standard logging providers.</summary>
public sealed class LoggerDiagnostics : IEffectDiagnostics
{
    private static readonly Action<ILogger, string, int, int, Exception?> defect = LoggerMessage.Define<string, int, int>(
        LogLevel.Error, new EventId(1001, "UnobservedDefect"),
        "Unobserved effect failure {FailureKind}; retained {SuppressedCount}, omitted {OmittedSuppressed}");
    private static readonly Action<ILogger, string, Exception?> error = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1002, "UnjoinedError"), "Unjoined effect error {FailureKind}");
    private static readonly Action<ILogger, int, Relation, string, Exception?> suppressed = LoggerMessage.Define<int, Relation, string>(
        LogLevel.Error, new EventId(1003, "SuppressedFailure"),
        "Suppressed effect failure {SuppressedIndex} with {Relation}: {FailureKind}");
    private readonly ILogger logger;

    /// <summary>Uses the factory without taking ownership of it.</summary>
    /// <param name="loggerFactory">The Host's logger factory.</param>
    public LoggerDiagnostics(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        logger = loggerFactory.CreateLogger("Moderato.Effects.Hosting");
    }

    /// <inheritdoc/>
    public void Report(ExitCase failure)
    {
        if (failure.Value is Failed)
        {
            if (logger.IsEnabled(LogLevel.Warning)) error(logger, "Failed", null);
            return;
        }
        if (!logger.IsEnabled(LogLevel.Error) || failure.Value is not Die die) return;
        defect(logger, "Die", die.Suppressed.Count, die.OmittedSuppressed, die.Exception);
        for (var index = 0; index < die.Suppressed.Count; index++)
        {
            var item = die.Suppressed[index];
            var kind = item.Case.Value switch { Die => "Die", Failed => "Failed", _ => "Interrupted" };
            suppressed(logger, index, item.Relation, kind, (item.Case.Value as Die)?.Exception);
        }
    }
}
