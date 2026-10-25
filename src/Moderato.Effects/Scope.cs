using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

/// <summary>
/// The handle a Gen block uses to run effects. An Err ends the block, and its <c>finally</c> blocks and <c>using</c> declarations still run.
/// Use it only inside its own block, from one logical flow at a time.
/// </summary>
/// <typeparam name="E">The error the block can fail with.</typeparam>
/// <typeparam name="R">The environment the block runs with.</typeparam>
public sealed class Scope<E, R>
{
    private readonly RunContext _context;
    private readonly Releases<R> _releases = new();
    private ImmutableArray<Suppressed>.Builder? _suppressed;
    private bool _failed;
    private E _error = default!;
    private int _busy;
    private bool _closed;

    private Scope(R env, RunContext context)
    {
        Env = env;
        _context = context;
    }

    /// <summary>The environment the block runs with.</summary>
    public R Env { get; }

    /// <summary>The token the block runs with.</summary>
    public CancellationToken CancellationToken => _context.CancellationToken;

    /// <summary>An effect that runs the block each time it runs.</summary>
    /// <param name="body">The block.</param>
    /// <returns>An effect whose value is what the block returns.</returns>
    public static Effect<T, E, R> Gen<T>(Func<Scope<E, R>, ValueTask<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new((env, context) => new Scope<E, R>(env, context).RunBodyAsync(body));
    }

    /// <summary>Runs an effect and returns its value. An Err ends the block.</summary>
    /// <param name="effect">The effect.</param>
    /// <returns>The value.</returns>
    public ValueTask<T> Run<T>(Effect<T, E, R> effect)
    {
        Enter();
        ValueTask<Outcome<T, E>> task;
        try
        {
            ThrowIfFailed();
            task = effect.Run(Env, _context);
        }
        catch
        {
            Leave();
            throw;
        }

        if (!task.IsCompletedSuccessfully) return AwaitAsync(task);
        Leave();
        return new(Unwrap(task.Result));

        async ValueTask<T> AwaitAsync(ValueTask<Outcome<T, E>> pending)
        {
            Outcome<T, E> outcome;
            try
            {
                outcome = await pending.ConfigureAwait(false);
            }
            finally
            {
                Leave();
            }

            return Unwrap(outcome);
        }
    }

    /// <summary>Returns the value of a result. An Err ends the block.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The value.</returns>
    public T Get<T>(Result<T, E> result)
    {
        Enter();
        try
        {
            ThrowIfFailed();
            if (result.TryGetOk(out var value)) return value;
            if (result.TryGetErr(out var error)) throw Record(error);
            throw ThrowHelper.UninitializedResultException();
        }
        finally
        {
            Leave();
        }
    }

    /// <summary>Ends the block with the error. Write it as <c>throw scope.Fail(error)</c> so the compiler knows the code after it does not run.</summary>
    /// <param name="error">The error.</param>
    /// <returns>Never returns.</returns>
    [DoesNotReturn]
    public Exception Fail(E error)
    {
        Enter();
        ScopeFailure failure;
        try
        {
            failure = Record(error);
        }
        finally
        {
            Leave();
        }

        throw failure;
    }

    /// <summary>Registers a release action that runs when the block ends, however it ends, in reverse order of registration.</summary>
    /// <param name="release">Builds the release from how the block ended.</param>
    public void Defer(Func<ExitCase, Effect<Unit, Never, R>> release)
    {
        ArgumentNullException.ThrowIfNull(release);
        Enter();
        try
        {
            _releases.Add(release);
        }
        finally
        {
            Leave();
        }
    }

    private async ValueTask<Outcome<T, E>> RunBodyAsync<T>(Func<Scope<E, R>, ValueTask<T>> body)
    {
        Outcome<T, E> outcome;
        try
        {
            var value = await body(this).ConfigureAwait(false);
            outcome = _failed ? Outcome<T, E>.Err(_error) : Outcome<T, E>.Ok(value);
        }
        catch (ScopeFailure failure) when (ReferenceEquals(failure.Owner, this))
        {
            outcome = Outcome<T, E>.Err(_error);
        }
        catch (Exception exception)
        {
            outcome = Defects.From<T, E>(exception, _context);
            // An Err that came first is kept as context for the defect or interruption that replaced it.
            if (_failed) AddSuppressed(new Suppressed(ExitCases.Failed(_error), Relation.Sequential));
        }

        _closed = true;
        if (_suppressed is { Count: > 0 }) outcome = outcome.After(_suppressed.ToImmutable());
        return await _releases.ReleaseAsync(outcome, Env).ConfigureAwait(false);
    }

    private T Unwrap<T>(Outcome<T, E> outcome)
    {
        if (!outcome.Suppressed.IsEmpty) AddSuppressed(outcome.Suppressed);

        switch (outcome.Kind)
        {
            case OutcomeKind.Ok:
                return outcome.Value;
            case OutcomeKind.Err:
                throw Record(outcome.Error);
            case OutcomeKind.Die:
                outcome.Defect!.Throw();
                return default!;
            default:
                throw new OperationCanceledException(_context.CancellationToken);
        }
    }

    private ScopeFailure Record(E error)
    {
        if (!_failed)
        {
            _failed = true;
            _error = error;
        }
        else
        {
            AddSuppressed(new Suppressed(ExitCases.Failed(error), Relation.Sequential));
        }

        return new ScopeFailure(this, _error);
    }

    private void ThrowIfFailed()
    {
        // F20: once an Err has ended the block, later steps must not run, even if a catch swallowed the ScopeFailure.
        if (_failed) throw new ScopeFailure(this, _error);
    }

    private void Enter()
    {
        if (_closed) throw new InvalidOperationException("The Gen block has ended; its scope can no longer be used.");
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            throw new InvalidOperationException(
                "The scope is already running a step. Use it from one flow at a time, and not from a nested Gen block; run effects in parallel with Effect.All instead.");
        }
    }

    private void Leave() => Volatile.Write(ref _busy, 0);

    private void AddSuppressed(Suppressed failure) => (_suppressed ??= ImmutableArray.CreateBuilder<Suppressed>()).Add(failure);

    private void AddSuppressed(ImmutableArray<Suppressed> failures) => (_suppressed ??= ImmutableArray.CreateBuilder<Suppressed>()).AddRange(failures);
}

/// <summary>Scope operations that accept a case of the scope's error union.</summary>
public static class ScopeExtensions
{
    /// <summary>Runs an effect whose error is a case of the scope's error union. An Err ends the block.</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="effect">The effect.</param>
    /// <returns>The value.</returns>
    public static ValueTask<T> Run<T, E, ECase, R>(this Scope<E, R> scope, Effect<T, ECase, R> effect)
        where E : IUnionCase<E, ECase>
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.Run(effect.Widen<E>());
    }

    /// <summary>Returns the value of a result whose error is a case of the scope's error union. An Err ends the block.</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="result">The result.</param>
    /// <returns>The value.</returns>
    public static T Get<T, E, ECase, R>(this Scope<E, R> scope, Result<T, ECase> result)
        where E : IUnionCase<E, ECase>
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.Get(result.HasValue ? result.MapErr(static error => E.From(error)) : default);
    }
}
