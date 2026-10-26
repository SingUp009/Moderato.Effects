namespace Moderato.Effects;

/// <summary>A union <typeparamref name="TSelf"/> that has <typeparamref name="TCase"/> as a case. <c>[Error]</c> unions get this from the source generator.</summary>
/// <typeparam name="TSelf">The union.</typeparam>
/// <typeparam name="TCase">The case.</typeparam>
public interface IUnionCase<TSelf, TCase> where TSelf : IUnionCase<TSelf, TCase>
{
    /// <summary>Widens a case to the union.</summary>
    /// <param name="value">The case.</param>
    /// <returns>The union.</returns>
    static abstract TSelf From(TCase value);

    /// <summary>Narrows the union to the case.</summary>
    /// <param name="self">The union.</param>
    /// <param name="value">The case, when the union holds it.</param>
    /// <returns>True if the union holds the case.</returns>
    static abstract bool TryGet(TSelf self, out TCase value);
}
