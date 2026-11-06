using Microsoft.EntityFrameworkCore;

namespace Moderato.Effects.EntityFrameworkCore.Tests;

public sealed class ConcurrencyConflictTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SaveChangesAsync_StaleUpdateOrDelete_ClassifiesWithoutRetry(bool delete, bool? map)
    {
        // Given
        await using var store = await Store.Open();
        await using (var seed = store.CreateDbContext())
        {
            seed.Rows.Add(new Row { Id = 1, Name = "original" });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using var db = store.CreateDbContext();
        var stale = await db.Rows.SingleAsync(TestContext.Current.CancellationToken);
        await using (var winner = store.CreateDbContext())
        {
            winner.Rows.Remove(await winner.Rows.SingleAsync(TestContext.Current.CancellationToken));
            await winner.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        if (delete) db.Rows.Remove(stale);
        else stale.Name = "pending";
        var saves = store.Probe.Saves;
        object? observed = null;
        bool Map(DatabaseFailure failure, out TestError error)
        {
            observed = failure.Value;
            error = new Rejected("concurrent change");
            return map == true;
        }
        var body = Scope<TestError, TestDb>.Gen(async scope =>
            await scope.Env.SaveChangesAsync(scope, map is null ? null : Map));

        // When
        var exit = await body.RunExitAsync(db, TestContext.Current.CancellationToken);

        // Then
        DbUpdateConcurrencyException cause;
        if (map == true)
        {
            Assert.True(exit.TryGetValue(out Err<TestError> err));
            Assert.Equal("concurrent change", Assert.IsType<Rejected>(err.Error.Value).Reason);
            cause = Assert.IsType<DbUpdateConcurrencyException>(Assert.IsType<OptimisticConcurrencyConflict>(observed).Cause);
        }
        else
        {
            Assert.True(exit.TryGetValue(out Die? die));
            cause = Assert.IsType<DbUpdateConcurrencyException>(die.Exception);
            if (map.HasValue) Assert.Same(cause, Assert.IsType<OptimisticConcurrencyConflict>(observed).Cause);
            else Assert.Null(observed);
        }
        Assert.Null(cause.InnerException);
        Assert.Same(stale, Assert.Single(cause.Entries).Entity);
        Assert.Equal(saves + 1, store.Probe.Saves);
        Assert.Equal(0, await store.Count());
    }

    [Fact]
    public async Task SaveChangesAsync_ConcurrencyMapperThrows_RetainsTheOriginalConflict()
    {
        // Given
        await using var store = await Store.Open();
        await using (var seed = store.CreateDbContext())
        {
            seed.Rows.Add(new Row { Id = 1, Name = "original" });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using var db = store.CreateDbContext();
        var stale = await db.Rows.SingleAsync(TestContext.Current.CancellationToken);
        await using (var winner = store.CreateDbContext())
        {
            winner.Rows.Remove(await winner.Rows.SingleAsync(TestContext.Current.CancellationToken));
            await winner.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        stale.Name = "pending";
        var mapperFailure = new InvalidOperationException("mapper");
        object? observed = null;
        bool Throw(DatabaseFailure failure, out TestError error)
        {
            observed = failure.Value;
            throw mapperFailure;
        }
        var body = Scope<TestError, TestDb>.Gen(async scope => await scope.Env.SaveChangesAsync(scope, Throw));

        // When
        var exit = await body.RunExitAsync(db, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(mapperFailure, die.Exception);
        var conflict = Assert.IsType<OptimisticConcurrencyConflict>(observed);
        Assert.IsType<DbUpdateConcurrencyException>(conflict.Cause);
        Assert.Contains(die.Suppressed, item => item.Case.Value is Failed failed && ReferenceEquals(conflict, failed.Error));
        Assert.Equal(0, await store.Count());
    }
}
