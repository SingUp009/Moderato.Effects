using Moderato.Results;

namespace Moderato.Effects.Testing;

// Built outside the measured region, with callbacks that allocate nothing themselves.
public enum AllocationCase
{
    Sync, CompletedAsync, Access, AccessAsync, Try, TryAsync, FromResultAsync,
    Map, MapErr, AndThen, AndThenNewConstant, AndThrough, OrElse, OrElseNewConstant, Tap, TapErr, Match,
    UnwrapOr, UnwrapOrElse, Attempt, Provide,
    LinqSameError, LinqSourceError, LinqNextError, LinqSecondNever,
    LinqFirstNever, LinqBothNever, LinqChain, CatchCaseNever,
    RetrySuccess, RetryStop, RetryEightAttempts, RepeatStop, RepeatEightRuns,
    EmptyAll, EmptyAllParallel, EmptyAllErrors, EmptyAllErrorsParallel, EmptyAllSettled, EmptyAllSettledParallel,
}

public static class AllocationScenarios
{
    public static (Effect<int, string, EmptyEnvironment> Effect, Result<int, string> Expected) Create(AllocationCase scenario)
    {
        var source = Ok(20);
        var next = Ok(22);
        Effect<int, string, EmptyEnvironment> failed = Effect<EmptyEnvironment>.Fail("failed");
        var never = Effect<EmptyEnvironment>.Succeed(20);
        var nextNever = Effect<EmptyEnvironment>.Succeed(22);
        var yes = Effect<EmptyEnvironment>.Succeed(true);
        var no = Effect<EmptyEnvironment>.Succeed(false);

        switch (scenario)
        {
            case AllocationCase.Sync:
                return (Effect<EmptyEnvironment>.Sync(static () => 42), Result.Ok(42));
            case AllocationCase.CompletedAsync:
                return (Effect<EmptyEnvironment>.Async(static _ => new ValueTask<int>(42)), Result.Ok(42));
            case AllocationCase.Access:
                return (Effect<int>.Access(static env => env).Provide<EmptyEnvironment>(static _ => 42), Result.Ok(42));
            case AllocationCase.AccessAsync:
                return (Effect<int>.AccessAsync(static (env, _) => new ValueTask<int>(env)).Provide<EmptyEnvironment>(static _ => 42), Result.Ok(42));
            case AllocationCase.Try:
                return (Effect<EmptyEnvironment>.Try(static () => 42, static ex => ex.Message), Result.Ok(42));
            case AllocationCase.TryAsync:
                return (Effect<EmptyEnvironment>.TryAsync(static _ => new ValueTask<int>(42), static ex => ex.Message), Result.Ok(42));
            case AllocationCase.FromResultAsync:
                return (Effect<EmptyEnvironment>.FromResultAsync(static _ => new ValueTask<Result<int, string>>(Result.Ok(42))), Result.Ok(42));
            case AllocationCase.Map:
                return (source.Map(static x => x + 22), Result.Ok(42));
            case AllocationCase.MapErr:
                return (failed.MapErr(static _ => "mapped"), Result.Err("mapped"));
            case AllocationCase.AndThen:
                return (source.AndThen(_ => next), Result.Ok(22));
            case AllocationCase.AndThenNewConstant:
                return (source.AndThen(static value => Effect<EmptyEnvironment>.Succeed(value + 22)), Result.Ok(42));
            case AllocationCase.AndThrough:
                return (source.AndThrough(_ => next), Result.Ok(20));
            case AllocationCase.OrElse:
                return (failed.OrElse(_ => next), Result.Ok(22));
            case AllocationCase.OrElseNewConstant:
                return (failed.OrElse<string>(static error => Effect<EmptyEnvironment>.Fail(error)), Result.Err("failed"));
            case AllocationCase.Tap:
                return (source.Tap(static _ => { }), Result.Ok(20));
            case AllocationCase.TapErr:
                return (failed.TapErr(static _ => { }), Result.Err("failed"));
            case AllocationCase.Match:
                return (failed.Match(static x => x, static _ => 42), Result.Ok(42));
            case AllocationCase.UnwrapOr:
                return (failed.UnwrapOr(42), Result.Ok(42));
            case AllocationCase.UnwrapOrElse:
                return (failed.UnwrapOrElse(static _ => 42), Result.Ok(42));
            case AllocationCase.Attempt:
                return (failed.Attempt().Map(static result => result.UnwrapOr(42)), Result.Ok(42));
            case AllocationCase.Provide:
                return (Effect<int>.Access(static x => x).Provide<EmptyEnvironment>(static _ => 42), Result.Ok(42));
            case AllocationCase.LinqSameError:
                return (source.SelectMany(_ => next, static (x, y) => x + y), Result.Ok(42));
            case AllocationCase.LinqSourceError:
                return (failed.SelectMany(_ => next, static (x, y) => x + y), Result.Err("failed"));
            case AllocationCase.LinqNextError:
                return (source.SelectMany(_ => failed, static (x, y) => x + y), Result.Err("failed"));
            case AllocationCase.LinqSecondNever:
                return (source.SelectMany(_ => nextNever, static (x, y) => x + y), Result.Ok(42));
            case AllocationCase.LinqFirstNever:
                return (never.SelectMany(_ => next, static (x, y) => x + y), Result.Ok(42));
            case AllocationCase.LinqBothNever:
                return (never.SelectMany(_ => nextNever, static (x, y) => x + y), Result.Ok(42));
            case AllocationCase.LinqChain:
                var pair = from x in never from y in next select x + y;
                var chain = from x in pair from y in nextNever select x + y;
                return (chain, Result.Ok(64));
            case AllocationCase.CatchCaseNever:
                Effect<int, CountError, EmptyEnvironment> caseError = Effect<EmptyEnvironment>.Fail(new CountError(7));
                return (caseError.CatchCase((int _) => nextNever).MapErr(static _ => "failed"), Result.Ok(22));
            case AllocationCase.RetrySuccess:
                return (source.Retry(_ => no), Result.Ok(20));
            case AllocationCase.RetryStop:
                return (failed.Retry(_ => no), Result.Err("failed"));
            case AllocationCase.RetryEightAttempts:
                return (failed.Retry(context => context.Attempt < 8 ? yes : no), Result.Err("failed"));
            case AllocationCase.RepeatStop:
                return (source.Repeat(_ => no), Result.Ok(20));
            case AllocationCase.RepeatEightRuns:
                return (source.Repeat(context => context.Repetition < 8 ? yes : no), Result.Ok(20));
            case AllocationCase.EmptyAll:
            case AllocationCase.EmptyAllParallel:
                return (Effect.All<int, string, EmptyEnvironment>([], scenario == AllocationCase.EmptyAllParallel ? Concurrency.Parallel : Concurrency.Sequential)
                    .Map(static values => values.Length), Result.Ok(0));
            case AllocationCase.EmptyAllErrors:
            case AllocationCase.EmptyAllErrorsParallel:
                return (Effect.AllErrors<int, string, EmptyEnvironment>([], scenario == AllocationCase.EmptyAllErrorsParallel ? Concurrency.Parallel : Concurrency.Sequential)
                    .Map(static values => values.Length).MapErr(static _ => "unexpected"), Result.Ok(0));
            case AllocationCase.EmptyAllSettled:
            case AllocationCase.EmptyAllSettledParallel:
                return (Effect.AllSettled<int, string, EmptyEnvironment>([], scenario == AllocationCase.EmptyAllSettledParallel ? Concurrency.Parallel : Concurrency.Sequential)
                    .Map(static values => values.Length), Result.Ok(0));
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }

    private static Effect<int, string, EmptyEnvironment> Ok(int value) => Effect<EmptyEnvironment>.FromResult<int, string>(Result.Ok(value));

    private readonly record struct CountError(int Code) : IUnionCase<CountError, int>
    {
        public static CountError From(int value) => new(value);

        public static bool TryGet(CountError self, out int value)
        {
            value = self.Code;
            return true;
        }
    }
}
