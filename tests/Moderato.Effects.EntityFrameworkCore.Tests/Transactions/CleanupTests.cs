using Microsoft.EntityFrameworkCore;

namespace Moderato.Effects.EntityFrameworkCore.Tests;

public sealed class CleanupTests
{

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transaction_ChildAndReleaseArePending_WaitsBeforeCommit(bool releaseFails)
    {
        // Given
        await using var store = await Store.Open();
        var token = TestContext.Current.CancellationToken;
        var entered = Signal();
        var childReleasing = Signal();
        var finishChild = Signal();
        var outerReleasing = Signal();
        var finishOuter = Signal();
        var defect = new IOException("outer release");
        var child = Scope<Never, TestDb>.Gen(async scope =>
        {
            scope.Defer(_ => Effect<TestDb>.Async<Unit>(async _ =>
            {
                childReleasing.SetResult();
                await finishChild.Task;
                return Unit.Value;
            }));
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, scope.CancellationToken);
            return Unit.Value;
        });
        var insert = TestEffects.Insert(1);
        var body = Scope<TestError, TestDb>.Gen(async scope =>
        {
            var rows = await scope.Run(insert);
            scope.Defer(_ => Effect<TestDb>.Async<Unit>(async _ =>
            {
                outerReleasing.SetResult();
                await finishOuter.Task;
                if (releaseFails) throw defect;
                return Unit.Value;
            }));
            await scope.Fork(child);
            await entered.Task;
            return rows;
        });

        // When
        var running = EfDatabase<Store>.Transaction(env => env, (_, db) => db, body).RunExitAsync(store, token).AsTask();
        bool waitingForChild;
        bool waitingForOuter;
        try
        {
            await childReleasing.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            waitingForChild = !running.IsCompleted && store.Probe.Commits == 0 && store.Probe.Disposals == 0;
            finishChild.SetResult();
            await outerReleasing.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            waitingForOuter = !running.IsCompleted && store.Probe.Commits == 0 && store.Probe.Disposals == 0;
        }
        finally
        {
            finishChild.TrySetResult();
            finishOuter.TrySetResult();
        }
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), token);

        // Then
        Assert.True(waitingForChild);
        Assert.True(waitingForOuter);
        Assert.Equal(releaseFails ? 0 : 1, store.Probe.Commits);
        Assert.Equal(releaseFails ? 1 : 0, store.Probe.Rollbacks);
        Assert.Equal(releaseFails ? 0 : 1, await store.Count());
        Assert.Equal(1, store.Probe.Disposals);
        if (releaseFails) Assert.Same(defect, Assert.IsType<Die>(exit.Value).Exception);
        else Assert.Equal(1, Assert.IsType<Ok<int>>(exit.Value).Value);

        static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [Fact]
    public async Task Transaction_RollbackFails_StillDisposesAndRetainsTheBodyErr()
    {
        // Given
        await using var store = await Store.Open();
        var failure = new InvalidOperationException("rollback");
        store.Probe.RollbackFailure = failure;
        Effect<int, TestError, TestDb> body = Scope<TestError, TestDb>.Gen<int>(scope =>
            throw scope.Fail(new Rejected("body")));

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Contains(die.Suppressed, item => item.Case.Value is Failed && item.Relation == Relation.Sequential);
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_DisposeFailsAfterCommit_DoesNotRollback()
    {
        // Given
        await using var store = await Store.Open();
        var failure = new InvalidOperationException("dispose");
        store.Probe.DisposeFailure = failure;

        var body = TestEffects.Insert(1);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(1, await store.Count());
        Assert.Equal(0, store.Probe.Rollbacks);
    }

    [Fact]
    public async Task Transaction_BodyAndBothCleanupOperationsFail_RetainsAllFailures()
    {
        // Given
        await using var store = await Store.Open();
        var rollback = new InvalidOperationException("rollback");
        var dispose = new InvalidOperationException("dispose");
        store.Probe.RollbackFailure = rollback;
        store.Probe.DisposeFailure = dispose;
        var body = Scope<TestError, TestDb>.Gen<int>(scope => throw scope.Fail(new Rejected("body")));

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(rollback, die.Exception);
        Assert.Contains(die.Suppressed, item => item.Case.Value is Die d && ReferenceEquals(d.Exception, dispose));
        Assert.Contains(die.Suppressed, item => item.Case.Value is Failed);
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_RetryingExecutionStrategy_RejectsBeforeStartingTheTransaction()
    {
        // Given
        await using var store = await Store.Open(retry: true);

        var body = TestEffects.Insert(1);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<NotSupportedException>(die.Exception);
        Assert.Equal(0, store.Probe.Saves);
        Assert.Equal(0, store.Probe.Commits);
        Assert.Equal(0, store.Probe.Rollbacks);
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_SecondSaveFails_RollsBackTheFirstSave()
    {
        // Given
        await using var store = await Store.Open();
        var body = Scope<TestError, TestDb>.Gen(async scope =>
        {
            scope.Env.Rows.Add(new Row { Id = 1, Name = "first" });
            await scope.Env.SaveChangesAsync(scope);
            scope.Env.Rows.Add(new Row { Id = 2, Name = null! });
            return await scope.Env.SaveChangesAsync(scope);
        });

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<DbUpdateException>(die.Exception);
        Assert.Equal(0, await store.Count());
    }
}
