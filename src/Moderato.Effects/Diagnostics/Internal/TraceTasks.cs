namespace Moderato.Effects.Internal;

internal static class TraceTasks
{
    internal static ValueTask<Outcome<T, E>> Observe<T, E>(ValueTask<Outcome<T, E>> task, EffectObservation? node)
    {
        if (node is null) return task;
        if (!task.IsCompletedSuccessfully) return Await(task, node);
        var outcome = task.Result;
        node.Finish(outcome);
        return new(outcome);
    }

    private static async ValueTask<Outcome<T, E>> Await<T, E>(ValueTask<Outcome<T, E>> task, EffectObservation node)
    {
        try
        {
            var outcome = await task.ConfigureAwait(false);
            node.Finish(outcome);
            return outcome;
        }
        catch (Exception exception)
        {
            node.Fault(exception);
            throw;
        }
    }
}

internal sealed class TrackingFrame<T, E, R>(Effect<T, E, R> effect, R environment, RunContext context, EffectFrame? parent)
    : EffectFrame<T, E>(parent)
{
    private EffectFrame<T, E>? input;
    private EffectObservation? node;

    internal override EffectFrame? Step()
    {
        if (input is null)
        {
            node = effect.BeginTracking(context);
            input = effect.EnterCore(environment, context.WithObservation(node), this);
            return input;
        }
        Output = input.Output;
        Fault = input.Fault;
        if (node is not null)
        {
            if (Fault is not null) node.Fault(Fault.SourceException);
            else node.Finish(Output);
        }
        return Parent;
    }
}
