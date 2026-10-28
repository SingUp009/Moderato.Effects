using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Moderato.Results;

namespace Moderato.Effects.Internal;

/// <summary>The lifetime of one scope (Gen block or <c>Use</c>): its body's token, its release actions and its children's defects.</summary>
internal sealed class Releases<R>(Cancellation bodyCancellation)
{
    // The owner never escapes to user code, so it can be its own gate.
    private List<object>? actions;
    private ImmutableArray<Outcome<Unit, Never>>.Builder? childDefects;
    private bool bodyEnded;

    /// <summary>The context the scope's body runs with.</summary>
    internal RunContext BodyContext { get; } = new(bodyCancellation.Token);

    /// <summary>Records a forked child's defect and cancels the body (D10); false once the body has ended.</summary>
    internal bool TryFailFromChild(Outcome<Unit, Never> defect)
    {
        lock (this)
        {
            if (bodyEnded) return false;
            (childDefects ??= ImmutableArray.CreateBuilder<Outcome<Unit, Never>>()).Add(defect);
        }

        bodyCancellation.TryCancel(CancellationReason.ChildDefect);
        return true;
    }

    internal void Add(Func<ExitCase, Effect<Unit, Never, R>> release)
    {
        lock (this) (actions ??= []).Add(release);
    }

    internal void Add<T>(T value, Func<T, ExitCase, Effect<Unit, Never, R>> release)
    {
        var acquired = new AcquiredRelease<T>(value, release);
        lock (this) (actions ??= []).Add(acquired);
    }

    private interface IAcquiredRelease
    {
        Effect<Unit, Never, R> Invoke(ExitCase exit);
    }

    private sealed class AcquiredRelease<T>(T value, Func<T, ExitCase, Effect<Unit, Never, R>> release) : IAcquiredRelease
    {
        public Effect<Unit, Never, R> Invoke(ExitCase exit) => release(value, exit);
    }

    /// <summary>Releases everything, then decides the final outcome.</summary>
    // Use and Gen each await this internal ValueTask exactly once.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<Outcome<T, E>> ReleaseAsync<T, E>(Outcome<T, E> body, R env)
    {
        List<object> actions;
        lock (this)
        {
            bodyEnded = true;
            if (childDefects is { Count: > 0 }) body = AfterChildDefects(body, childDefects);
            // Any cancellation of the body may have left a callback failure.
            body = bodyCancellation.WithCallbackFailure(body);
            if (this.actions is not { Count: > 0 }) return body;
            actions = this.actions;
            this.actions = null;
        }

        // Fixed before any release runs, so a failing release cannot change what later ones see.
        var exit = ExitCases.From(body);
        // An error that cannot be read is a defect, and the block ends with what its releases were told.
        if (body.Kind == OutcomeKind.Err && exit.Value is Die unreadable)
        {
            body = Outcome<T, E>.Die(ExceptionDispatchInfo.Capture(unreadable.Exception), [.. unreadable.Suppressed, .. body.Suppressed], omitted: body.Omitted);
            exit = ExitCases.From(body);
        }
        // Releases ignore the run's token, which would otherwise stop a rollback.
        var context = new RunContext(CancellationToken.None);

        ImmutableArray<Outcome<Unit, Never>>.Builder? defects = null;
        ImmutableArray<Suppressed>.Builder? carried = null;
        var carriedOmitted = 0;
        for (var i = actions.Count - 1; i >= 0; i--)
        {
            Outcome<Unit, Never> released;
            try
            {
                var action = actions[i];
                var release = action is Func<ExitCase, Effect<Unit, Never, R>> callback
                    ? callback(exit)
                    : ((IAcquiredRelease)action).Invoke(exit);
                released = await release.Run(env, context).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                released = Defects.From<Unit, Never>(exception, context);
            }

            if (released.Kind == OutcomeKind.Die)
            {
                (defects ??= ImmutableArray.CreateBuilder<Outcome<Unit, Never>>()).Add(
                    Outcome<Unit, Never>.Die(released.Defect!, released.Suppressed, omitted: released.Omitted, failureOrder: released.FailureOrder));
            }
            else
            {
                // A successful release may still carry failures, such as a dead Race loser.
                var retained = Math.Min(released.Suppressed.Length, SuppressedLimit.MAX - (carried?.Count ?? 0));
                if (retained > 0)
                    (carried ??= ImmutableArray.CreateBuilder<Suppressed>()).AddRange(released.Suppressed.AsSpan()[..retained]);
                carriedOmitted += released.Omitted + released.Suppressed.Length - retained;
            }
        }

        var outcome = defects is null ? body : Combine(body, defects);
        return carried is null && carriedOmitted == 0
            ? outcome
            : outcome.WithSuppressed(carried?.DrainToImmutable() ?? [], carriedOmitted).After(outcome);
    }

    /// <summary>The first child defect is the outcome, with the body's later work as context.</summary>
    private static Outcome<T, E> AfterChildDefects<T, E>(Outcome<T, E> body, ImmutableArray<Outcome<Unit, Never>>.Builder defects)
    {
        var primary = First(defects);
        var bodyFirst = body.Kind == OutcomeKind.Die && !body.Consequential
            && FailureSequence.Before(body.FailureOrder, defects[primary].FailureOrder);
        var first = bodyFirst ? body.WithTypes<Unit, Never>() : defects[primary];
        if (!bodyFirst && defects.Count == 1
            && (body.Kind is OutcomeKind.Ok or OutcomeKind.Interrupted
                || body.Kind == OutcomeKind.Die && ReferenceEquals(first.Defect!.SourceException, body.Defect!.SourceException)))
        {
            return body.Suppressed.IsEmpty
                ? first.WithTypes<T, E>().WithSuppressed(first.Suppressed, first.Omitted + body.Omitted)
                : first.WithTypes<T, E>().WithSuppressed(body.Suppressed, body.Omitted).After(first);
        }

        var suppressed = ImmutableArray.CreateBuilder<Suppressed>();
        var discarded = 0;
        for (var i = 0; i < defects.Count; i++)
        {
            var other = defects[i];
            if (bodyFirst || i != primary)
            {
                if (suppressed.Count < SuppressedLimit.MAX)
                    suppressed.Add(new Suppressed(ExitCases.Die(other.Defect!), Relation.Parallel));
                else discarded++;
            }
            discarded += AddContext(suppressed, other.Suppressed);
        }

        // A joined child's defect also reached the body, so it is not counted twice.
        var sameDefect = false;
        if (body.Kind == OutcomeKind.Die)
        {
            for (var i = 0; i < defects.Count; i++)
            {
                var defect = defects[i];
                if (!ReferenceEquals(defect.Defect!.SourceException, body.Defect!.SourceException)) continue;
                sameDefect = true;
                break;
            }
        }

        if (!bodyFirst && !sameDefect && ExitCases.FailureOf(body) is { } bodyFailure && body.Kind != OutcomeKind.Interrupted)
        {
            if (suppressed.Count < SuppressedLimit.MAX)
                suppressed.Add(new Suppressed(bodyFailure, !body.Consequential ? Relation.Parallel : Relation.Consequence));
            else discarded++;
        }
        discarded += AddContext(suppressed, body.Suppressed);
        var omitted = 0;
        for (var i = 0; i < defects.Count; i++) omitted = checked(omitted + defects[i].Omitted);
        return first.WithTypes<T, E>().WithSuppressed(suppressed.DrainToImmutable(), body.Omitted + omitted + discarded);
    }

    // Each failed release keeps its own suppressed failures.
    private static Outcome<T, E> Combine<T, E>(Outcome<T, E> body, ImmutableArray<Outcome<Unit, Never>>.Builder defects)
    {
        if (body.Kind == OutcomeKind.Ok && defects.Count == 1)
        {
            var only = defects[0];
            return only.WithTypes<T, E>().After(body);
        }

        var suppressed = ImmutableArray.CreateBuilder<Suppressed>();
        var omitted = 0;
        for (var i = 0; i < defects.Count; i++) omitted = checked(omitted + defects[i].Omitted);
        omitted += body.Omitted;
        var primaryIndex = First(defects);
        if (body.Kind == OutcomeKind.Die && !body.Consequential
            && !FailureSequence.Before(defects[primaryIndex].FailureOrder, body.FailureOrder))
        {
            omitted += AddContext(suppressed, body.Suppressed);
            omitted += AddReleaseFailures(suppressed, defects, null);
            return body.WithSuppressed(suppressed.DrainToImmutable(), omitted);
        }

        // An independent release defect outranks a body defect caused by cancellation.
        var primary = defects[primaryIndex];
        if (ExitCases.FailureOf(body) is { } bodyFailure)
            suppressed.Add(new Suppressed(bodyFailure, body.Consequential ? Relation.Consequence : Relation.Sequential));
        omitted += AddContext(suppressed, body.Suppressed);
        omitted += AddReleaseFailures(suppressed, defects, primaryIndex);
        return primary.WithTypes<T, E>().WithSuppressed(suppressed.DrainToImmutable(), omitted);
    }

    private static int AddReleaseFailures(ImmutableArray<Suppressed>.Builder suppressed, ImmutableArray<Outcome<Unit, Never>>.Builder defects, int? primary)
    {
        var omitted = 0;
        for (var i = 0; i < defects.Count; i++)
        {
            var defect = defects[i];
            if (i != primary)
            {
                if (suppressed.Count < SuppressedLimit.MAX)
                    suppressed.Add(new Suppressed(ExitCases.Die(defect.Defect!), Relation.Sequential));
                else omitted++;
            }
            omitted += AddContext(suppressed, defect.Suppressed);
        }

        return omitted;
    }

    private static int First(ImmutableArray<Outcome<Unit, Never>>.Builder defects)
    {
        var first = 0;
        for (var i = 1; i < defects.Count; i++)
            if (FailureSequence.Before(defects[i].FailureOrder, defects[first].FailureOrder)) first = i;
        return first;
    }

    private static int AddContext(ImmutableArray<Suppressed>.Builder suppressed, ImmutableArray<Suppressed> context)
    {
        var retained = Math.Min(context.Length, SuppressedLimit.MAX - suppressed.Count);
        suppressed.AddRange(context.AsSpan()[..retained]);
        return context.Length - retained;
    }
}

/// <summary>Builds <see cref="ExitCase"/> values from outcomes.</summary>
internal static class ExitCases
{
    private static readonly Succeeded succeeded = new();
    private static readonly Interrupted interrupted = new();

    internal static ExitCase From<T, E>(Outcome<T, E> outcome) => outcome.Kind switch
    {
        OutcomeKind.Ok => succeeded,
        OutcomeKind.Err => Failed(outcome.Error),
        OutcomeKind.Die => ToDie(outcome),
        _ => interrupted,
    };

    /// <summary>The outcome as a suppressed failure, or <see langword="null"/> if it succeeded.</summary>
    internal static ExitCase? FailureOf<T, E>(Outcome<T, E> outcome)
        => outcome.Kind == OutcomeKind.Ok ? null : (ExitCase?)From(outcome);

    internal static Die ToDie<T, E>(Outcome<T, E> outcome)
        => new(outcome.Defect!.SourceException, outcome.Suppressed) { OmittedSuppressed = outcome.Omitted };

    internal static ExitCase Die(ExceptionDispatchInfo defect) => new Die(defect.SourceException, []);

    /// <summary>A failure holding the error, a union unwrapped to its case so <c>Failed(NotFound n)</c> matches.</summary>
    /// <returns>The failure, or a <see cref="Die"/> with the error as context when a union's value throws.</returns>
    internal static ExitCase Failed(object? error)
    {
        try
        {
            while (error is System.Runtime.CompilerServices.IUnion union) error = union.Value;
        }
        catch (Exception exception)
        {
            // The union whose value threw is kept as it is, so nothing reads that value again.
            return new Die(exception, [new Suppressed(new Failed(error), Relation.Sequential)]);
        }

        return new Failed(error);
    }

    internal static Interrupted Interrupted => interrupted;
}
