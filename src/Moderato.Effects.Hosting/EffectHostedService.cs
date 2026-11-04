using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moderato.Results;

namespace Moderato.Effects.Hosting;

internal sealed class EffectHostedService(
    Effect<Unit, Never, AsyncServiceScope> effect,
    IServiceScopeFactory scopeFactory,
    DiagnosticsConnection diagnostics) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scoped = Resource.Make(
            Effect<EmptyEnvironment>.Sync(scopeFactory.CreateAsyncScope),
            static (scope, _) => Effect<EmptyEnvironment>.Async<Unit>(async _ =>
            {
                await scope.DisposeAsync().ConfigureAwait(false);
                return Unit.Value;
            }));
        var run = scoped.Use(scope => effect.Provide<EmptyEnvironment>(_ => scope));
        var exit = await run.RunExitAsync(EmptyEnvironment.Value, stoppingToken,
            new EffectRunOptions { UnobservedFailureHandler = diagnostics.Report }).ConfigureAwait(false);
        var stopping = stoppingToken.IsCancellationRequested;
        if (exit.TryGetValue(out Die? die)) throw new EffectHostedServiceException(die);
        if (exit.TryGetValue(out Interrupted? interrupted) && !stopping)
            throw new EffectHostedServiceException(interrupted);
    }
}
