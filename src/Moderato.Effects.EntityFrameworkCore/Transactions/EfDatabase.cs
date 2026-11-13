using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moderato.Results;

namespace Moderato.Effects.EntityFrameworkCore;

/// <summary>Runs effects in an owned EF context and transaction.</summary>
public static class EfDatabase<R>
{
    /// <summary>Commits a successful body after its children and releases have completed.</summary>
    /// <param name="contexts">The borrowed context factory.</param>
    /// <returns>An effect retaining unconfirmed commits in its error union.</returns>
    public static Effect<T, E, R> Transaction<TContext, T, E, RBody>(
        Func<R, IDbContextFactory<TContext>> contexts,
        Func<R, TContext, RBody> environment,
        Effect<T, E, RBody> body,
        TryMapDatabaseFailure<E>? commitFailure = null)
        where TContext : DbContext
        where E : IUnionCase<E, DatabaseFailure>
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(environment);
        // The body is first composed per run, which would reject a default effect only after a context was created.
        _ = body.WidenEnv<RBody>();
        Resource<TContext, E, R> context = Resource.Make(
            Effect<R>.AccessAsync<TContext>(async (env, token) =>
                await contexts(env).CreateDbContextAsync(token).ConfigureAwait(false)),
            (db, _) => Effect<R>.Async<Unit>(async _ =>
            {
                await db.DisposeAsync().ConfigureAwait(false);
                return Unit.Value;
            }));

        return context.Use(db =>
        {
            Effect<Lease, E, R> begin = Effect<R>.Async<Lease>(async token =>
            {
                if (db.Database.CreateExecutionStrategy().RetriesOnFailure)
                    throw new NotSupportedException("Retrying execution strategies require an explicit transaction retry policy.");
                if (db.Database.CurrentTransaction is not null)
                    throw new InvalidOperationException("The factory must return a context without an existing transaction.");
                return new(await db.Database.BeginTransactionAsync(token).ConfigureAwait(false));
            });

            return Resource.Make(begin, (lease, _) => Scope<Never, R>.Gen(async cleanup =>
            {
                cleanup.Defer(_ => Effect<R>.Async<Unit>(async _ =>
                {
                    await lease.Transaction.DisposeAsync().ConfigureAwait(false);
                    return Unit.Value;
                }));
                if (!lease.Committed)
                {
                    await cleanup.Run(Effect<R>.Async<Unit>(async _ =>
                    {
                        await lease.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        return Unit.Value;
                    })).ConfigureAwait(false);
                }
                return Unit.Value;
            })).Use(lease => body.Provide<R>(env => environment(env, db)).AndThen(value =>
                Effect<R>.FromResultAsync<T, DatabaseFailure>(async token =>
                {
                    token.ThrowIfCancellationRequested();
                    var transaction = lease.Transaction.GetDbTransaction();
                    try
                    {
                        await lease.Transaction.CommitAsync(token).ConfigureAwait(false);
                        lease.Committed = true;
                        return new Ok<T>(value);
                    }
                    catch (Exception exception) when (exception is DbException or IOException or OperationCanceledException)
                    {
                        return new Err<DatabaseFailure>(DatabaseFailures.FromCommit(exception, transaction));
                    }
                }).MapErr(failure =>
                {
                    if (commitFailure is not null && commitFailure(failure, out var error)) return error;
                    return failure.Value is CommitNotConfirmed ? E.From(failure) : DatabaseFailures.Rethrow<E>(failure.Cause);
                })));
        });
    }

    private sealed class Lease(IDbContextTransaction transaction)
    {
        internal IDbContextTransaction Transaction { get; } = transaction;
        internal bool Committed { get; set; }
    }
}
