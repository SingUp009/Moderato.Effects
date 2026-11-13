namespace Moderato.Effects.EntityFrameworkCore.Tests;

public sealed class TransactionTests
{
    [Fact]
    public async Task Transaction_Success_CommitsAndDisposesTheContext()
    {
        // Given
        await using var store = await Store.Open();

        var body = TestEffects.Insert(1);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env,
            environment: (_, db) => db,
            body: body).RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Ok<int> ok));
        Assert.Equal(1, ok.Value);
        Assert.Equal(1, await store.Count());
        Assert.Equal(1, store.Probe.Commits);
        Assert.Equal(0, store.Probe.Rollbacks);
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Theory]
    [InlineData("Err")]
    [InlineData("Die")]
    [InlineData("Interrupted")]
    [InlineData("Release")]
    public async Task Transaction_BodyFails_RollsBackBeforeDisposal(string kind)
    {
        // Given
        await using var store = await Store.Open();
        using var cancellation = new CancellationTokenSource();
        var failure = new InvalidOperationException("body");
        var body = Scope<TestError, TestDb>.Gen(async scope =>
        {
            scope.Env.Rows.Add(new Row { Id = 1, Name = "staged" });
            await scope.Env.SaveChangesAsync(scope.CancellationToken);
            if (kind == "Err") throw scope.Fail(new Rejected("body"));
            if (kind == "Die") throw failure;
            if (kind == "Interrupted")
            {
                cancellation.Cancel();
                throw new OperationCanceledException(scope.CancellationToken);
            }
            scope.Defer(_ => Effect<TestDb>.Sync<Unit>(() => throw failure));
            return 1;
        });

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, cancellation.Token);

        // Then
        if (kind == "Err")
        {
            Assert.True(exit.TryGetValue(out Err<TestError> _));
        }
        else if (kind == "Interrupted")
        {
            Assert.True(exit.TryGetValue(out Interrupted? _));
        }
        else
        {
            Assert.True(exit.TryGetValue(out Die? die));
            Assert.Same(failure, die.Exception);
        }
        Assert.Equal(0, await store.Count());
        Assert.Equal(0, store.Probe.Commits);
        Assert.Equal(1, store.Probe.Rollbacks);
        Assert.False(store.Probe.RollbackCanBeCanceled);
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_Reexecuted_CreatesFreshContexts()
    {
        // Given
        await using var store = await Store.Open();
        Effect<Guid, TestError, TestDb> body = Effect<TestDb>.Access(db => db.Instance);
        var effect = EfDatabase<Store>.Transaction(contexts: env => env,
            environment: (_, db) => db, body: body);

        // When
        var first = await effect.RunExitAsync(store, TestContext.Current.CancellationToken);
        var second = await effect.RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(first.TryGetValue(out Ok<Guid> one));
        Assert.True(second.TryGetValue(out Ok<Guid> two));
        Assert.NotEqual(one.Value, two.Value);
        Assert.Equal(2, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_EnvironmentFactoryThrows_RollsBackAndDisposes()
    {
        // Given
        await using var store = await Store.Open();
        var failure = new InvalidOperationException("environment");
        TestDb Environment(Store env, TestDb db) => throw failure;

        var body = TestEffects.Insert(1);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: Environment, body: body)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(0, store.Probe.Saves);
        Assert.Equal(1, store.Probe.Rollbacks);
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_DefaultBody_RejectsBeforeCreatingAContext()
    {
        // Given
        await using var store = await Store.Open();
        var contextsRequested = false;
        Effect<int, TestError, TestDb> body = default;

        // When
        var exception = Assert.Throws<InvalidOperationException>(() => EfDatabase<Store>.Transaction(
            contexts: env => { contextsRequested = true; return env; }, environment: (_, db) => db, body: body));

        // Then
        Assert.NotNull(exception);
        Assert.False(contextsRequested);
        Assert.Equal(0, store.Probe.Disposals);
    }
}
