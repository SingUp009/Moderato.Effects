using Microsoft.Extensions.Logging;

namespace Moderato.Effects.Hosting.Tests;

public class LoggingAllocationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Report_PrebuiltFailureAndNoOpProvider_AllocatesNothing(bool enabled, bool defect)
    {
        // Given
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new NoOpProvider(enabled)));
        var diagnostics = new LoggerDiagnostics(factory);
        ExitCase failure = defect
            ? new Die(new IOException("root"), [new Suppressed(new Failed("context"), Relation.Sequential)])
            : new Failed("child");
        for (var i = 0; i < 1024; i++) diagnostics.Report(failure);
        var before = GC.GetAllocatedBytesForCurrentThread();

        // When
        for (var i = 0; i < 1024; i++) diagnostics.Report(failure);

        // Then
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    private sealed class NoOpProvider(bool enabled) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new NoOpLogger(enabled);
        public void Dispose() { }

        private sealed class NoOpLogger(bool enabled) : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => enabled;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
        }
    }
}
