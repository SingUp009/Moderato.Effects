namespace Moderato.Effects;

/// <summary>Creates effects from Gen blocks.</summary>
public static class Effect
{
    /// <summary>An effect that runs the block each time it runs. Write the parameter's type: <c>Effect.Gen(async (Scope&lt;AppError, R&gt; scope) =&gt; ...)</c>.</summary>
    /// <param name="body">The block.</param>
    /// <returns>An effect whose value is what the block returns.</returns>
    public static Effect<T, E, R> Gen<T, E, R>(Func<Scope<E, R>, ValueTask<T>> body) => Scope<E, R>.Gen(body);
}
