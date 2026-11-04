using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moderato.Results;

namespace Moderato.Effects.Hosting;

/// <summary>Registers effects and diagnostics with a Generic Host.</summary>
public static class EffectsHostingServiceCollectionExtensions
{
    /// <summary>Configures a Host-owned diagnostic implementation.</summary>
    /// <param name="services">The Host's services.</param>
    /// <param name="configure">Selects the diagnostic factory; the last registration wins.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddEffectsHosting(this IServiceCollection services, Action<EffectsHostingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new EffectsHostingOptions();
        configure(options);
        var factory = options.Diagnostics;
        ArgumentNullException.ThrowIfNull(factory);
        AddDefaults(services);
        services.Replace(ServiceDescriptor.Singleton<IEffectDiagnostics>(provider => factory(provider)
            ?? throw new InvalidOperationException("The diagnostic factory returned null.")));
        return services;
    }

    /// <summary>Runs one effect with its own asynchronous DI scope and a required typed error handler.</summary>
    /// <param name="services">The Host's services.</param>
    /// <param name="effect">The lazy description to run once.</param>
    /// <param name="environment">Creates its environment from execution-scoped services.</param>
    /// <param name="onError">Handles a root Err before the environment is disposed.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddEffectHostedService<E, R>(this IServiceCollection services,
        Effect<Unit, E, R> effect, Func<IServiceProvider, R> environment,
        Func<E, R, CancellationToken, ValueTask> onError)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(onError);
        var handled = effect.TapErr(error => Effect<R>.AccessAsync<Unit>(async (environment, token) =>
            {
                await onError(error, environment, token).ConfigureAwait(false);
                return Unit.Value;
            }))
            .UnwrapOr(Unit.Value)
            .Provide<AsyncServiceScope>(scope => environment(scope.ServiceProvider));
        AddDefaults(services);
        services.AddSingleton<IHostedService>(provider => new EffectHostedService(handled,
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<DiagnosticsConnection>()));
        return services;
    }

    private static void AddDefaults(IServiceCollection services)
    {
        services.AddLogging();
        services.TryAddSingleton<IEffectDiagnostics>(static provider =>
            new LoggerDiagnostics(provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        services.TryAddSingleton<DiagnosticsConnection>();
    }
}
