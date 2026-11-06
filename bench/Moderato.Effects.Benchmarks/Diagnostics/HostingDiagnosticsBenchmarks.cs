using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using Moderato.Effects.Hosting;

namespace Moderato.Effects.Benchmarks;

[MemoryDiagnoser]
public class HostingDiagnosticsBenchmarks
{
    [Params(false, true)]
    public bool Enabled;

    private ILoggerFactory factory = null!;
    private LoggerDiagnostics diagnostics = null!;
    private readonly ExitCase defect = new Die(new IOException("root"), [new Suppressed(new Failed("context"), Relation.Sequential)]);
    private readonly ExitCase error = new Failed("child");

    [GlobalSetup]
    public void Setup()
    {
        factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new NoOpProvider(Enabled)));
        diagnostics = new LoggerDiagnostics(factory);
    }

    [Benchmark]
    public void ReportDefect() => diagnostics.Report(defect);

    [Benchmark]
    public void ReportError() => diagnostics.Report(error);

    [GlobalCleanup]
    public void Cleanup() => factory.Dispose();

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
