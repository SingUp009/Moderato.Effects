using System.Diagnostics;
using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

public readonly partial struct Effect<T, E, R>
{
    private ValueTask<Result<T, E>> RunResultObserved(R environment, RunContext context)
    {
        var task = ObserveRoot(environment, context, "RunAsync");
        return task.IsCompletedSuccessfully ? ToResult(task.Result, context) : Await(task, context);

        static async ValueTask<Result<T, E>> Await(ValueTask<Outcome<T, E>> pending, RunContext context)
            => await ToResult(await pending.ConfigureAwait(false), context).ConfigureAwait(false);
    }

    private ValueTask<Exit<T, E>> RunExitObserved(R environment, RunContext context)
    {
        var task = ObserveRoot(environment, context, "RunExitAsync");
        return task.IsCompletedSuccessfully ? new(ToExit(task.Result, context)) : Await(task, context);

        static async ValueTask<Exit<T, E>> Await(ValueTask<Outcome<T, E>> pending, RunContext context)
            => ToExit(await pending.ConfigureAwait(false), context);
    }

    private ValueTask<Outcome<T, E>> ObserveRoot(R environment, RunContext context, string name)
    {
        var previous = Activity.Current;
        var observation = EffectObservation.BeginRoot(context, name);
        EffectObservation.SetCurrent(observation?.Activity ?? previous);
        try
        {
            // Keep synchronous callbacks on their caller's ExecutionContext.
            var task = context.CancellationToken.IsCancellationRequested
                ? new ValueTask<Outcome<T, E>>(Outcome<T, E>.Interrupted())
                : Start(environment, context.WithObservation(observation));
            if (!task.IsCompletedSuccessfully) return Await(task, observation, context, previous);
            var outcome = task.Result;
            EffectObservation.SetCurrent(previous);
            observation?.Finish(outcome);
            return new(outcome);
        }
        catch (Exception exception)
        {
            var outcome = Defects.From<T, E>(exception, context);
            EffectObservation.SetCurrent(previous);
            observation?.Finish(outcome);
            return new(outcome);
        }
        finally
        {
            EffectObservation.SetCurrent(previous);
        }

        static async ValueTask<Outcome<T, E>> Await(ValueTask<Outcome<T, E>> pending, EffectObservation? observation, RunContext context, Activity? previous)
        {
            Outcome<T, E> outcome;
            try { outcome = await pending.ConfigureAwait(false); }
            catch (Exception exception) { outcome = Defects.From<T, E>(exception, context); }
            EffectObservation.SetCurrent(previous);
            observation?.Finish(outcome);
            return outcome;
        }
    }
}
