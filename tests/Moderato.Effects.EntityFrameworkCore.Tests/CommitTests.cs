using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using static SQLitePCL.raw;

namespace Moderato.Effects.EntityFrameworkCore.Tests;

public sealed class CommitTests
{

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transaction_UnconfirmedCommitWithoutApplicationMapping_ReturnsStandardErr(bool provideMapper)
    {
        // Given
        await using var store = await Store.Open();
        var failure = new IOException("reply unavailable");
        store.Probe.BeforeCommit = _ => throw failure;
        TryMapDatabaseFailure<TestError>? mapper = provideMapper ? NoMapping : null;

        var body = TestEffects.Insert(1);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body, commitFailure: mapper)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Err<TestError> err));
        var unknown = Assert.IsType<CommitNotConfirmed>(err.Error.Value);
        Assert.Same(failure, unknown.Cause);
        Assert.Equal(0, await store.Count());
        Assert.Equal(1, store.Probe.Rollbacks);
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_CanceledDuringCommit_ReturnsStandardErr()
    {
        // Given
        await using var store = await Store.Open();
        using var cancellation = new CancellationTokenSource();
        store.Probe.BeforeCommit = token =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(token);
        };

        var body = TestEffects.Insert(1);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, cancellation.Token);

        // Then
        Assert.True(exit.TryGetValue(out Err<TestError> err));
        Assert.IsType<CommitNotConfirmed>(err.Error.Value);
        Assert.Equal(0, await store.Count());
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_CanceledBeforeCommit_DoesNotCallCommit()
    {
        // Given
        await using var store = await Store.Open();
        using var cancellation = new CancellationTokenSource();
        var body = TestEffects.Insert(1).Map(value => { cancellation.Cancel(); return value; });

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, cancellation.Token);

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
        Assert.Equal(0, store.Probe.Commits);
        Assert.Equal(0, await store.Count());
    }

    [Fact]
    public async Task Transaction_CanceledAfterSuccessfulCommit_KeepsTheSuccess()
    {
        // Given
        await using var store = await Store.Open();
        using var cancellation = new CancellationTokenSource();
        store.Probe.AfterCommit = () => cancellation.Cancel();

        var body = TestEffects.Insert(1);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, cancellation.Token);

        // Then
        Assert.True(exit.TryGetValue(out Ok<int> _));
        Assert.Equal(1, await store.Count());
        Assert.Equal(0, store.Probe.Rollbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transaction_DeferredForeignKeyRejected_ClassifiesTheConfirmedFailure(bool map)
    {
        // Given
        await using var store = await Store.Open();
        var body = Scope<TestError, TestDb>.Gen(async scope =>
        {
            await scope.Env.Database.ExecuteSqlRawAsync("INSERT INTO Children VALUES (1, 999)", scope.CancellationToken);
            return 1;
        });
        TryMapDatabaseFailure<TestError>? mapper = map ? MapForeignKey : null;

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body, commitFailure: mapper)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        if (map)
        {
            Assert.True(exit.TryGetValue(out Err<TestError> err));
            Assert.IsType<Rejected>(err.Error.Value);
        }
        else
        {
            Assert.True(exit.TryGetValue(out Die? die));
            Assert.IsType<SqliteException>(die.Exception);
        }
        Assert.Equal(0, await store.Count("Children"));
        Assert.Equal(1, store.Probe.Rollbacks);
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_UnconfirmedCommitMapped_ReturnsTheApplicationError()
    {
        // Given
        await using var store = await Store.Open();
        store.Probe.BeforeCommit = _ => throw new IOException("unconfirmed");
        static bool Map(DatabaseFailure failure, out TestError error)
        {
            error = new Rejected(failure is CommitNotConfirmed ? "unconfirmed" : "unexpected");
            return true;
        }

        var body = TestEffects.Insert(1);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body, commitFailure: Map)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Err<TestError> err));
        Assert.Equal("unconfirmed", Assert.IsType<Rejected>(err.Error.Value).Reason);
        Assert.Equal(0, await store.Count());
    }

    [Fact]
    public async Task Transaction_ReplyLostAfterPhysicalCommit_PreservesFailureAndCommittedRows()
    {
        // Given
        await using var store = await Store.Open();
        store.Probe.AfterCommit = () => throw new IOException("lost reply");

        var body = TestEffects.Insert(1);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Contains(die.Suppressed, item => item.Case.Value is Failed { Error: CommitNotConfirmed });
        Assert.Equal(1, await store.Count());
        Assert.Equal(1, store.Probe.Disposals);
    }

    [Fact]
    public async Task Transaction_CommitMapperThrows_RetainsTheOriginalCommitFailure()
    {
        // Given
        await using var store = await Store.Open();
        var original = new IOException("commit");
        var mapperFailure = new InvalidOperationException("mapper");
        store.Probe.BeforeCommit = _ => throw original;
        bool Throw(DatabaseFailure failure, out TestError error) => throw mapperFailure;

        var body = TestEffects.Insert(1);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body, commitFailure: Throw)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(mapperFailure, die.Exception);
        Assert.Contains(die.Suppressed, item => item.Case.Value is Failed { Error: CommitNotConfirmed failure }
            && ReferenceEquals(failure.Cause, original));
        Assert.Equal(0, await store.Count());
    }

    [Fact]
    public async Task Transaction_AfterCommitHookThrowsConstraintError_DoesNotClaimACommitRejection()
    {
        // Given
        await using var store = await Store.Open();
        var failure = new SqliteException("post-commit hook", SQLITE_CONSTRAINT, SQLITE_CONSTRAINT_FOREIGNKEY);
        store.Probe.AfterCommit = () => throw failure;
        DatabaseFailure? observed = null;
        bool Observe(DatabaseFailure value, out TestError error)
        {
            observed = value;
            error = default;
            return false;
        }

        var body = TestEffects.Insert(1);

        // When
        var exit = await EfDatabase<Store>.Transaction(
            contexts: env => env, environment: (_, db) => db, body: body, commitFailure: Observe)
            .RunExitAsync(store, TestContext.Current.CancellationToken);

        // Then
        Assert.IsType<CommitNotConfirmed>(observed);
        Assert.Same(failure, observed.Cause);
        Assert.True(exit.TryGetValue(out Die? _));
        Assert.Equal(1, await store.Count());
    }

    private static bool NoMapping(DatabaseFailure failure, out TestError error)
    {
        error = default;
        return false;
    }

    private static bool MapForeignKey(DatabaseFailure failure, out TestError error)
    {
        if (failure is ConstraintViolation { Kind: ConstraintKind.ForeignKey })
        {
            error = new Rejected("foreign key");
            return true;
        }
        error = default;
        return false;
    }
}
