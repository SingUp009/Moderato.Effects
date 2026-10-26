namespace Moderato.Effects;

/// <summary>Creates effects whose environment type is inferred.</summary>
public static partial class Effect
{
    /// <summary>An effect that runs the block on each run; type the parameter, as in <c>Effect.Gen(async (Scope&lt;AppError, R&gt; scope) =&gt; ...)</c>.</summary>
    /// <param name="body">The block.</param>
    /// <returns>An effect whose value is what the block returns.</returns>
    public static Effect<T, E, R> Gen<T, E, R>(Func<Scope<E, R>, ValueTask<T>> body) => Scope<E, R>.Gen(body);
}
