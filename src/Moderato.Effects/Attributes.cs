namespace Moderato.Effects;

/// <summary>
/// Makes a <c>partial union</c> an error union: the source generator implements <see cref="IUnionCase{TSelf, TCase}"/> for each case,
/// including the cases of cases that are unions themselves.
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class ErrorAttribute : Attribute;

/// <summary>
/// Makes a partial type with a primary constructor an environment: the source generator implements <see cref="IHas{TService}"/>
/// for each constructor parameter.
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class EnvironmentAttribute : Attribute;

/// <summary>Provides an <see cref="EnvironmentAttribute"/> parameter as <typeparamref name="T"/> instead of as its own type.</summary>
/// <typeparam name="T">The service type to provide.</typeparam>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = true)]
public sealed class ExposeAttribute<T> : Attribute;

/// <summary>
/// Declares this <c>partial union</c> as <typeparamref name="E"/> without one of its cases, so <c>Catch</c> can remove that case
/// from the error type. The source generator implements <see cref="IUnionRest{TSelf, TSource, TCase}"/>.
/// </summary>
/// <typeparam name="E">The union this one is the rest of.</typeparam>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class RestOfAttribute<E> : Attribute;
