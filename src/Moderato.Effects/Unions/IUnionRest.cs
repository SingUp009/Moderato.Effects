namespace Moderato.Effects;

/// <summary>A union <typeparamref name="TSelf"/> holding every case of <typeparamref name="TSource"/> except <typeparamref name="TCase"/>, generated for <c>[RestOf&lt;E&gt;]</c> unions.</summary>
public interface IUnionRest<TSelf, TSource, TCase> where TSelf : IUnionRest<TSelf, TSource, TCase>
{
    /// <summary>Splits the full union into the case left out, or the rest.</summary>
    /// <param name="rest">The rest, when the union holds any other case.</param>
    /// <returns>True if the union holds the case left out.</returns>
    static abstract bool TrySplit(TSource source, out TCase caught, out TSelf rest);
}
