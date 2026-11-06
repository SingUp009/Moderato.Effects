using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Moderato.Effects.Hosting;

/// <summary>Configures the diagnostics owned by one Host.</summary>
public sealed class EffectsHostingOptions
{
    /// <summary>Creates the Host-owned diagnostic implementation from root services.</summary>
    /// <remarks>The container disposes the returned implementation. Do not resolve scoped services here.</remarks>
    public Func<IServiceProvider, IEffectDiagnostics> Diagnostics { get; set; }
        = static services => new LoggerDiagnostics(
            services.GetRequiredService<ILoggerFactory>());
}
