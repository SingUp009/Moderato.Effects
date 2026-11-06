using Microsoft.EntityFrameworkCore;
using Moderato.Results;

namespace Moderato.Effects.EntityFrameworkCore;

/// <summary>Connects EF saves to the current effect scope.</summary>
public static class DbContextEffects
{
    /// <summary>Saves changes and maps selected update failures to the scope's error.</summary>
    public static ValueTask<int> SaveChangesAsync<E, R>(this DbContext context, Scope<E, R> scope,
        TryMapDatabaseFailure<E>? saveFailure = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scope);
        return scope.Run(Effect<R>.FromResultAsync<int, DatabaseFailure>(async token =>
        {
            try
            {
                return new Ok<int>(await context.SaveChangesAsync(token).ConfigureAwait(false));
            }
            catch (DbUpdateException exception)
            {
                return new Err<DatabaseFailure>(DatabaseFailures.From(exception));
            }
        }).MapErr(failure =>
        {
            if (saveFailure is not null && saveFailure(failure, out var error)) return error;
            return DatabaseFailures.Rethrow<E>(failure.Cause);
        }));
    }
}
