namespace Moderato.Effects.Internal;

internal sealed class ThenInstruction<A, B, T, E, R, TState>(Effect<A, B, R> source, TState state,
    Func<Outcome<A, B>, R, RunContext, TState, ValueTask<Outcome<T, E>>> next) : EffectInstruction<T, E, R>
{
    private EffectFrame<A, B> EnterSource(R environment, RunContext context, EffectFrame parent)
        => source.Enter(environment, context, parent);

    internal override ValueTask<Outcome<T, E>> RunDirect(R environment, RunContext context, int depth)
    {
        var task = source.Run(environment, context, depth);
        return task.IsCompletedSuccessfully ? Continue(task.Result, environment, context) : Await(task, environment, context);
    }

    private async ValueTask<Outcome<T, E>> Await(ValueTask<Outcome<A, B>> task, R environment, RunContext context)
        => await Continue(await task.ConfigureAwait(false), environment, context).ConfigureAwait(false);

    private ValueTask<Outcome<T, E>> Continue(Outcome<A, B> outcome, R environment, RunContext context)
    {
        try
        {
            return next(outcome, environment, context, state);
        }
        catch (Exception exception)
        {
            return new(Defects.From<T, E>(exception, context).Replacing(outcome));
        }
    }

    internal override EffectFrame<T, E> Enter(R environment, RunContext context, EffectFrame? parent)
        => new Frame(this, environment, context, parent);

    private sealed class Frame(ThenInstruction<A, B, T, E, R, TState> instruction, R environment, RunContext context,
        EffectFrame? parent) : AwaitingEffectFrame<T, E>(parent)
    {
        private EffectFrame<A, B>? input;
        internal override bool CapturesContext => input is not null;

        internal override EffectFrame? Step()
        {
            if (input is null)
            {
                input = instruction.EnterSource(environment, context, this);
                return input;
            }

            var previous = input;
            input = null;
            if (previous.Fault is not null)
            {
                Fault = previous.Fault;
                return Parent;
            }

            return Complete(instruction.Continue(previous.Output, environment, context));
        }
    }
}
