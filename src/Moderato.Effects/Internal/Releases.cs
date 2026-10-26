using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using Moderato.Results;

namespace Moderato.Effects.Internal;

/// <summary>
/// The lifetime of one scope (a Gen block or a <c>Use</c>): the token its body runs with, the release actions to run after it,
/// and the defects of forked children that end it early.
/// </summary>
internal sealed class Releases<R>(Cancellation bodyCancellation)
{
    private readonly List<Func<ExitCase, Effect<Unit, Never, R>>> actions = [];
    private readonly Lock gate = new();
    private ImmutableArray<Outcome<Unit, Never>>.Builder? childDefects;
    private bool bodyEnded;

    /// <summary>The context the scope's body runs with.</summary>
    internal RunContext BodyContext { get; } = new(bodyCancellation.Token);

    /// <summary>
    /// Records the defect of a forked child and cancels the body, so the scope ends with the child's defect (D10).
    /// Returns false once the body has ended; the child's release reports the defect then.
    /// </summary>
    internal bool TryFailFromChild(Outcome<Unit, Never> defect)
    {
        lock (gate)
        {
            if (bodyEnded) return false;
            (childDefects ??= ImmutableArray.CreateBuilder<Outcome<Unit, Never>>()).Add(defect);
        }

        bodyCancellation.TryCancel(CancellationReason.ChildDefect);
        return true;
    }

    internal void Add(Func<ExitCase, Effect<Unit, Never, R>> release)
    {
        lock (gate) actions.Add(release);
    }

    /// <summary>Releases everything, then decides the final outcome.</summary>
    internal async ValueTask<Outcome<T, E>> ReleaseAsync<T, E>(Outcome<T, E> body, R env)
    {
        Func<ExitCase, Effect<Unit, Never, R>>[] actions;
        lock (gate)
        {
            bodyEnded = true;
            if (childDefects is { Count: > 0 }) body = bodyCancellation.WithCallbackFailure(AfterChildDefects(body, childDefects.ToImmutable()));
            if (this.actions.Count == 0) return body;
            actions = [.. this.actions];
            this.actions.Clear();
        }

        // Fixed before any release runs, so a failing release cannot change what the later ones see.
        var exit = ExitCases.From(body);
        // Releases ignore the run's token: a canceled token would stop a rollback from running.
        var context = new RunContext(CancellationToken.None);

        var defects = ImmutableArray.CreateBuilder<Outcome<Unit, Never>>();
        var carried = ImmutableArray.CreateBuilder<Suppressed>();
        var carriedOmitted = 0;
        for (var i = actions.Length - 1; i >= 0; i--)
        {
            Outcome<Unit, Never> released;
            try
            {
                released = await actions[i](exit).Run(env, context).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                released = Defects.From<Unit, Never>(exception, context);
            }

            if (released.Kind == OutcomeKind.Die)
            {
                defects.Add(released);
            }
            else
            {
                // A release that succeeded can still carry failures, such as a Race whose loser died; they go along with the outcome.
                carried.AddRange(released.Suppressed);
                carriedOmitted += released.Omitted;
            }
        }

        var outcome = defects.Count == 0 ? body : Combine(body, defects.ToImmutable());
        return carried.Count == 0 && carriedOmitted == 0
            ? outcome
            : outcome.WithSuppressed(outcome.Suppressed.AddRange(carried), outcome.Omitted + carriedOmitted);
    }

    /// <summary>The first child defect is the outcome; what the body did after being canceled for it is context.</summary>
    private static Outcome<T, E> AfterChildDefects<T, E>(Outcome<T, E> body, ImmutableArray<Outcome<Unit, Never>> defects)
    {
        var first = defects[0];
        var suppressed = ImmutableArray.CreateBuilder<Suppressed>();
        suppressed.AddRange(first.Suppressed);
        foreach (var other in defects.AsSpan()[1..])
        {
            suppressed.Add(new Suppressed(ExitCases.Die(other.Defect!), Relation.Parallel));
            suppressed.AddRange(other.Suppressed);
        }

        // A joined child's defect reaches the body too; it is the same failure, not a second one.
        var sameDefect = body.Kind == OutcomeKind.Die && defects.Any(d => ReferenceEquals(d.Defect!.SourceException, body.Defect!.SourceException));
        if (!sameDefect && ExitCases.FailureOf(body) is { } bodyFailure && body.Kind != OutcomeKind.Interrupted)
            suppressed.Add(new Suppressed(bodyFailure, body.Kind == OutcomeKind.Die && !body.Consequential ? Relation.Parallel : Relation.Consequence));
        suppressed.AddRange(body.Suppressed);
        return Outcome<T, E>.Die(first.Defect!, suppressed.ToImmutable(), omitted: body.Omitted + defects.Sum(static d => d.Omitted));
    }

    // Each failed release brings its own suppressed failures along, so a release that failed twice loses neither.
    private static Outcome<T, E> Combine<T, E>(Outcome<T, E> body, ImmutableArray<Outcome<Unit, Never>> defects)
    {
        var suppressed = ImmutableArray.CreateBuilder<Suppressed>();
        var omitted = body.Omitted + defects.Sum(static d => d.Omitted);
        if (body.Kind == OutcomeKind.Die && !body.Consequential)
        {
            suppressed.AddRange(body.Suppressed);
            AddReleaseFailures(suppressed, defects);
            return Outcome<T, E>.Die(body.Defect!, suppressed.ToImmutable(), omitted: omitted);
        }

        // A release defect is independent, so it outranks a body defect that only followed from cancellation.
        var primary = defects[0];
        if (ExitCases.FailureOf(body) is { } bodyFailure)
            suppressed.Add(new Suppressed(bodyFailure, body.Consequential ? Relation.Consequence : Relation.Sequential));
        suppressed.AddRange(body.Suppressed);
        suppressed.AddRange(primary.Suppressed);
        AddReleaseFailures(suppressed, defects.RemoveAt(0));
        return Outcome<T, E>.Die(primary.Defect!, suppressed.ToImmutable(), omitted: omitted);
    }

    private static void AddReleaseFailures(ImmutableArray<Suppressed>.Builder suppressed, ImmutableArray<Outcome<Unit, Never>> defects)
    {
        foreach (var defect in defects)
        {
            suppressed.Add(new Suppressed(ExitCases.Die(defect.Defect!), Relation.Sequential));
            suppressed.AddRange(defect.Suppressed);
        }
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

    /// <summary>A failure holding the error. A union error is unwrapped to the case it holds, so <c>Failed(NotFound n)</c> patterns match.</summary>
    internal static ExitCase Failed(object? error)
    {
        while (error is System.Runtime.CompilerServices.IUnion union) error = union.Value;
        return new Failed(error);
    }

    internal static ExitCase Interrupted => interrupted;
}
