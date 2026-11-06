using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Moderato.Effects.Http;

/// <summary>Registers request-scoped effect execution with ASP.NET Core.</summary>
public static class EffectsHttpServiceCollectionExtensions
{
    /// <summary>Connects effects to request cancellation and standard logging.</summary>
    public static IServiceCollection AddEffectsHttp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.TryAddSingleton<HttpDiagnostics>();
        services.TryAddScoped(provider => new EffectRequest(
            provider.GetRequiredService<IHttpContextAccessor>(),
            provider.GetRequiredService<HttpDiagnostics>()));
        return services;
    }
}
