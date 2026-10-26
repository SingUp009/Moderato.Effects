namespace Moderato.Effects;

/// <summary>Makes a <c>partial union</c> an error union whose cases, nested ones included, get <see cref="IUnionCase{TSelf, TCase}"/>.</summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class ErrorAttribute : Attribute;

/// <summary>Makes a partial type an environment providing <see cref="IHas{TService}"/> for each primary constructor parameter.</summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class EnvironmentAttribute : Attribute;

/// <summary>Provides an <see cref="EnvironmentAttribute"/> parameter as <typeparamref name="T"/> instead of as its own type.</summary>
/// <typeparam name="T">The service type to provide.</typeparam>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = true)]
public sealed class ExposeAttribute<T> : Attribute;

/// <summary>Declares this <c>partial union</c> as <typeparamref name="E"/> minus one case, which <c>Catch</c> can then remove.</summary>
/// <typeparam name="E">The union this one is the rest of.</typeparam>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class RestOfAttribute<E> : Attribute;
