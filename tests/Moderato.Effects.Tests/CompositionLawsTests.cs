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

    private static void SameExit(Exit<int, string> expected, Exit<int, string> actual)
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
