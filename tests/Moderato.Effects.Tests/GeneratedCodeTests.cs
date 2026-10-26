using Microsoft.Extensions.Time.Testing;

namespace Moderato.Effects.Tests;

[Environment]
public sealed partial record ProfileEnv([Expose<TimeProvider>] FakeTimeProvider Clock, Counter Visits);

public class NestedErrorUnionTests
{
    [Fact]
    public async Task ScopeRun_ErrorThatIsACaseOfACase_WidensToTheOuterUnion()
    {
        // Given
        Effect<int, ConnFailed, EmptyEnvironment> connect = Effect<EmptyEnvironment>.Fail(new ConnFailed("db1"));
        var load = Scope<StoreError, EmptyEnvironment>.Gen(async scope => await scope.Run(connect));

        // When
        var result = await load.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var error));
        Assert.True(error is DbError db && db is ConnFailed { Host: "db1" });
    }

    [Fact]
    public async Task CatchCase_CaseOfACase_RecoversFromIt()
    {
        // Given
        Effect<string, StoreError, EmptyEnvironment> load = Effect<EmptyEnvironment>.Fail<StoreError>(new DbError(new QueryFailed("select 1")));

        // When
        var result = await load.CatchCase((QueryFailed q) => Effect<EmptyEnvironment>.Succeed($"cached instead of {q.Sql}"))
            .RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, StoreError>>(Result.Ok("cached instead of select 1"), result);
    }
}

public class CatchTests
{
    [Fact]
    public async Task Catch_TheCaseLeftOut_RecoversAndRemovesItFromTheErrorType()
    {
        // Given
        Effect<User, AppError, EmptyEnvironment> findUser = Effect<EmptyEnvironment>.Fail<AppError>(new NotFound(1));

        // When
        Effect<User, Denied, EmptyEnvironment> orGuest = findUser.Catch<NotFound, Denied>(nf => Effect<EmptyEnvironment>.Succeed(new User(nf.Id, "guest")));
        var result = await orGuest.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<User, Denied>>(Result.Ok(new User(1, "guest")), result);
    }

    [Fact]
    public async Task Catch_AnotherCase_PassesThroughAsTheRest()
    {
        // Given
        Effect<User, AppError, EmptyEnvironment> findUser = Effect<EmptyEnvironment>.Fail<AppError>(new Forbidden("inactive"));

        // When
        var result = await findUser.Catch<NotFound, Denied>(nf => Effect<EmptyEnvironment>.Succeed(new User(nf.Id, "guest")))
            .RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetErr(out var denied));
        Assert.Equal(new Forbidden("inactive"), denied.Value);
    }

    [Fact]
    public async Task Catch_Ok_DoesNotCallTheHandler()
    {
        // Given
        var called = false;
        Effect<User, AppError, EmptyEnvironment> findUser = Effect<EmptyEnvironment>.Succeed(new User(1, "ada"));

        // When
        var result = await findUser.Catch<NotFound, Denied>(_ =>
        {
            called = true;
            return Effect<EmptyEnvironment>.Succeed(new User(0, "guest"));
        }).RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<User, Denied>>(Result.Ok(new User(1, "ada")), result);
        Assert.False(called);
    }
}

public class EnvironmentAttributeTests
{
    [Fact]
    public async Task Environment_ExposedFakeClock_DrivesSleepWhileTheTestKeepsTheConcreteType()
    {
        // Given
        var env = new ProfileEnv(new FakeTimeProvider(), new Counter());
        var nap = Effect.Sleep<ProfileEnv>(TimeSpan.FromSeconds(5));

        // When
        var running = nap.RunAsync(env, TestContext.Current.CancellationToken).AsTask();
        env.Clock.Advance(TimeSpan.FromSeconds(5));
        var result = await running;

        // Then
        Assert.Equal<Result<Unit, Never>>(Result.Ok(Unit.Value), result);
    }

    [Fact]
    public async Task Environment_Parameter_IsResolvedAsItsOwnType()
    {
        // Given
        var env = new ProfileEnv(new FakeTimeProvider(), new Counter());
        var visit = Effect<ProfileEnv>.Access(e => e.Resolve<Counter>().Next());

        // When
        await visit.RunAsync(env, TestContext.Current.CancellationToken);
        var result = await visit.RunAsync(env, TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, Never>>(Result.Ok(2), result);
    }
}

public class NullableCaseTests
{
    [Fact]
    public async Task CatchCase_NullableCaseWithAValue_RecoversFromIt()
    {
        // Given
        Effect<string, Note, EmptyEnvironment> load = Effect<EmptyEnvironment>.Fail<Note>("missing");

        // When
        var result = await load.CatchCase((string? note) => Effect<EmptyEnvironment>.Succeed($"noted {note ?? "null"}"))
            .RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, Note>>(Result.Ok("noted missing"), result);
    }

    [Fact]
    public async Task CatchCase_NullableCaseHoldingNull_RecoversWithNull()
    {
        // Given
        Effect<string, Note, EmptyEnvironment> load = Effect<EmptyEnvironment>.Fail<Note>((string?)null);

        // When
        var result = await load.CatchCase((string? note) => Effect<EmptyEnvironment>.Succeed($"noted {note ?? "null"}"))
            .RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, Note>>(Result.Ok("noted null"), result);
    }

    [Fact]
    public async Task Catch_NullableCaseHoldingNull_RemovesIt()
    {
        // Given
        Effect<string, Note, EmptyEnvironment> load = Effect<EmptyEnvironment>.Fail<Note>((string?)null);

        // When
        Effect<string, NoteNumber, EmptyEnvironment> recovered = load.Catch<string?, NoteNumber>(note => Effect<EmptyEnvironment>.Succeed(note ?? "null"));
        var result = await recovered.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, NoteNumber>>(Result.Ok("null"), result);
    }

    [Fact]
    public async Task Catch_NullableCaseLeftOutWithAnotherCase_PassesItThrough()
    {
        // Given
        Effect<string, Note, EmptyEnvironment> load = Effect<EmptyEnvironment>.Fail<Note>(7);

        // When
        Effect<string, NoteNumber, EmptyEnvironment> recovered = load.Catch<string?, NoteNumber>(note => Effect<EmptyEnvironment>.Succeed(note ?? "null"));
        var result = await recovered.RunAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<string, NoteNumber>>(Result.Err<NoteNumber>(7), result);
    }
}
