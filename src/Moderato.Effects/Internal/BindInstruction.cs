namespace Moderato.Effects.Internal;

internal readonly struct EffectSelection<C, D, T, E, R>
{
    internal Effect<C, D, R> Next { get; }
    internal Outcome<T, E> Output { get; }
    internal bool HasNext { get; }

    internal EffectSelection(Effect<C, D, R> next)
    {
        Next = next;
        HasNext = true;
    }

    internal EffectSelection(Outcome<T, E> output) => Output = output;
}

internal sealed class BindInstruction<A, B, C, D, T, E, R, TState> : EffectInstruction<T, E, R>
{
    private readonly Effect<A, B, R> source;
    private readonly TState state;
    private readonly Func<Outcome<A, B>, TState, EffectSelection<C, D, T, E, R>> select;
    private readonly Func<Outcome<C, D>, Outcome<A, B>, RunContext, TState, Outcome<T, E>> project;

    internal BindInstruction(Effect<A, B, R> source, TState state,
        Func<Outcome<A, B>, TState, EffectSelection<C, D, T, E, R>> select,
        Func<Outcome<C, D>, Outcome<A, B>, RunContext, TState, Outcome<T, E>> project)
    {
        this.source = source;
        this.state = state;
        this.select = select;
        this.project = project;
    }

    internal override ValueTask<Outcome<T, E>> RunDirect(R environment, RunContext context, int depth)
    {
        var task = source.Run(environment, context, depth);
        return task.IsCompletedSuccessfully ? Continue(task.Result, environment, context, depth) : Await(task, environment, context, depth);
    }

    private async ValueTask<Outcome<T, E>> Await(ValueTask<Outcome<A, B>> task, R environment, RunContext context, int depth)
        => await Continue(await task.ConfigureAwait(false), environment, context, depth).ConfigureAwait(false);

    private ValueTask<Outcome<T, E>> Continue(Outcome<A, B> previous, R environment, RunContext context, int depth)
    {
        try
        {
            var selected = select(previous, state);
            return selected.HasNext
                ? Steps.Then(selected.Next.Run(environment, context, depth), (previous, context, instruction: this),
                    static (end, input) => input.instruction.project(end, input.previous, input.context, input.instruction.state))
                : new(selected.Output);
        }
        catch (Exception exception)
        {
            return new(Defects.From<T, E>(exception, context).Replacing(previous));
        }
    }

    internal override EffectFrame<T, E> Enter(R environment, RunContext context, EffectFrame? parent)
        => new Frame(this, environment, context, parent);

    private sealed class Frame(BindInstruction<A, B, C, D, T, E, R, TState> instruction, R environment, RunContext context,
        EffectFrame? parent) : EffectFrame<T, E>(parent)
    {
        private EffectFrame<A, B>? first;
        private EffectFrame<C, D>? second;
        private Outcome<A, B> previous;
        private bool started;
        internal override bool CapturesContext => first is not null || second is not null;

        internal override EffectFrame? Step()
        {
            if (!started)
            {
                started = true;
                first = instruction.source.Enter(environment, context, this);
                return first;
            }

            if (first is not null)
            {
                var input = first;
                first = null;
                if (input.Fault is not null)
                {
                    Fault = input.Fault;
                    return Parent;
                }

                previous = input.Output;
                try
                {
                    var selected = instruction.select(previous, instruction.state);
                    if (!selected.HasNext)
                    {
                        Output = selected.Output;
                        return Parent;
                    }

                    second = selected.Next.Enter(environment, context, this);
                    return second;
                }
                catch (Exception exception)
                {
                    Output = Defects.From<T, E>(exception, context).Replacing(previous);
                    return Parent;
                }
            }

            var end = second!;
            second = null;
            if (end.Fault is not null)
            {
                if (end.IsAsync) Fault = end.Fault;
                else Output = Defects.From<T, E>(end.Fault.SourceException, context).Replacing(previous);
                return Parent;
            }

            try
            {
                Output = instruction.project(end.Output, previous, context, instruction.state);
            }
            catch (Exception exception)
            {
                if (end.IsAsync) Fault = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
                else Output = Defects.From<T, E>(exception, context).Replacing(previous);
            }

            previous = default;
            return Parent;
        }
    }
}
