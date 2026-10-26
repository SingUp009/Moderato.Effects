using Moderato.Effects;

public static class Misuse<R>
{
    // MEFF301: the catch-all swallows ScopeFailure.
    public static Effect<int, string, R> Swallow() => Scope<string, R>.Gen(async scope =>
    {
        try
        {
            return await scope.Run(Effect<R>.Succeed(1));
        }
        catch (Exception)
        {
            return 0;
        }
    });
}
