using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Moderato.Effects.Hosting.Tests;

[CollectionDefinition("Global hosting diagnostics", DisableParallelization = true)]
public class GlobalHostingDiagnosticsCollection;

[Collection("Global hosting diagnostics")]
public class LateDiagnosticsTests
{
    [Fact]
    public async Task StopAsync_BudgetExpires_KeepsScopeAliveAndDisconnectsDisposedDiagnostics()
    {
        // Given
        var entered = HostingTests.Signal();
        var releasing = HostingTests.Signal();
        var resume = HostingTests.Signal();
        var environment = new HostingTests.Environment();
        var diagnostics = new DiagnosticsTests.SpyDiagnostics();
        var fallback = new ConcurrentQueue<ExitCase>();
        var previous = EffectDiagnostics.UnobservedFailureHandler;
        EffectDiagnostics.UnobservedFailureHandler = fallback.Enqueue;
        var builder = HostingTests.Builder();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(100));
        builder.Services.AddScoped(_ => environment);
        builder.Services.AddEffectsHosting(options => options.Diagnostics = _ => diagnostics);
        var resource = Resource.Make(Effect<HostingTests.Environment>.Succeed(Unit.Value), (_, _) =>
            Scope<Never, HostingTests.Environment>.Gen(async scope =>
            {
                releasing.SetResult();
                await resume.Task;
                var ended = HostingTests.Signal();
                await scope.Fork(Effect<HostingTests.Environment>.Fail("late child").TapErr(_ => ended.TrySetResult()));
                await ended.Task;
                return Unit.Value;
            }));
        Effect<Unit, string, HostingTests.Environment> effect = resource.Use(_ => Effect<HostingTests.Environment>.Async<Unit>(async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Unit.Value;
        }));
        builder.Services.AddEffectHostedService(effect, services => services.GetRequiredService<HostingTests.Environment>(), HostingTests.NoError);
        using var host = builder.Build();
        try
        {
            // When
            await host.StartAsync(TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var execution = HostingTests.Services(host).Single().ExecuteTask!;
            var stopping = host.StopAsync(TestContext.Current.CancellationToken);
            await releasing.Task.WaitAsync(TestContext.Current.CancellationToken);
            await stopping.WaitAsync(TestContext.Current.CancellationToken);
            var wasIncomplete = !execution.IsCompleted;
            var scopeWasAlive = !environment.Disposed;
            host.Dispose();
            var diagnosticsDisposed = diagnostics.Disposals;
            resume.SetResult();
            await execution.WaitAsync(TestContext.Current.CancellationToken);

            // Then
            Assert.True(wasIncomplete);
            Assert.True(scopeWasAlive);
            Assert.Equal(1, diagnosticsDisposed);
            Assert.True(environment.Disposed);
            Assert.Empty(diagnostics.Failures);
            Assert.IsType<Failed>(Assert.Single(fallback).Value);
        }
        finally
        {
            resume.TrySetResult();
            EffectDiagnostics.UnobservedFailureHandler = previous;
        }
    }
}
