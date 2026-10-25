using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using Moderato.Results;

namespace Moderato.Effects.Internal;

/// <summary>Runs release actions after a body has finished, in reverse order of registration.</summary>
internal sealed class Releases<R>
{
    private readonly List<Func<ExitCase, Effect<Unit, Never, R>>> _actions = [];
    private readonly Lock _lock = new();

    internal void Add(Func<ExitCase, Effect<Unit, Never, R>> release)
    {
        lock (_lock) _actions.Add(release);
    }

    /// <summary>Releases everything, then decides the final outcome.</summary>
    internal async ValueTask<Outcome<T, E>> ReleaseAsync<T, E>(Outcome<T, E> body, R env)
    {
        Func<ExitCase, Effect<Unit, Never, R>>[] actions;
        lock (_lock)
        {
            if (_actions.Count == 0) return body;
            actions = [.. _actions];
            _actions.Clear();
        }

        // Fixed before any release runs, so a failing release cannot change what the later ones see.
        var exit = ExitCases.From(body);
        // Releases ignore the run's token: a canceled token would stop a rollback from running.
        var context = new RunContext(CancellationToken.None);

        var defects = ImmutableArray.CreateBuilder<ExceptionDispatchInfo>();
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

            if (released.Kind == OutcomeKind.Die) defects.Add(released.Defect!);
        }

        return defects.Count == 0 ? body : Combine(body, defects.ToImmutable());
    }

    private static Outcome<T, E> Combine<T, E>(Outcome<T, E> body, ImmutableArray<ExceptionDispatchInfo> defects)
    {
        var releaseFailures = defects.Select(d => new Suppressed(ExitCases.Die(d), Relation.Sequential));
        if (body.Kind == OutcomeKind.Die && !body.Consequential)
            return Outcome<T, E>.Die(body.Defect!, body.Suppressed.AddRange(releaseFailures), omitted: body.Omitted);

        // A release defect is independent, so it outranks a body defect that only followed from cancellation.
        var suppressed = ImmutableArray.CreateBuilder<Suppressed>();
        if (ExitCases.FailureOf(body) is { } bodyFailure)
            suppressed.Add(new Suppressed(bodyFailure, body.Consequential ? Relation.Consequence : Relation.Sequential));
        suppressed.AddRange(body.Suppressed);
        suppressed.AddRange(releaseFailures.Skip(1));
        return Outcome<T, E>.Die(defects[0], suppressed.ToImmutable(), omitted: body.Omitted);
    }
}

/// <summary>Builds <see cref="ExitCase"/> values from outcomes.</summary>
internal static class ExitCases
{
    private static readonly Succeeded s_succeeded = new();
    private static readonly Interrupted s_interrupted = new();

    internal static ExitCase From<T, E>(Outcome<T, E> outcome) => outcome.Kind switch
    {
        OutcomeKind.Ok => s_succeeded,
        OutcomeKind.Err => new Failed(outcome.Error),
        OutcomeKind.Die => ToDie(outcome),
        _ => s_interrupted,
    };

    /// <summary>The outcome as a suppressed failure, or <see langword="null"/> if it succeeded.</summary>
    internal static ExitCase? FailureOf<T, E>(Outcome<T, E> outcome)
        => outcome.Kind == OutcomeKind.Ok ? null : (ExitCase?)From(outcome);

    internal static Die ToDie<T, E>(Outcome<T, E> outcome)
        => new(outcome.Defect!.SourceException, outcome.Suppressed) { OmittedSuppressed = outcome.Omitted };

    internal static ExitCase Die(ExceptionDispatchInfo defect) => new Die(defect.SourceException, []);

    internal static ExitCase Failed(object? error) => new Failed(error);

    internal static ExitCase Interrupted => s_interrupted;
}
