using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using Moderato.Results;

namespace Moderato.Effects.Internal;

/// <summary>The lifetime of one scope (Gen block or <c>Use</c>): its body's token, its release actions and its children's defects.</summary>
internal sealed class Releases<R>(Cancellation bodyCancellation)
{
    private readonly List<Func<ExitCase, Effect<Unit, Never, R>>> actions = [];
    private readonly Lock gate = new();
    private ImmutableArray<Outcome<Unit, Never>>.Builder? childDefects;
    private bool bodyEnded;

    /// <summary>The context the scope's body runs with.</summary>
    internal RunContext BodyContext { get; } = new(bodyCancellation.Token);

    /// <summary>Records a forked child's defect and cancels the body (D10); false once the body has ended.</summary>
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
            if (childDefects is { Count: > 0 }) body = AfterChildDefects(body, childDefects.ToImmutable());
            // Any cancellation of the body may have left a callback failure.
            body = bodyCancellation.WithCallbackFailure(body);
            if (this.actions.Count == 0) return body;
            actions = [.. this.actions];
            this.actions.Clear();
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
                // A successful release may still carry failures, such as a dead Race loser.
                carried.AddRange(released.Suppressed);
                carriedOmitted += released.Omitted;
            }
        }

        var outcome = defects.Count == 0 ? body : Combine(body, defects.ToImmutable());
        return carried.Count == 0 && carriedOmitted == 0
            ? outcome
            : outcome.WithSuppressed(outcome.Suppressed.AddRange(carried), outcome.Omitted + carriedOmitted);
    }

    /// <summary>The first child defect is the outcome, with the body's later work as context.</summary>
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

        // A joined child's defect also reached the body, so it is not counted twice.
        var sameDefect = body.Kind == OutcomeKind.Die && defects.Any(d => ReferenceEquals(d.Defect!.SourceException, body.Defect!.SourceException));
        if (!sameDefect && ExitCases.FailureOf(body) is { } bodyFailure && body.Kind != OutcomeKind.Interrupted)
            suppressed.Add(new Suppressed(bodyFailure, body.Kind == OutcomeKind.Die && !body.Consequential ? Relation.Parallel : Relation.Consequence));
        suppressed.AddRange(body.Suppressed);
        return Outcome<T, E>.Die(first.Defect!, suppressed.ToImmutable(), omitted: body.Omitted + defects.Sum(static d => d.Omitted));
    }

    // Each failed release keeps its own suppressed failures.
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

        // An independent release defect outranks a body defect caused by cancellation.
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
