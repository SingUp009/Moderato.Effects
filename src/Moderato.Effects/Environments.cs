namespace Moderato.Effects;

/// <summary>An environment providing <typeparamref name="TService"/>, required as <c>where R : IHas&lt;TService&gt;</c>.</summary>
/// <typeparam name="TService">The service.</typeparam>
public interface IHas<TService>
{
    /// <summary>The service.</summary>
    TService Service { get; }
}

/// <summary>Reads services from an environment.</summary>
public static class HasExtensions
{
    /// <summary>Gets a service from the environment.</summary>
    /// <param name="environment">The environment.</param>
    /// <returns>The service.</returns>
    public static TService Resolve<TService>(this IHas<TService> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.Service;
    }
}

/// <summary>An environment that provides nothing, for effects that need nothing.</summary>
public sealed class EmptyEnvironment
{
    private EmptyEnvironment() { }

    /// <summary>The only instance.</summary>
    public static EmptyEnvironment Value { get; } = new();
}

/// <summary>An environment with only the system clock and the shared random number generator.</summary>
public sealed class DefaultEnvironment : IHas<TimeProvider>, IHas<Random>
{
    private DefaultEnvironment() { }

    /// <summary>The only instance.</summary>
    public static DefaultEnvironment Value { get; } = new();

    TimeProvider IHas<TimeProvider>.Service => TimeProvider.System;

    Random IHas<Random>.Service => Random.Shared;
}
