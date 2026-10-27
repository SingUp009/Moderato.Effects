namespace Moderato.Effects.Internal;

/// <summary>Reads a service through a constrained call, without boxing a value-type environment.</summary>
internal static class Services
{
    internal static TService Resolve<TService, R>(R environment) where R : IHas<TService>
    {
        if (!typeof(R).IsValueType && environment is null) throw new ArgumentNullException(nameof(environment));
        return environment.Service;
    }
}
