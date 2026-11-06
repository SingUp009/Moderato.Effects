using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Moderato.Effects.Hosting.Tests;

public class HostingTests
{
    [Fact]
    public async Task AddEffectHostedService_SameTypes_RunOnceInSeparateScopes()
    {
        // Given
        var environments = new ConcurrentBag<Environment>();
        Effect<Unit, string, Environment> effect = Effect<Environment>.Access(environment =>
        {
            environments.Add(environment);
            return Unit.Value;
        });
        var builder = Builder();
        builder.Services.AddScoped<Environment>();
        builder.Services.AddEffectHostedService(effect, services => services.GetRequiredService<Environment>(), NoError);
        builder.Services.AddEffectHostedService(effect, services => services.GetRequiredService<Environment>(), NoError);
        using var host = builder.Build();
        Assert.Empty(environments);

        // When
        await host.StartAsync(TestContext.Current.CancellationToken);
        await Task.WhenAll(Services(host).Select(service => service.ExecuteTask!))
            .WaitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(2, environments.Count);
        Assert.Equal(2, environments.Distinct().Count());
        Assert.All(environments, environment => Assert.True(environment.Disposed));
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ErrorHandlerFails_PreservesOriginalError(bool asynchronous)
    {
        // Given
        var environment = new Environment();
        var defect = new IOException("handler");
        Effect<Unit, string, Environment> effect = Effect<Environment>.Fail("original");
        var builder = Builder();
        builder.Services.AddScoped(_ => environment);
        builder.Services.AddEffectHostedService(effect, services => services.GetRequiredService<Environment>(),
            (error, services, token) =>
            {
                Assert.Equal("original", error);
                Assert.Same(environment, services);
                Assert.False(services.Disposed);
                if (!asynchronous) throw defect;
                return FailLater(defect);
            });
        using var host = builder.Build();

        // When
        await host.StartAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<EffectHostedServiceException>(() => Services(host).Single().ExecuteTask!);

        // Then
        Assert.True(environment.Disposed);
        Assert.Same(defect, exception.InnerException);
        var die = Assert.IsType<Die>(exception.Exit.Value);
        var suppressed = Assert.Single(die.Suppressed);
        Assert.Equal(Relation.Sequential, suppressed.Relation);
        Assert.Equal("original", Assert.IsType<Failed>(suppressed.Case.Value).Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_BodyAndScopeBothFail_AggregatesBoth(bool factoryFails)
    {
        // Given
        var body = new IOException("body");
        var cleanup = new IOException("scope");
        var environment = new Environment { DisposalFailure = cleanup };
        var called = false;
        Effect<Unit, string, Environment> effect = Effect<Environment>.Sync<Unit>(() => { called = true; throw body; });
        var builder = Builder();
        builder.Services.AddScoped(_ => environment);
        builder.Services.AddEffectHostedService(effect, services =>
        {
            var value = services.GetRequiredService<Environment>();
            if (factoryFails) throw body;
            return value;
        }, NoError);
        using var host = builder.Build();

        // When
        await host.StartAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<EffectHostedServiceException>(() => Services(host).Single().ExecuteTask!);

        // Then
        Assert.Equal(!factoryFails, called);
        Assert.True(environment.Disposed);
        var die = Assert.IsType<Die>(exception.Exit.Value);
        Assert.Same(body, die.Exception);
        var suppressed = Assert.Single(die.Suppressed);
        Assert.Same(cleanup, Assert.IsType<Die>(suppressed.Case.Value).Exception);
        Assert.Equal(Relation.Sequential, suppressed.Relation);
    }

    [Fact]
    public async Task ExecuteAsync_HandledErrorThenScopeFails_DoesNotReviveError()
    {
        // Given
        var cleanup = new IOException("scope");
        var environment = new Environment { DisposalFailure = cleanup };
        var handled = false;
        Effect<Unit, string, Environment> effect = Effect<Environment>.Fail("handled");
        var builder = Builder();
        builder.Services.AddScoped(_ => environment);
        builder.Services.AddEffectHostedService(effect, services => services.GetRequiredService<Environment>(), (_, value, _) =>
        {
            Assert.False(value.Disposed);
            handled = true;
            return ValueTask.CompletedTask;
        });
        using var host = builder.Build();

        // When
        await host.StartAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<EffectHostedServiceException>(() => Services(host).Single().ExecuteTask!);

        // Then
        Assert.True(handled);
        var die = Assert.IsType<Die>(exception.Exit.Value);
        Assert.Same(cleanup, die.Exception);
        Assert.Empty(die.Suppressed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAsync_ReleaseFails_ReportsDefectEvenForCancellationException(bool cancellationException)
    {
        // Given
        var entered = Signal();
        Exception defect = cancellationException ? new OperationCanceledException("release") : new IOException("release");
        var resource = Resource.Make(Effect<Environment>.Succeed(Unit.Value), (_, _) =>
            Effect<Environment>.Sync<Unit>(() => throw defect));
        Effect<Unit, string, Environment> effect = resource.Use(_ => Effect<Environment>.Async<Unit>(async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Unit.Value;
        }));
        var builder = Builder();
        builder.Services.AddEffectHostedService(effect, _ => new Environment(), NoError);
        using var host = builder.Build();

        // When
        await host.StartAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<EffectHostedServiceException>(() => Services(host).Single().ExecuteTask!);

        // Then
        Assert.Same(defect, exception.InnerException);
        Assert.True(Services(host).Single().ExecuteTask!.IsFaulted);
    }

    [Fact]
    public async Task StopAsync_CooperativeBody_CompletesNormallyAndDisposesScope()
    {
        // Given
        var entered = Signal();
        var environment = new Environment();
        Effect<Unit, string, Environment> effect = Effect<Environment>.Async<Unit>(async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Unit.Value;
        });
        var builder = Builder();
        builder.Services.AddScoped(_ => environment);
        builder.Services.AddEffectHostedService(effect, services => services.GetRequiredService<Environment>(), NoError);
        using var host = builder.Build();

        // When
        await host.StartAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        await Services(host).Single().ExecuteTask!.WaitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(environment.Disposed);
        Assert.True(Services(host).Single().ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExecuteAsync_IndependentInterrupted_FaultsWithoutChangingTheExit()
    {
        // Given
        Fiber<Unit, Never, Environment>? child = null;
        var scope = Scope<Never, Environment>.Gen(async owner =>
        {
            child = await owner.Fork(Effect<Environment>.Async<Unit>(async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Unit.Value;
            }));
            return Unit.Value;
        });
        await scope.RunAsync(new Environment(), TestContext.Current.CancellationToken);
        Effect<Unit, string, Environment> effect = child!.Join();
        var builder = Builder();
        builder.Services.AddEffectHostedService(effect, _ => new Environment(), NoError);
        using var host = builder.Build();

        // When
        await host.StartAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<EffectHostedServiceException>(() => Services(host).Single().ExecuteTask!);

        // Then
        Assert.IsType<Interrupted>(exception.Exit.Value);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void AddEffectHostedService_DefaultEffect_RejectsBeforeResolvingServices()
    {
        // Given
        var builder = Builder();
        var factoryCalled = false;
        Effect<Unit, string, Environment> effect = default;

        // When
        var exception = Assert.Throws<InvalidOperationException>(() => builder.Services.AddEffectHostedService(
            effect, _ => { factoryCalled = true; return new Environment(); }, NoError));

        // Then
        Assert.NotNull(exception);
        Assert.False(factoryCalled);
    }

    [Fact]
    public async Task ExecuteAsync_DefaultStopHostPolicy_PropagatesTheFaultToTheHost()
    {
        // Given
        var defect = new IOException("root");
        var builder = Builder();
        var logs = new DiagnosticsTests.RecordingProvider();
        builder.Logging.AddProvider(logs);
        builder.Services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
        Effect<Unit, string, Environment> effect = Effect<Environment>.Sync<Unit>(() => throw defect);
        builder.Services.AddEffectHostedService(effect, _ => new Environment(), NoError);
        using var host = builder.Build();
        var stopped = Signal();
        using var registration = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => stopped.TrySetResult());

        // When
        await host.StartAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<EffectHostedServiceException>(() => Services(host).Single().ExecuteTask!);
        await stopped.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Record.ExceptionAsync(() => host.StopAsync(TestContext.Current.CancellationToken));

        // Then
        Assert.Same(defect, exception.InnerException);
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, exception));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAsync_ErrorHandlerCompletesOrCancels_ReleasesItsEnvironment(bool cancelHandler)
    {
        // Given
        var entered = Signal();
        var resume = Signal();
        var environment = new Environment();
        var handled = false;
        var builder = Builder();
        builder.Services.AddScoped(_ => environment);
        Effect<Unit, string, Environment> effect = Effect<Environment>.Fail("original");
        builder.Services.AddEffectHostedService(effect, services => services.GetRequiredService<Environment>(), async (error, services, token) =>
        {
            Assert.Equal("original", error);
            Assert.Same(environment, services);
            entered.SetResult();
            if (cancelHandler) await Task.Delay(Timeout.Infinite, token);
            else await resume.Task;
            handled = true;
        });
        using var host = builder.Build();

        // When
        await host.StartAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var stopping = host.StopAsync(TestContext.Current.CancellationToken);
        resume.SetResult();
        await stopping.WaitAsync(TestContext.Current.CancellationToken);
        await Services(host).Single().ExecuteTask!.WaitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(environment.Disposed);
        Assert.Equal(!cancelHandler, handled);
    }

    [Fact]
    public void AddEffectsHosting_NullFactory_RejectsTheConfiguration()
    {
        // Given
        var builder = Builder();

        // When
        var exception = Assert.Throws<ArgumentNullException>(() => builder.Services.AddEffectsHosting(options => options.Diagnostics = null!));

        // Then
        Assert.Equal("factory", exception.ParamName);
    }

    internal static HostApplicationBuilder Builder()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
        return builder;
    }

    internal static BackgroundService[] Services(IHost host) => host.Services.GetServices<IHostedService>().Cast<BackgroundService>().ToArray();
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static ValueTask NoError(string error, Environment environment, CancellationToken token)
        => throw new InvalidOperationException("Unexpected root Err: " + error);

    private static async ValueTask FailLater(Exception exception)
    {
        await Task.Yield();
        throw exception;
    }

    public sealed class Environment : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public Exception? DisposalFailure { get; init; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return DisposalFailure is { } failure ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
        }
    }
}
