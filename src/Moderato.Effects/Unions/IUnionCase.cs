namespace Moderato.Effects;

/// <summary>A union <typeparamref name="TSelf"/> with case <typeparamref name="TCase"/>, generated for <c>[Error]</c> unions.</summary>
public interface IUnionCase<TSelf, TCase> where TSelf : IUnionCase<TSelf, TCase>
{
    /// <summary>Widens a case to the union.</summary>
    static abstract TSelf From(TCase value);

    static abstract bool TryGet(TSelf self, out TCase value);
}
