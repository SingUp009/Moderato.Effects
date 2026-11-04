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

internal class BindInstruction<A, B, C, D, T, E, R, TState> : EffectInstruction<T, E, R>
{
    private protected readonly Effect<A, B, R> source;
    private protected readonly TState state;
    private readonly Func<Outcome<A, B>, TState, RunContext, EffectSelection<C, D, T, E, R>> select;
    private readonly Func<Outcome<C, D>, Outcome<A, B>, RunContext, TState, Outcome<T, E>> project;
    private readonly bool isolate;

    internal BindInstruction(Effect<A, B, R> source, TState state,
        Func<Outcome<A, B>, TState, RunContext, EffectSelection<C, D, T, E, R>> select,
        Func<Outcome<C, D>, Outcome<A, B>, RunContext, TState, Outcome<T, E>> project, bool isolate)
    {
        this.source = source;
        this.state = state;
        this.select = select;
        this.project = project;
        this.isolate = isolate;
    }

    internal override ValueTask<Outcome<T, E>> RunDirect(R environment, RunContext context, int depth)
    {
        if (isolate) return RunIsolated(environment, context, depth);
        var task = source.Run(environment, context, depth);
        return task.IsCompletedSuccessfully ? Continue(task.Result, environment, context, depth) : Await(task, environment, context, depth);
    }

    // Resource acquisition has an async scope even when both acquisitions complete synchronously.
    private async ValueTask<Outcome<T, E>> RunIsolated(R environment, RunContext context, int depth)
    {
        var previous = await source.Run(environment, context, depth).ConfigureAwait(false);
        try
        {
            var selected = select(previous, state, context);
            return selected.HasNext
                ? project(await selected.Next.Run(environment, context, depth).ConfigureAwait(false), previous, context, state)
                : selected.Output;
        }
        catch (Exception exception)
        {
            return Defects.From<T, E>(exception, context).Replacing(previous);
        }
    }

    private async ValueTask<Outcome<T, E>> Await(ValueTask<Outcome<A, B>> task, R environment, RunContext context, int depth)
        => await Continue(await task.ConfigureAwait(false), environment, context, depth).ConfigureAwait(false);

    private ValueTask<Outcome<T, E>> Continue(Outcome<A, B> previous, R environment, RunContext context, int depth)
    {
        try
        {
            var selected = select(previous, state, context);
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
        => isolate ? new IsolatedFrame(this, environment, context, parent) : new Frame(this, environment, context, parent);

    private class Frame(BindInstruction<A, B, C, D, T, E, R, TState> instruction, R environment, RunContext context,
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
                    var selected = instruction.select(previous, instruction.state, context);
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
                if (end.IsAsync && !instruction.isolate) Fault = end.Fault;
                else Output = Defects.From<T, E>(end.Fault.SourceException, context).Replacing(previous);
                return Parent;
            }

            try
            {
                Output = instruction.project(end.Output, previous, context, instruction.state);
            }
            catch (Exception exception)
            {
                if (end.IsAsync && !instruction.isolate) Fault = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
                else Output = Defects.From<T, E>(exception, context).Replacing(previous);
            }

            previous = default;
            return Parent;
        }
    }

    private sealed class IsolatedFrame(BindInstruction<A, B, C, D, T, E, R, TState> instruction, R environment,
        RunContext context, EffectFrame? parent) : Frame(instruction, environment, context, parent)
    {
        private ExecutionScope entry;
        private bool entered;
        private bool suspended;

        internal override EffectFrame? Step()
        {
            if (!entered)
            {
                entry = ExecutionScope.Capture();
                entered = true;
            }

            var next = base.Step();
            if (ReferenceEquals(next, Parent))
            {
                if (!suspended) entry.Restore();
                if (Fault is not null) IsAsync = true;
            }
            return next;
        }

        internal override bool Suspend()
        {
            if (suspended) return false;
            suspended = true;
            // Restore the caller before its own await captures the context.
            entry.Restore();
            return true;
        }
    }
}
