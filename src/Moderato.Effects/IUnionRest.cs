namespace Moderato.Effects;

/// <summary>
/// A union <typeparamref name="TSelf"/> that holds every case of <typeparamref name="TSource"/> except <typeparamref name="TCase"/>.
/// <c>[RestOf&lt;E&gt;]</c> unions get this from the source generator.
/// </summary>
/// <typeparam name="TSelf">The rest.</typeparam>
/// <typeparam name="TSource">The full union.</typeparam>
/// <typeparam name="TCase">The case left out.</typeparam>
public interface IUnionRest<TSelf, TSource, TCase> where TSelf : IUnionRest<TSelf, TSource, TCase>
{
    /// <summary>Splits the full union into the case left out, or the rest.</summary>
    /// <param name="source">The full union.</param>
    /// <param name="caught">The case, when the union holds it.</param>
    /// <param name="rest">The rest, when the union holds any other case.</param>
    /// <returns>True if the union holds the case left out.</returns>
    static abstract bool TrySplit(TSource source, out TCase caught, out TSelf rest);
}
