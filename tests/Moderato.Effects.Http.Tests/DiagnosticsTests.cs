using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Moderato.Effects.Http.Tests;

[CollectionDefinition("HTTP global diagnostics", DisableParallelization = true)]
public class GlobalDiagnosticsCollection;

[Collection("HTTP global diagnostics")]
public class DiagnosticsTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task RunAsync_UnjoinedError_UsesStandardLoggingWithoutChangingResult(bool enabled, bool throws)
    {
        // Given
        var provider = new RecordingProvider { Enabled = enabled, Throws = throws };
        using var services = Services(provider);
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
        var global = EffectDiagnostics.UnobservedFailureHandler;

        // When
        var result = await request.RunAsync(UnjoinedError(), new object());

        // Then
        Assert.Equal(3, Assert.IsType<Ok<int>>(result.Value).Value);
        Assert.Equal(enabled ? 1 : 0, provider.Entries.Count);
        if (enabled)
        {
            var entry = Assert.Single(provider.Entries);
            Assert.Equal("Moderato.Effects.Http", entry.Category);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Equal(nameof(PrivateError), entry.Fields["ErrorType"]);
            Assert.DoesNotContain(entry.Fields.Values, value => value is PrivateError);
        }
        Assert.Same(global, EffectDiagnostics.UnobservedFailureHandler);
    }

    [Fact]
    public async Task RunAsync_RootErrorAndDefect_DoesNotDuplicateLogs()
    {
        // Given
        var provider = new RecordingProvider();
        using var services = Services(provider);
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();

        // When
        var result = await request.RunAsync(Effect<object>.Fail("root"), new object());
        var error = await Record.ExceptionAsync(() => request.RunAsync(Effect<object>.Sync<int>(() => throw new IOException("root")), new object()).AsTask());

        // Then
        Assert.IsType<Err<string>>(result.Value);
        Assert.IsType<EffectHttpException>(error);
        Assert.Empty(provider.Entries);
    }

    [Fact]
    public async Task AddEffectsHttp_TwoProvidersAndSiblingRequests_KeepDiagnosticsIsolated()
    {
        // Given
        var first = new RecordingProvider();
        var second = new RecordingProvider();
        using var a = Services(first);
        using var b = Services(second);
        using var scopeA = a.CreateScope();
        using var scopeB = b.CreateScope();
        using var siblingScope = a.CreateScope();
        var requestA = scopeA.ServiceProvider.GetRequiredService<EffectRequest>();
        var requestB = scopeB.ServiceProvider.GetRequiredService<EffectRequest>();
        var sibling = siblingScope.ServiceProvider.GetRequiredService<EffectRequest>();

        // When
        await Task.WhenAll(requestA.RunAsync(UnjoinedError(), new object()).AsTask(), requestB.RunAsync(UnjoinedError(), new object()).AsTask());
        requestA.Dispose();
        await requestB.RunAsync(UnjoinedError(), new object());
        await sibling.RunAsync(UnjoinedError(), new object());

        // Then
        Assert.Equal(2, first.Entries.Count);
        Assert.Equal(2, second.Entries.Count);
        Assert.NotSame(requestA, requestB);
        Assert.NotSame(requestA, sibling);
        Assert.Same(requestB, scopeB.ServiceProvider.GetRequiredService<EffectRequest>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task RunAsync_DisconnectionDuringCleanup_RoutesLateDiagnosticToGlobalHandler(int disposal)
    {
        // Given
        var provider = new RecordingProvider();
        using var services = Services(provider);
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
        var releasing = LifetimeTests.Signal();
        var resume = LifetimeTests.Signal();
        var failures = new ConcurrentQueue<ExitCase>();
        var previous = EffectDiagnostics.UnobservedFailureHandler;
        EffectDiagnostics.UnobservedFailureHandler = failures.Enqueue;
        var resource = Resource.Make(Effect<object>.Succeed(Unit.Value), (_, _) => Scope<Never, object>.Gen(async execution =>
        {
            releasing.SetResult();
            await resume.Task;
            await execution.Run(UnjoinedError());
            return Unit.Value;
        }));
        try
        {
            // When
            var pending = request.RunAsync(resource.Use(_ => Effect<object>.Succeed(1)), new object()).AsTask();
            await releasing.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (disposal == 0) { request.Dispose(); }
            else if (disposal == 1) { services.Dispose(); }
            else if (disposal == 2) { await Task.WhenAll(Task.Run(request.Dispose, TestContext.Current.CancellationToken), Task.Run(services.Dispose, TestContext.Current.CancellationToken)); }
            else if (disposal == 3) { request.Dispose(); services.Dispose(); }
            else { services.Dispose(); request.Dispose(); }
            resume.SetResult();
            var result = await pending.WaitAsync(TestContext.Current.CancellationToken);

            // Then
            Assert.Equal(1, Assert.IsType<Ok<int>>(result.Value).Value);
            Assert.Empty(provider.Entries);
            Assert.IsType<Failed>(Assert.Single(failures).Value);
            Assert.Equal(disposal != 0, provider.Disposed);
            Assert.Equal(0, provider.AfterDisposalCalls);
        }
        finally
        {
            resume.TrySetResult();
            EffectDiagnostics.UnobservedFailureHandler = previous;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_DisposedRequestAndUnavailableFallback_PreservesResult(bool throws)
    {
        // Given
        var provider = new RecordingProvider();
        using var services = Services(provider);
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
        var previous = EffectDiagnostics.UnobservedFailureHandler;
        EffectDiagnostics.UnobservedFailureHandler = throws ? _ => throw new IOException("fallback") : null;
        var stderr = Console.Error;
        using var output = new StringWriter();
        Console.SetError(output);
        var entered = LifetimeTests.Signal();
        var resume = LifetimeTests.Signal();
        var effect = Scope<Never, object>.Gen(async execution =>
        {
            entered.SetResult();
            await resume.Task;
            return await execution.Run(UnjoinedError());
        });
        try
        {
            // When
            var pending = request.RunAsync(effect, new object()).AsTask();
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            request.Dispose();
            resume.SetResult();
            var result = await pending.WaitAsync(TestContext.Current.CancellationToken);

            // Then
            Assert.Equal(3, Assert.IsType<Ok<int>>(result.Value).Value);
            Assert.Empty(provider.Entries);
            Assert.Contains(throws ? "handler threw" : "late diagnostic", output.ToString());
        }
        finally
        {
            resume.TrySetResult();
            Console.SetError(stderr);
            EffectDiagnostics.UnobservedFailureHandler = previous;
        }
    }

    [Fact]
    public async Task RunAsync_DiagnosticDuringCleanup_KeepsRequestConnectionActive()
    {
        // Given
        var provider = new RecordingProvider();
        using var services = Services(provider);
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
        var resource = Resource.Make(Effect<object>.Succeed(Unit.Value), (_, _) => UnjoinedError().Map(_ => Unit.Value));

        // When
        var result = await request.RunAsync(resource.Use(_ => Effect<object>.Succeed(1)), new object());

        // Then
        Assert.Equal(1, Assert.IsType<Ok<int>>(result.Value).Value);
        Assert.Single(provider.Entries);
        Assert.False(provider.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_SuppressedDefect_UsesEnabledLogger(bool enabled)
    {
        // Given
        var provider = new RecordingProvider { Enabled = enabled };
        using var services = Services(provider);
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
        var original = new IOException("loser");
        var entered = LifetimeTests.Signal();
        Effect<Unit, string, object> loser = Effect<object>.Async<Unit>(async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw original; }
            return Unit.Value;
        });
        var winner = Scope<string, object>.Gen<Unit>(async execution =>
        {
            await entered.Task;
            throw execution.Fail("winner");
        });

        // When
        var result = await request.RunAsync(Effect.RaceFirst(winner, loser), new object());

        // Then
        Assert.Equal("winner", Assert.IsType<Err<string>>(result.Value).Error);
        Assert.Equal(enabled ? 1 : 0, provider.Entries.Count);
        if (enabled)
        {
            var entry = Assert.Single(provider.Entries);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Equal(0, entry.Fields["SuppressedCount"]);
        }
    }

    internal static Effect<int, Never, object> UnjoinedError() => Scope<Never, object>.Gen(async scope =>
    {
        var ended = LifetimeTests.Signal();
        await scope.Fork(Effect<object>.Fail(new PrivateError()).TapErr(_ => ended.TrySetResult()));
        await ended.Task;
        return 3;
    });

    private static ServiceProvider Services(RecordingProvider provider)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<ILoggerProvider>(_ => provider);
        services.AddEffectsHttp();
        var result = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        result.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext();
        return result;
    }

    private sealed class PrivateError
    {
        public override string ToString() => throw new InvalidOperationException("Do not format error values.");
    }

    private sealed record Entry(string Category, LogLevel Level, Dictionary<string, object?> Fields);

    private sealed class RecordingProvider : ILoggerProvider
    {
        internal bool Enabled { get; init; } = true;
        internal bool Throws { get; init; }
        internal bool Disposed { get; private set; }
        private int afterDisposalCalls;
        internal int AfterDisposalCalls => Volatile.Read(ref afterDisposalCalls);
        internal ConcurrentQueue<Entry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void Dispose() => Disposed = true;

        private sealed class Logger(RecordingProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel)
            {
                if (owner.Disposed) Interlocked.Increment(ref owner.afterDisposalCalls);
                return owner.Enabled;
            }
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (owner.Disposed) Interlocked.Increment(ref owner.afterDisposalCalls);
                owner.Entries.Enqueue(new(category, logLevel, ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value)));
                if (owner.Throws) throw new IOException("logger");
            }
        }
    }
}
