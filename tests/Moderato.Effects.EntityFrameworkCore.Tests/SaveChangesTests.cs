using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Moderato.Effects.EntityFrameworkCore.Tests;

public sealed class SaveChangesTests
{

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChangesAsync_DuplicateKey_MapsOnlyWhenRequested(bool map)
    {
        // Given
        await using var store = await Store.Open();
        await using (var existing = store.CreateDbContext())
        {
            existing.Rows.Add(new Row { Id = 1, Name = "original" });
            await existing.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var body = Scope<TestError, TestDb>.Gen(async scope =>
        {
            scope.Env.Rows.Add(new Row { Id = 1, Name = "duplicate" });
            return await scope.Env.SaveChangesAsync(scope, map ? MapUnique : null);
        });

        await using var db = store.CreateDbContext();

        // When
        var exit = await body.RunExitAsync(db, TestContext.Current.CancellationToken);

        // Then
        if (map)
        {
            Assert.True(exit.TryGetValue(out Err<TestError> err));
            Assert.IsType<Rejected>(err.Error.Value);
        }
        else
        {
            Assert.True(exit.TryGetValue(out Die? die));
            var update = Assert.IsType<DbUpdateException>(die.Exception);
            Assert.IsType<SqliteException>(update.InnerException);
        }
        Assert.Equal(1, await store.Count());
    }

    [Fact]
    public async Task SaveChangesAsync_MapperThrows_DiesAndRollsBack()
    {
        // Given
        await using var store = await Store.Open();
        var failure = new InvalidOperationException("mapper");
        DatabaseFailure? observedFailure = null;
        var body = Scope<TestError, TestDb>.Gen(async scope =>
        {
            scope.Env.Rows.Add(new Row { Id = 1, Name = null! });
            return await scope.Env.SaveChangesAsync(scope, Throw);
        });
        bool Throw(DatabaseFailure observed, out TestError error)
        {
            observedFailure = observed;
            throw failure;
        }

        await using var db = store.CreateDbContext();

        // When
        var exit = await body.RunExitAsync(db, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Contains(die.Suppressed, item => item.Case.Value is Failed { Error: DatabaseFailure { Cause: DbUpdateException } });
        Assert.Contains(die.Suppressed, item => item.Case.Value is Failed failed && ReferenceEquals(failed.Error, observedFailure));
        Assert.Equal(0, await store.Count());
    }

    [Fact]
    public async Task SaveChangesAsync_OuterScopeUsedInNestedGen_RejectsBeforeTheDatabaseCall()
    {
        // Given
        await using var store = await Store.Open();
        var body = Scope<TestError, TestDb>.Gen(async outer =>
            await outer.Run(Scope<TestError, TestDb>.Gen(async inner =>
            {
                inner.Env.Rows.Add(new Row { Id = 1, Name = "invalid scope" });
#pragma warning disable MEFF303 // The negative test checks the runtime guard independently of the analyzer.
                return await inner.Env.SaveChangesAsync(outer);
#pragma warning restore MEFF303
            })));

        await using var db = store.CreateDbContext();

        // When
        var exit = await body.RunExitAsync(db, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<InvalidOperationException>(die.Exception);
        Assert.Equal(0, store.Probe.Saves);
        Assert.Equal(0, await store.Count());
    }

    [Fact]
    public async Task SaveChangesAsync_NotAwaited_WaitsForThePendingOperationBeforeReturning()
    {
        // Given
        await using var store = await Store.Open();
        var allowSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Probe.BeforeSave = () => allowSave.Task;
        var body = Scope<TestError, TestDb>.Gen(scope =>
        {
            scope.Env.Rows.Add(new Row { Id = 1, Name = "not awaited" });
            _ = scope.Env.SaveChangesAsync(scope);
            return new ValueTask<int>(1);
        });

        await using var db = store.CreateDbContext();

        // When
        var running = body.RunExitAsync(db, TestContext.Current.CancellationToken).AsTask();

        // Then
        try
        {
            Assert.False(running.IsCompleted);
            Assert.Equal(0, store.Probe.Commits);
            Assert.Equal(0, store.Probe.Disposals);
        }
        finally
        {
            allowSave.TrySetResult();
        }
        await running;
        Assert.Equal(0, await store.Count());
    }

    private static bool MapUnique(DatabaseFailure failure, out TestError error)
    {
        if (failure is ConstraintViolation { Kind: ConstraintKind.Unique })
        {
            error = new Rejected("duplicate");
            return true;
        }
        error = default;
        return false;
    }
}
