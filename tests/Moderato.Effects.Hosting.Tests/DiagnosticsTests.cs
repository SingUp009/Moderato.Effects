using System.Collections;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Moderato.Effects.Hosting.Tests;

public class DiagnosticsTests
{
    [Fact]
    public async Task AddEffectsHosting_TwoHosts_RouteDiagnosticsToTheirOwnImplementations()
    {
        // Given
        var first = new SpyDiagnostics();
        var second = new SpyDiagnostics();
        var before = EffectDiagnostics.UnobservedFailureHandler;
        var effect = UnjoinedError();
        var a = HostingTests.Builder();
        a.Services.AddEffectsHosting(options => options.Diagnostics = _ => first);
        a.Services.AddEffectHostedService(effect, _ => new HostingTests.Environment(), HostingTests.NoError);
        var b = HostingTests.Builder();
        b.Services.AddEffectsHosting(options => options.Diagnostics = _ => second);
        b.Services.AddEffectHostedService(effect, _ => new HostingTests.Environment(), HostingTests.NoError);
        using var hostA = a.Build();
        using var hostB = b.Build();

        // When
        await Task.WhenAll(hostA.StartAsync(TestContext.Current.CancellationToken), hostB.StartAsync(TestContext.Current.CancellationToken));
        await Task.WhenAll(HostingTests.Services(hostA).Concat(HostingTests.Services(hostB)).Select(service => service.ExecuteTask!))
            .WaitAsync(TestContext.Current.CancellationToken);
        hostA.Dispose();

        // Then
        Assert.IsType<Failed>(Assert.Single(first.Failures).Value);
        Assert.IsType<Failed>(Assert.Single(second.Failures).Value);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(0, second.Disposals);
        Assert.Same(before, EffectDiagnostics.UnobservedFailureHandler);
    }

    [Fact]
    public async Task ExecuteAsync_DiagnosticImplementationThrows_DoesNotChangeResult()
    {
        // Given
        var diagnostics = new SpyDiagnostics { ThrowOnReport = true };
        var builder = HostingTests.Builder();
        builder.Services.AddEffectsHosting(options => options.Diagnostics = _ => diagnostics);
        builder.Services.AddEffectHostedService(UnjoinedError(), _ => new HostingTests.Environment(), HostingTests.NoError);
        using var host = builder.Build();

        // When
        await host.StartAsync(TestContext.Current.CancellationToken);
        await HostingTests.Services(host).Single().ExecuteTask!.WaitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(HostingTests.Services(host).Single().ExecuteTask!.IsCompletedSuccessfully);
        Assert.Single(diagnostics.Failures);
    }

    [Fact]
    public void Report_Defect_EmitsNamedFieldsAndOrderedContext()
    {
        // Given
        var provider = new RecordingProvider();
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        var diagnostics = new LoggerDiagnostics(factory);
        var defect = new IOException("root");
        var child = new IOException("child");
        var failure = new Die(defect,
        [
            new Suppressed(new Failed(new SecretError()), Relation.Sequential),
            new Suppressed(new Die(child, []), Relation.Consequence),
        ])
        { OmittedSuppressed = 3 };

        // When
        diagnostics.Report(failure);

        // Then
        var entries = provider.Entries.ToArray();
        Assert.Equal(3, entries.Length);
        Assert.Same(defect, entries[0].Exception);
        Assert.Equal("Die", entries[0].Fields["FailureKind"]);
        Assert.Equal(2, entries[0].Fields["SuppressedCount"]);
        Assert.Equal(3, entries[0].Fields["OmittedSuppressed"]);
        Assert.Equal(0, entries[1].Fields["SuppressedIndex"]);
        Assert.Equal(Relation.Sequential, entries[1].Fields["Relation"]);
        Assert.Equal(1, entries[2].Fields["SuppressedIndex"]);
        Assert.Equal(Relation.Consequence, entries[2].Fields["Relation"]);
        Assert.Same(child, entries[2].Exception);
    }

    [Fact]
    public void Report_Disabled_DoesNotInspectSuppressedContextOrFormatErrors()
    {
        // Given
        var provider = new RecordingProvider { Enabled = false };
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        var diagnostics = new LoggerDiagnostics(factory);
        var context = new UnreadableContext();

        // When
        diagnostics.Report(new Die(new IOException("root"), context));
        diagnostics.Report(new Failed(new SecretError()));

        // Then
        Assert.Empty(provider.Entries);
        Assert.Equal(0, context.Reads);
    }

    [Fact]
    public void Report_Error_DoesNotExposeArbitraryErrorValues()
    {
        // Given
        var provider = new RecordingProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var diagnostics = new LoggerDiagnostics(factory);

        // When
        diagnostics.Report(new Failed(new SecretError()));

        // Then
        var entry = Assert.Single(provider.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.DoesNotContain(entry.Fields.Values, value => value is SecretError);
    }

    internal static Effect<Unit, string, HostingTests.Environment> UnjoinedError()
        => Scope<string, HostingTests.Environment>.Gen(async scope =>
        {
            var ended = HostingTests.Signal();
            await scope.Fork(Effect<HostingTests.Environment>.Fail("child").TapErr(_ => ended.TrySetResult()));
            await ended.Task;
            return Unit.Value;
        });

    internal sealed class SpyDiagnostics : IEffectDiagnostics, IDisposable
    {
        public ConcurrentQueue<ExitCase> Failures { get; } = new();
        public bool ThrowOnReport { get; init; }
        public int Disposals { get; private set; }
        public void Report(ExitCase failure)
        {
            Assert.Equal(0, Disposals);
            Failures.Enqueue(failure);
            if (ThrowOnReport) throw new IOException("diagnostic implementation");
        }
        public void Dispose() => Disposals++;
    }

    internal sealed record Entry(LogLevel Level, Exception? Exception, Dictionary<string, object?> Fields);

    internal sealed class RecordingProvider : ILoggerProvider
    {
        internal ConcurrentQueue<Entry> Entries { get; } = new();
        internal bool Enabled { get; init; } = true;
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);
        public void Dispose() { }

        private sealed class RecordingLogger(RecordingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => owner.Enabled;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner.Entries.Enqueue(new Entry(logLevel, exception,
                    ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value)));
        }
    }

    private sealed class SecretError
    {
        public override string ToString() => throw new InvalidOperationException("Do not format this error.");
    }

    private sealed class UnreadableContext : IReadOnlyList<Suppressed>
    {
        public int Reads { get; private set; }
        public int Count { get { Reads++; throw new InvalidOperationException("Do not inspect disabled logs."); } }
        public Suppressed this[int index] => throw new NotSupportedException();
        public IEnumerator<Suppressed> GetEnumerator() => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
