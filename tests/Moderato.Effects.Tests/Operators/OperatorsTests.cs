namespace Moderato.Effects.Tests;

public class MapTests
{
    [Fact]
    public async Task Map_Ok_TransformsTheValue()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Succeed(2);

        // When
        var result = await effect.Map(x => x * 10).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(20), result);
    }

    [Fact]
    public async Task Map_Err_DoesNotCallTheMapper()
    {
        // Given
        var calls = 0;
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Fail("boom");

        // When
        var result = await effect.Map(x => ++calls).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, string>>(Result.Err("boom"), result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Map_MapperThrows_RunAsyncRethrowsTheDefect()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Succeed(2);

        // When
        var act = async () => await effect.Map<int>(_ => throw new InvalidOperationException("bug")).RunAsync(TestContext.Current.CancellationToken);

        // Then
        await Assert.ThrowsAsync<InvalidOperationException>(act);
    }
}

public class MapErrTests
{
    [Fact]
    public async Task MapErr_Err_TransformsTheError()
    {
        // Given
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Fail("boom");

        // When
        var result = await effect.MapErr(e => e.Length).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, int>>(Result.Err(4), result);
    }

    [Fact]
    public async Task MapErr_Ok_DoesNotCallTheMapper()
    {
        // Given
        var calls = 0;
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Succeed(1);

        // When
        var result = await effect.MapErr(e => ++calls).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, int>>(Result.Ok(1), result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task MapErr_MapperThrows_DiesKeepingTheOriginalErr()
    {
        // Given
        Effect<int, string, EmptyEnvironment> failing = Effect<EmptyEnvironment>.Fail("database unavailable");

        // When
        var exit = await failing.MapErr<int>(_ => throw new InvalidOperationException("error mapping failed")).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<InvalidOperationException>(die.Exception);
        Assert.Equal([new Suppressed(new Failed("database unavailable"), Relation.Sequential)], die.Suppressed);
    }
}

public class AndThenTests
{
    private static Effect<int, string, EmptyEnvironment> Half(int x)
        => x % 2 == 0 ? Effect<EmptyEnvironment>.Succeed(x / 2) : Effect<EmptyEnvironment>.Fail("odd");

    [Fact]
    public async Task AndThen_Ok_RunsTheNextEffect()
    {
        // Given
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Succeed(4);

        // When
        var result = await effect.AndThen(Half).AndThen(Half).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, string>>(Result.Ok(1), result);
    }

    [Fact]
    public async Task AndThen_Err_DoesNotCallTheBinder()
    {
        // Given
        var calls = 0;
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Fail("boom");

        // When
        var result = await effect.AndThen(x => { calls++; return Half(x); }).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, string>>(Result.Err("boom"), result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AndThen_NextEffectCannotFail_NeedsNoTypeArguments()
    {
        // Given
        Effect<User, AppError, EmptyEnvironment> findUser = Effect<EmptyEnvironment>.Succeed(new User(1, "ada"));

        // When
        var result = await findUser.AndThen(user => Effect<EmptyEnvironment>.Succeed(user.Name)).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var name));
        Assert.Equal("ada", name);
    }

    [Fact]
    public async Task AndThen_ConditionalSucceedOrFail_TakesTheValueTypeAsTypeArgument()
    {
        // Given
        Effect<User, AppError, EmptyEnvironment> findUser = Effect<EmptyEnvironment>.Succeed(new User(1, "ada"));

        // When
        var effect = findUser.AndThen<string>(user => user.Id > 1
            ? Effect<EmptyEnvironment>.Succeed(user.Name)
            : Effect<EmptyEnvironment>.Fail<AppError>(new Forbidden("too early")));
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var error));
        Assert.Equal(new Forbidden("too early"), error.Value);
    }

    [Fact]
    public async Task AndThen_BinderReturnsDefaultEffect_RunAsyncThrows()
    {
        // Given
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Succeed(1);

        // When
        var act = async () => await effect.AndThen(_ => default(Effect<int, string, EmptyEnvironment>)).RunAsync(TestContext.Current.CancellationToken);

        // Then
        await Assert.ThrowsAsync<InvalidOperationException>(act);
    }

    [Fact]
    public async Task AndThen_ComposedEffectRunTwice_RunsEveryStepAgain()
    {
        // Given
        var calls = 0;
        var effect = Effect<EmptyEnvironment>.Sync(() => ++calls).AndThen(x => Effect<EmptyEnvironment>.Sync(() => x + ++calls));

        // When
        var first = await effect.RunAsync(TestContext.Current.CancellationToken);
        var second = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(1 + 2), first);
        Assert.Equal<Result<int, Never>>(Result.Ok(3 + 4), second);
    }
}

public class AndThroughTests
{
    private static Effect<Unit, string, EmptyEnvironment> Positive(int x)
        => x > 0 ? Effect<EmptyEnvironment>.Succeed(Unit.Value) : Effect<EmptyEnvironment>.Fail($"{x} is not positive");

    [Fact]
    public async Task AndThrough_CheckPasses_KeepsTheValue()
    {
        // Given
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Succeed(2);

        // When
        var result = await effect.AndThrough(Positive).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, string>>(Result.Ok(2), result);
    }

    [Fact]
    public async Task AndThrough_CheckFails_RunsToTheChecksErr()
    {
        // Given
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Succeed(-1);

        // When
        var result = await effect.AndThrough(Positive).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, string>>(Result.Err("-1 is not positive"), result);
    }
}

public class OrElseTests
{
    [Fact]
    public async Task OrElse_Err_RunsTheRecovery()
    {
        // Given
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Fail("boom");

        // When
        var result = await effect.OrElse(_ => Effect<EmptyEnvironment>.Succeed(0)).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(0), result);
    }

    [Fact]
    public async Task OrElse_Ok_DoesNotCallTheBinder()
    {
        // Given
        var calls = 0;
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Succeed(2);

        // When
        var result = await effect.OrElse(_ => { calls++; return Effect<EmptyEnvironment>.Succeed(0); }).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(2), result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OrElse_RecoveryFails_RunsToTheRecoverysErr()
    {
        // Given
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Fail("boom");

        // When
        var result = await effect.OrElse<int>(e => Effect<EmptyEnvironment>.Fail(e.Length)).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, int>>(Result.Err(4), result);
    }

    [Fact]
    public async Task OrElse_HandlerThrows_DiesKeepingTheOriginalErr()
    {
        // Given
        Effect<int, string, EmptyEnvironment> failing = Effect<EmptyEnvironment>.Fail("database unavailable");

        // When
        var exit = await failing.OrElse<string>(_ => throw new InvalidOperationException("recovery failed")).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<InvalidOperationException>(die.Exception);
        Assert.Equal([new Suppressed(new Failed("database unavailable"), Relation.Sequential)], die.Suppressed);
    }
}

public class TapTests
{
    [Fact]
    public async Task Tap_Ok_RunsTheSideEffectAndKeepsTheValue()
    {
        // Given
        var seen = new List<int>();
        var effect = Effect<EmptyEnvironment>.Succeed(2);

        // When
        var result = await effect.Tap(seen.Add).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(2), result);
        Assert.Equal([2], seen);
    }

    [Fact]
    public async Task TapErr_Err_RunsTheSideEffectAndKeepsTheError()
    {
        // Given
        var seen = new List<string>();
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Fail("boom");

        // When
        var result = await effect.Tap(_ => seen.Add("ok")).TapErr(seen.Add).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, string>>(Result.Err("boom"), result);
        Assert.Equal(["boom"], seen);
    }
}

public class MatchTests
{
    [Fact]
    public async Task Match_OkAndErr_FoldIntoOneValue()
    {
        // Given
        Effect<int, string, EmptyEnvironment> ok = Effect<EmptyEnvironment>.Succeed(2);
        Effect<int, string, EmptyEnvironment> err = Effect<EmptyEnvironment>.Fail("boom");

        // When
        var okText = await ok.Match(x => $"ok {x}", e => $"err {e}").RunAsync(TestContext.Current.CancellationToken);
        var errText = await err.Match(x => $"ok {x}", e => $"err {e}").RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, Never>>(Result.Ok("ok 2"), okText);
        Assert.Equal<Result<string, Never>>(Result.Ok("err boom"), errText);
    }

    [Fact]
    public async Task Match_Defect_IsNotFolded()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Sync<int>(() => throw new InvalidOperationException("bug"));

        // When
        var exit = await effect.Match(x => $"ok {x}", e => "err").RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? _));
    }
}

public class UnwrapOrTests
{
    [Fact]
    public async Task UnwrapOr_Err_RunsToTheFallback()
    {
        // Given
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Fail("boom");

        // When
        var result = await effect.UnwrapOr(0).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(0), result);
    }

    [Fact]
    public async Task UnwrapOrElse_Err_BuildsTheFallbackFromTheError()
    {
        // Given
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Fail("boom");

        // When
        var result = await effect.UnwrapOrElse(e => e.Length).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(4), result);
    }

    [Fact]
    public async Task UnwrapOr_Ok_KeepsTheValue()
    {
        // Given
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Succeed(2);

        // When
        var result = await effect.UnwrapOr(0).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(2), result);
    }
}

public class AttemptTests
{
    [Fact]
    public async Task Attempt_Err_RunsToOkHoldingTheErr()
    {
        // Given
        Effect<User, AppError, EmptyEnvironment> findUser = Effect<EmptyEnvironment>.Fail<AppError>(new NotFound(9));

        // When
        var found = await findUser.Attempt().RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(found.TryGetOk(out var result));
        var message = result switch
        {
            Ok<User>(var user) => user.Name,
            Err<AppError>(NotFound n) => $"guest for {n.Id}",
            Err<AppError>(Forbidden f) => f.Reason,
        };
        Assert.Equal("guest for 9", message);
    }

    [Fact]
    public async Task Attempt_Defect_IsNotTurnedIntoAValue()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Sync<int>(() => throw new InvalidOperationException("bug"));

        // When
        var exit = await effect.Attempt().RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? _));
    }
}

public class WidenTests
{
    [Fact]
    public async Task Widen_CaseErrorInQuery_BecomesTheUnion()
    {
        // Given
        Effect<User, NotFound, EmptyEnvironment> findUser = Effect<EmptyEnvironment>.Fail(new NotFound(3));
        Effect<string, Forbidden, EmptyEnvironment> authorize = Effect<EmptyEnvironment>.Succeed("ok");

        // When
        var effect =
            from user in findUser.Widen<AppError>()
            from grant in authorize.Widen<AppError>()
            select user.Name + grant;
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var error));
        Assert.Equal(new NotFound(3), error.Value);
    }
}

public class CatchCaseTests
{
    private static readonly User guest = new(0, "guest");

    [Fact]
    public async Task CatchCase_TheCaughtCase_RecoversWithAnEffectThatCannotFail()
    {
        // Given
        Effect<User, AppError, EmptyEnvironment> findUser = Effect<EmptyEnvironment>.Fail<AppError>(new NotFound(1));

        // When
        var result = await findUser.CatchCase((NotFound _) => Effect<EmptyEnvironment>.Succeed(guest)).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var user));
        Assert.Same(guest, user);
    }

    [Fact]
    public async Task CatchCase_AnotherCase_PassesThrough()
    {
        // Given
        Effect<User, AppError, EmptyEnvironment> findUser = Effect<EmptyEnvironment>.Fail<AppError>(new Forbidden("inactive"));

        // When
        var result = await findUser.CatchCase((NotFound _) => Effect<EmptyEnvironment>.Succeed(guest)).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var error));
        Assert.Equal(new Forbidden("inactive"), error.Value);
    }

    [Fact]
    public async Task CatchCase_RecoveryFails_RunsToTheRecoverysErr()
    {
        // Given
        Effect<User, AppError, EmptyEnvironment> findUser = Effect<EmptyEnvironment>.Fail<AppError>(new NotFound(1));
        Effect<User, AppError, EmptyEnvironment> fallback = Effect<EmptyEnvironment>.Fail<AppError>(new Forbidden("no guests"));

        // When
        var result = await findUser.CatchCase((NotFound _) => fallback).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var error));
        Assert.Equal(new Forbidden("no guests"), error.Value);
    }
}

public class ProvideTests
{
    [Fact]
    public async Task Provide_OuterEnvironment_BuildsTheInnerOne()
    {
        // Given
        var now = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        var readClock = Effect<Clock>.Access(env => env.Resolve<TimeProvider>().GetUtcNow());

        // When
        var result = await readClock.Provide((DateTimeOffset outer) => new Clock(outer)).RunAsync(now, TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<DateTimeOffset, Never>>(Result.Ok(now), result);
    }

    [Fact]
    public async Task Provide_BuildThrows_RunAsyncRethrowsTheDefect()
    {
        // Given
        var readClock = Effect<Clock>.Access(env => env.Resolve<TimeProvider>().GetUtcNow());

        // When
        var act = async () => await readClock.Provide<string>(_ => throw new InvalidOperationException("no clock")).RunAsync("", TestContext.Current.CancellationToken);

        // Then
        await Assert.ThrowsAsync<InvalidOperationException>(act);
    }

    [Fact]
    public async Task WidenEnv_EnvironmentWithMoreServices_RunsTheEffect()
    {
        // Given
        var readClock = Effect<IHas<TimeProvider>>.Access(env => env.Resolve());
        var environment = DefaultEnvironment.Value;

        // When
        var result = await readClock.WidenEnv<DefaultEnvironment>().RunAsync(environment, TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var clock));
        Assert.Same(TimeProvider.System, clock);
    }
}

public class LinqTests
{
    private static Effect<User, AppError, EmptyEnvironment> FindUser(int id)
        => id > 0 ? Effect<EmptyEnvironment>.Succeed(new User(id, $"user{id}")) : Effect<EmptyEnvironment>.Fail<AppError>(new NotFound(id));

    private static Effect<int, Never, EmptyEnvironment> Log(List<string> log, string message)
        => Effect<EmptyEnvironment>.Sync(() => { log.Add(message); return log.Count; });

    [Fact]
    public async Task SelectMany_StepsOfTheSameErrorType_RunInOrder()
    {
        // Given
        var effect =
            from a in FindUser(1)
            from b in FindUser(2)
            select a.Name + b.Name;

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var names));
        Assert.Equal("user1user2", names);
    }

    [Fact]
    public async Task SelectMany_StepsThatCannotFail_MixWithStepsThatCan()
    {
        // Given
        var log = new List<string>();
        var effect =
            from started in Log(log, "start")
            from user in FindUser(1)
            from logged in Log(log, $"found {user.Name}")
            select logged;

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var count));
        Assert.Equal(2, count);
        Assert.Equal(["start", "found user1"], log);
    }

    [Fact]
    public async Task SelectMany_OnlyStepsThatCannotFail_CannotFail()
    {
        // Given
        var log = new List<string>();

        // When
        Effect<int, Never, EmptyEnvironment> effect =
            from a in Log(log, "a")
            from b in Log(log, "b")
            select a + b;
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(3), result);
    }

    [Fact]
    public async Task SelectMany_FirstStepFails_SkipsTheRest()
    {
        // Given
        var log = new List<string>();
        var effect =
            from user in FindUser(0)
            from logged in Log(log, "unreachable")
            select logged;

        // When
        var result = await effect.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var error));
        Assert.Equal(new NotFound(0), error.Value);
        Assert.Empty(log);
    }

    [Fact]
    public async Task SelectMany_StepReadsTheEnvironment_ResolvesTheService()
    {
        // Given
        var now = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        var effect =
            from start in Effect<Clock>.Succeed(1)
            from clock in Effect<Clock>.Access(env => env.Resolve<TimeProvider>())
            select clock.GetUtcNow();

        // When
        var result = await effect.RunAsync(new Clock(now), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<DateTimeOffset, Never>>(Result.Ok(now), result);
    }
}
