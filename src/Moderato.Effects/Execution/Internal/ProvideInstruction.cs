namespace Moderato.Effects.Internal;

internal sealed class ProvideInstruction<T, E, Inner, Outer>(Effect<T, E, Inner> source, Func<Outer, Inner> build)
    : EffectInstruction<T, E, Outer>
{
    internal override ValueTask<Outcome<T, E>> RunDirect(Outer environment, RunContext context, int depth)
    {
        Inner inner;
        try
        {
            inner = build(environment);
        }
        catch (Exception exception)
        {
            return new(Defects.From<T, E>(exception, context));
        }

        return source.Run(inner, context, depth);
    }

    private EffectFrame<T, E> EnterSource(Outer environment, RunContext context, EffectFrame? parent)
        => source.Enter(build(environment), context, parent);

    internal override EffectFrame<T, E> Enter(Outer environment, RunContext context, EffectFrame? parent)
        => new Frame(this, environment, context, parent);

    private sealed class Frame(ProvideInstruction<T, E, Inner, Outer> instruction, Outer environment, RunContext context,
        EffectFrame? parent) : EffectFrame<T, E>(parent)
    {
        private EffectFrame<T, E>? input;

        internal override EffectFrame? Step()
        {
            if (input is null)
            {
                try
                {
                    input = instruction.EnterSource(environment, context, this);
                    return input;
                }
                catch (Exception exception)
                {
                    Output = Defects.From<T, E>(exception, context);
                    return Parent;
                }
            }

            Output = input.Output;
            Fault = input.Fault;
            input = null;
            return Parent;
        }
    }
}
