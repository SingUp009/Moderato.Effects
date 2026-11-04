namespace Moderato.Effects.Internal;

internal sealed class AndThenInstruction<A, T, E, R>(Effect<A, E, R> input, Func<A, Effect<T, E, R>> binder)
    : BindInstruction<A, E, T, E, T, E, R, Func<A, Effect<T, E, R>>>(input, binder,
        static (previous, bind, _) => previous.Kind == OutcomeKind.Ok ? new(bind(previous.Value)) : new(previous.WithValueType<T>()),
        static (next, previous, _, _) => next.After(previous), isolate: false)
{
    internal override ValueTask<Outcome<T, E>> RunDirect(R environment, RunContext context, int depth)
    {
        var task = source.Run(environment, context, depth);
        return task.IsCompletedSuccessfully ? Continue(task.Result, environment, context, depth) : Await(task, environment, context, depth);
    }

    private async ValueTask<Outcome<T, E>> Await(ValueTask<Outcome<A, E>> task, R environment, RunContext context, int depth)
        => await Continue(await task.ConfigureAwait(false), environment, context, depth).ConfigureAwait(false);

    // The common value bind avoids copying the generic selection's unused terminal outcome.
    private ValueTask<Outcome<T, E>> Continue(Outcome<A, E> previous, R environment, RunContext context, int depth)
    {
        if (previous.Kind != OutcomeKind.Ok) return new(previous.WithValueType<T>());
        try
        {
            return Steps.Then(state(previous.Value).Run(environment, context, depth), previous,
                static (next, earlier) => next.After(earlier));
        }
        catch (Exception exception)
        {
            return new(Defects.From<T, E>(exception, context).Replacing(previous));
        }
    }
}
