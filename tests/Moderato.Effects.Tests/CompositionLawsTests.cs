using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class CompositionLawsTests
{
    [Theory]
    [InlineData("ok")]
    [InlineData("err")]
    [InlineData("die")]
    [InlineData("interrupted")]
    public async Task AndThen_TotalBinder_LeftUnitPreservesPublicExit(string kind)
    {
        // Given
        // Observation: public Exit and ordered context; the binder itself does not throw.
        var defect = new IOException("binder result");
        Effect<int, string, EmptyEnvironment> Bind(int value) => From(ResultOf(kind, value + 1, defect));
        Effect<int, string, EmptyEnvironment> pure = Effect<EmptyEnvironment>.Succeed(5);

        // When
        var left = await pure.AndThen(Bind).RunExitAsync(CancellationToken.None);
        var right = await Bind(5).RunExitAsync(CancellationToken.None);

        // Then
        SameExit(right, left);
        if (kind == "ok") Assert.Equal<Exit<int, string>>(new Ok<int>(6), left);
    }

    [Fact]
    public async Task AndThen_ThrowingBinder_LeftUnitDoesNotEquateConstructionAndExecution()
    {
        // Given
        // Observation: exception timing is observable, so this binder is outside left unit's scope.
        var defect = new IOException("binder");
        Effect<int, string, EmptyEnvironment> Bind(int _) => throw defect;
        Effect<int, string, EmptyEnvironment> pure = Effect<EmptyEnvironment>.Succeed(5);

        // When
        var composed = pure.AndThen(Bind);
        var direct = Assert.Throws<IOException>(() => Bind(5));
        var exit = await composed.RunExitAsync(CancellationToken.None);

        // Then
        Assert.Same(defect, direct);
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(defect, die.Exception);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("err")]
    [InlineData("die")]
    [InlineData("interrupted")]
    public async Task AndThen_PureRightUnit_PreservesPublicExit(string kind)
    {
        // Given
        // Observation: public Exit, including ordered Die context and omitted count.
        var source = From(ResultOf(kind, 5, new IOException("source")));

        // When
        var identity = await source.AndThen<int>(static value => Effect<EmptyEnvironment>.Succeed(value)).RunExitAsync(CancellationToken.None);
        var original = await source.RunExitAsync(CancellationToken.None);

        // Then
        SameExit(original, identity);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("err")]
    [InlineData("die")]
    [InlineData("interrupted")]
    [InlineData("middleErr")]
    [InlineData("lastErr")]
    public async Task AndThen_TotalBinders_AssociatesPublicExit(string kind)
    {
        // Given
        // Observation: public Exit and binder order, for valid effects and nonthrowing binders.
        var trace = new List<int>();
        var source = From(ResultOf(kind is "middleErr" or "lastErr" ? "ok" : kind, 5, new IOException("source")));
        Effect<int, string, EmptyEnvironment> F(int value) { trace.Add(value); return kind == "middleErr" ? Effect<EmptyEnvironment>.Fail("middle") : Effect<EmptyEnvironment>.Succeed(value * 2); }
        Effect<int, string, EmptyEnvironment> G(int value) { trace.Add(value); return kind == "lastErr" ? Effect<EmptyEnvironment>.Fail("last") : Effect<EmptyEnvironment>.Succeed(value + 3); }

        // When
        var left = await source.AndThen(F).AndThen(G).RunExitAsync(CancellationToken.None);
        var leftTrace = trace.ToArray();
        trace.Clear();
        var right = await source.AndThen(value => F(value).AndThen(G)).RunExitAsync(CancellationToken.None);

        // Then
        SameExit(left, right);
        Assert.Equal(leftTrace, trace);
        Assert.Equal(kind is "ok" or "lastErr" ? new[] { 5, 10 } : kind == "middleErr" ? [5] : [], trace);
        if (kind == "middleErr") Assert.Equal<Exit<int, string>>(new Err<string>("middle"), left);
        if (kind == "lastErr") Assert.Equal<Exit<int, string>>(new Err<string>("last"), left);
        if (kind == "ok") Assert.Equal<Exit<int, string>>(new Ok<int>(13), left);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 2, 3)]
    [InlineData(60, 60, 60)]
    [InlineData(100, 1, 1)]
    public async Task AndThen_ContextCrossesRetentionLimit_AssociatesOrderedContext(int a, int b, int c)
    {
        // Given
        // Observation: public Die identity, context order and omissions; internal origin ranks are not compared.
        var defect = new IOException("last");
        var firstContext = Context("a", a);
        var secondContext = Context("b", b);
        var lastContext = Context("c", c);
        var first = From(Outcome<int, string>.Ok(1).WithSuppressed(firstContext, 2));
        Effect<int, string, EmptyEnvironment> F(int value) => From(Outcome<int, string>.Ok(value + 1).WithSuppressed(secondContext, 3));
        Effect<int, string, EmptyEnvironment> G(int _) => From(Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(defect), lastContext, omitted: 5));

        // When
        var left = await first.AndThen(F).AndThen(G).RunExitAsync(CancellationToken.None);
        var right = await first.AndThen(value => F(value).AndThen(G)).RunExitAsync(CancellationToken.None);

        // Then
        SameExit(left, right);
        Assert.True(left.TryGetValue(out Die? die));
        Assert.Same(defect, die.Exception);
        Assert.Equal(firstContext.Concat(secondContext).Concat(lastContext).Take(100), die.Suppressed);
        Assert.Equal(10 + Math.Max(0, a + b + c - 100), die.OmittedSuppressed);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("err")]
    [InlineData("die")]
    [InlineData("interrupted")]
    public async Task Map_TotalFunctions_IdentityAndCompositionPreservePublicExit(string kind)
    {
        // Given
        // Observation: public Exit and its context, with pure total functions.
        var source = From(ResultOf(kind, 5, new IOException("source")));
        static int F(int value) => value * 2;
        static int G(int value) => value + 3;

        // When
        var original = await source.RunExitAsync(CancellationToken.None);
        var identity = await source.Map(static value => value).RunExitAsync(CancellationToken.None);
        var sequential = await source.Map(F).Map(G).RunExitAsync(CancellationToken.None);
        var composed = await source.Map(value => G(F(value))).RunExitAsync(CancellationToken.None);

        // Then
        SameExit(original, identity);
        SameExit(sequential, composed);
        if (kind == "ok") Assert.Equal<Exit<int, string>>(new Ok<int>(13), composed);
    }

    [Theory]
    [InlineData("ok", false, 0)]
    [InlineData("ok", false, 128)]
    [InlineData("ok", true, 0)]
    [InlineData("ok", true, 128)]
    [InlineData("err", false, 0)]
    [InlineData("err", false, 128)]
    [InlineData("err", true, 0)]
    [InlineData("err", true, 128)]
    [InlineData("die", false, 0)]
    [InlineData("die", false, 128)]
    [InlineData("die", true, 0)]
    [InlineData("die", true, 128)]
    [InlineData("interrupted", false, 0)]
    [InlineData("interrupted", false, 128)]
    [InlineData("interrupted", true, 0)]
    [InlineData("interrupted", true, 128)]
    public async Task MapErr_TotalFunctions_IdentityAndCompositionPreservePublicExit(string kind, bool pending, int padding)
    {
        // Given
        // A common failing observer also exposes context retained by successful and expected-error outcomes.
        var defect = new IOException("source");
        var observer = new IOException("observer");
        var outcome = ResultOf(kind, 5, defect);
        var gate = new TaskCompletionSource<Outcome<int, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Effect<int, string, EmptyEnvironment>((_, _) => pending ? new(gate.Task) : new(outcome));
        for (var i = 0; i < padding; i++) source = source.Map(static value => value);
        static int F(string error) => error?.Length ?? 0;
        static string G(int error) => "code:" + error.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // When
        var original = source.RunExitAsync(CancellationToken.None);
        var identity = source.MapErr(static error => error).RunExitAsync(CancellationToken.None);
        var sequential = source.MapErr(F).MapErr(G).RunExitAsync(CancellationToken.None);
        var composed = source.MapErr(error => G(F(error))).RunExitAsync(CancellationToken.None);
        var originalObserved = source.Map<int>(_ => throw observer).MapErr<string>(_ => throw observer).RunExitAsync(CancellationToken.None);
        var identityObserved = source.MapErr(static error => error)
            .Map<int>(_ => throw observer).MapErr<string>(_ => throw observer).RunExitAsync(CancellationToken.None);
        var sequentialObserved = source.MapErr(F).MapErr(G)
            .Map<int>(_ => throw observer).MapErr<string>(_ => throw observer).RunExitAsync(CancellationToken.None);
        var composedObserved = source.MapErr(error => G(F(error)))
            .Map<int>(_ => throw observer).MapErr<string>(_ => throw observer).RunExitAsync(CancellationToken.None);
        bool[] completed = [original.IsCompleted, identity.IsCompleted, sequential.IsCompleted, composed.IsCompleted,
            originalObserved.IsCompleted, identityObserved.IsCompleted, sequentialObserved.IsCompleted, composedObserved.IsCompleted];
        gate.SetResult(outcome);
        var exits = await Task.WhenAll(original.AsTask(), identity.AsTask(), sequential.AsTask(), composed.AsTask(),
            originalObserved.AsTask(), identityObserved.AsTask(), sequentialObserved.AsTask(), composedObserved.AsTask())
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.All(completed, value => Assert.Equal(!pending, value));
        SameExit(exits[0], exits[1]);
        SameExit(exits[2], exits[3]);
        SameExit(exits[4], exits[5]);
        SameExit(exits[6], exits[7]);
        if (kind == "err") Assert.Equal<Exit<int, string>>(new Err<string>("code:5"), exits[3]);
        if (kind == "ok") Assert.Equal<Exit<int, string>>(new Ok<int>(5), exits[3]);
        if (kind != "interrupted")
        {
            var observed = Assert.IsType<Die>(exits[7].Value);
            Assert.Same(kind == "die" ? defect : observer, observed.Exception);
            var context = Context("source", 2);
            Assert.Equal(kind == "err" ? [new Suppressed(new Failed("code:5"), Relation.Sequential), .. context] : context, observed.Suppressed);
            Assert.Equal(3, observed.OmittedSuppressed);
        }
    }

    [Theory]
    [InlineData("ok", false, 0)]
    [InlineData("ok", false, 128)]
    [InlineData("ok", true, 0)]
    [InlineData("ok", true, 128)]
    [InlineData("err", false, 0)]
    [InlineData("err", false, 128)]
    [InlineData("err", true, 0)]
    [InlineData("err", true, 128)]
    [InlineData("die", false, 0)]
    [InlineData("die", false, 128)]
    [InlineData("die", true, 0)]
    [InlineData("die", true, 128)]
    [InlineData("interrupted", false, 0)]
    [InlineData("interrupted", false, 128)]
    [InlineData("interrupted", true, 0)]
    [InlineData("interrupted", true, 128)]
    public async Task MapAndMapErr_TotalIndependentFunctions_CommutePublicExitAndContext(string kind, bool pending, int padding)
    {
        // Given
        var defect = new IOException("source");
        var observer = new IOException("observer");
        var outcome = ResultOf(kind, 5, defect);
        var gate = new TaskCompletionSource<Outcome<int, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Effect<int, string, EmptyEnvironment>((_, _) => pending ? new(gate.Task) : new(outcome));
        for (var i = 0; i < padding; i++) source = source.Map(static value => value);
        static long F(int value) => (long)value + 7;
        static int G(string error) => error?.Length ?? 0;

        // When
        var successFirst = source.Map(F).MapErr(G).RunExitAsync(CancellationToken.None);
        var errorFirst = source.MapErr(G).Map(F).RunExitAsync(CancellationToken.None);
        var successFirstObserved = source.Map(F).MapErr(G)
            .Map<long>(_ => throw observer).MapErr<int>(_ => throw observer).RunExitAsync(CancellationToken.None);
        var errorFirstObserved = source.MapErr(G).Map(F)
            .Map<long>(_ => throw observer).MapErr<int>(_ => throw observer).RunExitAsync(CancellationToken.None);
        bool[] completed = [successFirst.IsCompleted, errorFirst.IsCompleted, successFirstObserved.IsCompleted, errorFirstObserved.IsCompleted];
        gate.SetResult(outcome);
        var exits = await Task.WhenAll(successFirst.AsTask(), errorFirst.AsTask(), successFirstObserved.AsTask(), errorFirstObserved.AsTask())
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.All(completed, value => Assert.Equal(!pending, value));
        SameExit(exits[0], exits[1]);
        SameExit(exits[2], exits[3]);
        if (kind == "ok") Assert.Equal<Exit<long, int>>(new Ok<long>(12), exits[0]);
        if (kind == "err") Assert.Equal<Exit<long, int>>(new Err<int>(5), exits[0]);
        if (kind != "interrupted")
        {
            var observed = Assert.IsType<Die>(exits[3].Value);
            Assert.Same(kind == "die" ? defect : observer, observed.Exception);
            var context = Context("source", 2);
            Assert.Equal(kind == "err" ? [new Suppressed(new Failed(5), Relation.Sequential), .. context] : context, observed.Suppressed);
            Assert.Equal(3, observed.OmittedSuppressed);
        }
    }

    [Theory]
    [InlineData("ok", false, 0)]
    [InlineData("ok", false, 128)]
    [InlineData("ok", true, 0)]
    [InlineData("ok", true, 128)]
    [InlineData("err", false, 0)]
    [InlineData("err", false, 128)]
    [InlineData("err", true, 0)]
    [InlineData("err", true, 128)]
    [InlineData("die", false, 0)]
    [InlineData("die", false, 128)]
    [InlineData("die", true, 0)]
    [InlineData("die", true, 128)]
    [InlineData("interrupted", false, 0)]
    [InlineData("interrupted", false, 128)]
    [InlineData("interrupted", true, 0)]
    [InlineData("interrupted", true, 128)]
    public async Task MapErr_SequentialMappers_RunsOnceInOrderOnlyForErr(string kind, bool pending, int padding)
    {
        // Given
        var calls = new List<(string Mapper, object? Value)>();
        var outcome = ResultOf(kind, 5, new IOException("source"));
        var gate = new TaskCompletionSource<Outcome<int, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Effect<int, string, EmptyEnvironment>((_, _) => pending ? new(gate.Task) : new(outcome));
        for (var i = 0; i < padding; i++) source = source.Map(static value => value);
        int First(string error) { calls.Add(("first", error)); return error?.Length ?? 0; }
        string Second(int error) { calls.Add(("second", error)); return error.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        var effect = source.MapErr(First).MapErr(Second);
        Assert.Empty(calls);

        // When
        var running = effect.RunExitAsync(CancellationToken.None);
        var completed = running.IsCompleted;
        gate.SetResult(outcome);
        var exit = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(!pending, completed);
        Assert.Equal(kind == "err" ? [("first", (object?)"error"), ("second", 5)] : [], calls);
        if (kind == "err") Assert.Equal<Exit<int, string>>(new Err<string>("5"), exit);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SelectMany_SelectorAndProjection_MatchesAndThenWithOrderedContext(bool selectorDies, bool projectionThrows)
    {
        // Given
        // Observation: public Exit and ordered context, including a projection that throws at execution.
        var defect = new IOException("selected or projected");
        var a = Context("source", 60);
        var b = Context("selected", 60);
        var source = From(Outcome<int, string>.Ok(5).WithSuppressed(a, 2));
        Effect<int, string, EmptyEnvironment> Select(int value) => From((selectorDies
            ? Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(defect))
            : Outcome<int, string>.Ok(value * 2)).WithSuppressed(b, 3));
        var arguments = new List<(int, int)>();
        int Project(int x, int y) { arguments.Add((x, y)); return projectionThrows ? throw defect : x + y; }
        var values = new List<int>();
        int Finish(int value) { values.Add(value); throw defect; }

        // When
        var query = await source.SelectMany(Select, Project).Map(Finish).RunExitAsync(CancellationToken.None);
        var bound = await source.AndThen(x => Select(x).Map(y => Project(x, y))).Map(Finish).RunExitAsync(CancellationToken.None);

        // Then
        SameExit(query, bound);
        Assert.True(query.TryGetValue(out Die? die));
        Assert.Same(defect, die.Exception);
        Assert.Equal(a.Concat(b).Take(100), die.Suppressed);
        Assert.Equal(25, die.OmittedSuppressed);
        Assert.Equal(selectorDies ? Array.Empty<(int, int)>() : [(5, 10), (5, 10)], arguments);
        Assert.Equal(selectorDies || projectionThrows ? Array.Empty<int>() : [15, 15], values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AndThrough_Check_MatchesAndThenKeepingTheOriginalValue(bool checkDies)
    {
        // Given
        // Observation: public Exit and context observed by a failing continuation after a successful check.
        var defect = new IOException("check or continuation");
        var a = Context("source", 1);
        var b = Context("check", 1);
        var source = From(Outcome<int, string>.Ok(5).WithSuppressed(a, 0));
        Effect<int, string, EmptyEnvironment> Check(int _) => From((checkDies
            ? Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(defect))
            : Outcome<int, string>.Ok(999)).WithSuppressed(b, 0));
        var values = new List<int>();
        int End(int value) { values.Add(value); throw defect; }

        // When
        var direct = await source.AndThrough(Check).Map(End).RunExitAsync(CancellationToken.None);
        var expanded = await source.AndThen(x => Check(x).Map(_ => x)).Map(End).RunExitAsync(CancellationToken.None);

        // Then
        SameExit(direct, expanded);
        Assert.True(direct.TryGetValue(out Die? die));
        Assert.Equal(a.Concat(b), die.Suppressed);
        Assert.Equal(checkDies ? Array.Empty<int>() : [5, 5], values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrElse_TotalRecovery_AssociatesPublicExitAndRecoveryOrder(bool dies)
    {
        // Given
        // Observation: recovered context is consumed; observe public Exit and the order of recovery calls.
        var defect = new IOException("recovery");
        var trace = new List<string>();
        var source = From(Outcome<int, string>.Err("a").WithSuppressed(Context("source", 1), 0));
        Effect<int, string, EmptyEnvironment> F(string error)
        {
            trace.Add(error);
            return From(Outcome<int, string>.Err(error + "b").WithSuppressed(Context("middle", 1), 0));
        }
        Effect<int, string, EmptyEnvironment> G(string error)
        {
            trace.Add(error);
            return From((dies ? Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(defect)) : Outcome<int, string>.Ok(error.Length))
                .WithSuppressed(Context("last", 1), 0));
        }

        // When
        var left = await source.OrElse(F).OrElse(G).RunExitAsync(CancellationToken.None);
        var leftTrace = trace.ToArray();
        trace.Clear();
        var right = await source.OrElse(error => F(error).OrElse(G)).RunExitAsync(CancellationToken.None);

        // Then
        SameExit(left, right);
        Assert.Equal(new[] { "a", "ab" }, leftTrace);
        Assert.Equal(leftTrace, trace);
        if (dies)
        {
            Assert.True(left.TryGetValue(out Die? die));
            Assert.Equal(Context("last", 1), die.Suppressed);
        }
        else
        {
            Assert.Equal<Exit<int, string>>(new Ok<int>(2), left);
        }
    }

    [Fact]
    public async Task Provide_TotalEnvironmentBuilders_ObeysContravariantIdentityAndComposition()
    {
        // Given
        // Observation: public Exit and environment-builder order, with total builders.
        var trace = new List<string>();
        var source = Effect<int>.Access(value => { trace.Add("run"); return value * 2; });
        int F(string value) { trace.Add("f"); return value.Length; }
        string G(string[] values) { trace.Add("g"); return string.Concat(values); }

        // When
        var original = await source.RunExitAsync(3, CancellationToken.None);
        var identity = await source.Provide<int>(static value => value).RunExitAsync(3, CancellationToken.None);
        trace.Clear();
        var sequential = await source.Provide<string>(F).Provide<string[]>(G).RunExitAsync(["ab", "c"], CancellationToken.None);
        var sequentialTrace = trace.ToArray();
        trace.Clear();
        var composed = await source.Provide<string[]>(values => F(G(values))).RunExitAsync(["ab", "c"], CancellationToken.None);

        // Then
        Assert.Equal(original, identity);
        Assert.Equal<Exit<int, Never>>(new Ok<int>(6), identity);
        Assert.Equal(sequential, composed);
        Assert.Equal<Exit<int, Never>>(new Ok<int>(6), composed);
        Assert.Equal(new[] { "g", "f", "run" }, sequentialTrace);
        Assert.Equal(sequentialTrace, trace);
    }

    private static ImmutableArray<Suppressed> Context(string prefix, int count)
        => [.. Enumerable.Range(0, count).Select(i => new Suppressed(new Failed($"{prefix}{i}"), Relation.Sequential))];

    private static Effect<int, string, EmptyEnvironment> From(Outcome<int, string> outcome) => new((_, _) => new(outcome));

    private static Outcome<int, string> ResultOf(string kind, int value, Exception defect) => (kind switch
    {
        "ok" => Outcome<int, string>.Ok(value),
        "err" => Outcome<int, string>.Err("error"),
        "die" => Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(defect)),
        _ => Outcome<int, string>.Interrupted(),
    }).WithSuppressed(Context("source", 2), 3);

    private static void SameExit<T, E>(Exit<T, E> expected, Exit<T, E> actual)
    {
        if (((IUnion)expected).Value is Die left)
        {
            Assert.True(actual.TryGetValue(out Die? right));
            Assert.Same(left.Exception, right.Exception);
            Assert.Equal(left.Suppressed, right.Suppressed);
            Assert.Equal(left.OmittedSuppressed, right.OmittedSuppressed);
        }
        else
        {
            Assert.Equal(((IUnion)expected).Value, ((IUnion)actual).Value);
        }
    }
}
