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
        // Observation: public Exit and executed-step order, with pure description construction.
        var trace = new List<int>();
        var source = From(ResultOf(kind is "middleErr" or "lastErr" ? "ok" : kind, 5, new IOException("source")));
        Effect<int, string, EmptyEnvironment> F(int value) => new((_, _) =>
        {
            trace.Add(value);
            return new(kind == "middleErr" ? Outcome<int, string>.Err("middle") : Outcome<int, string>.Ok(value * 2));
        });
        Effect<int, string, EmptyEnvironment> G(int value) => new((_, _) =>
        {
            trace.Add(value);
            return new(kind == "lastErr" ? Outcome<int, string>.Err("last") : Outcome<int, string>.Ok(value + 3));
        });
        _ = F(5);
        _ = G(10);
        Assert.Empty(trace);

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
        static int F(string error) => error?.Length ?? 0;
        static string G(int error) => "code:" + error.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var original = Pad(source, padding);
        var identity = Pad(source.MapErr(static error => error), padding);
        var sequential = Pad(source.MapErr(F).MapErr(G), padding);
        var composed = Pad(source.MapErr(error => G(F(error))), padding);
        var originalObserved = Pad(source.Map<int>(_ => throw observer).MapErr<string>(_ => throw observer), padding);
        var identityObserved = Pad(source.MapErr(static error => error)
            .Map<int>(_ => throw observer).MapErr<string>(_ => throw observer), padding);
        var sequentialObserved = Pad(source.MapErr(F).MapErr(G)
            .Map<int>(_ => throw observer).MapErr<string>(_ => throw observer), padding);
        var composedObserved = Pad(source.MapErr(error => G(F(error)))
            .Map<int>(_ => throw observer).MapErr<string>(_ => throw observer), padding);

        // When
        var originalRun = original.RunExitAsync(CancellationToken.None);
        var identityRun = identity.RunExitAsync(CancellationToken.None);
        var sequentialRun = sequential.RunExitAsync(CancellationToken.None);
        var composedRun = composed.RunExitAsync(CancellationToken.None);
        var originalObservedRun = originalObserved.RunExitAsync(CancellationToken.None);
        var identityObservedRun = identityObserved.RunExitAsync(CancellationToken.None);
        var sequentialObservedRun = sequentialObserved.RunExitAsync(CancellationToken.None);
        var composedObservedRun = composedObserved.RunExitAsync(CancellationToken.None);
        bool[] completed = [originalRun.IsCompleted, identityRun.IsCompleted, sequentialRun.IsCompleted, composedRun.IsCompleted,
            originalObservedRun.IsCompleted, identityObservedRun.IsCompleted, sequentialObservedRun.IsCompleted, composedObservedRun.IsCompleted];
        gate.SetResult(outcome);
        var exits = await Task.WhenAll(originalRun.AsTask(), identityRun.AsTask(), sequentialRun.AsTask(), composedRun.AsTask(),
            originalObservedRun.AsTask(), identityObservedRun.AsTask(), sequentialObservedRun.AsTask(), composedObservedRun.AsTask())
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
        static long F(int value) => (long)value + 7;
        static int G(string error) => error?.Length ?? 0;

        var successFirst = Pad(source.Map(F).MapErr(G), padding);
        var errorFirst = Pad(source.MapErr(G).Map(F), padding);
        var successFirstObserved = Pad(source.Map(F).MapErr(G)
            .Map<long>(_ => throw observer).MapErr<int>(_ => throw observer), padding);
        var errorFirstObserved = Pad(source.MapErr(G).Map(F)
            .Map<long>(_ => throw observer).MapErr<int>(_ => throw observer), padding);

        // When
        var successFirstRun = successFirst.RunExitAsync(CancellationToken.None);
        var errorFirstRun = errorFirst.RunExitAsync(CancellationToken.None);
        var successFirstObservedRun = successFirstObserved.RunExitAsync(CancellationToken.None);
        var errorFirstObservedRun = errorFirstObserved.RunExitAsync(CancellationToken.None);
        bool[] completed = [successFirstRun.IsCompleted, errorFirstRun.IsCompleted, successFirstObservedRun.IsCompleted, errorFirstObservedRun.IsCompleted];
        gate.SetResult(outcome);
        var exits = await Task.WhenAll(successFirstRun.AsTask(), errorFirstRun.AsTask(), successFirstObservedRun.AsTask(), errorFirstObservedRun.AsTask())
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
        int First(string error) { calls.Add(("first", error)); return error?.Length ?? 0; }
        string Second(int error) { calls.Add(("second", error)); return error.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        var effect = Pad(source.MapErr(First).MapErr(Second), padding);
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
        // Observation: recovered context is consumed; observe public Exit and recovery execution order.
        var defect = new IOException("recovery");
        var trace = new List<string>();
        var source = From(Outcome<int, string>.Err("a").WithSuppressed(Context("source", 1), 0));
        Effect<int, string, EmptyEnvironment> F(string error) => new((_, _) =>
        {
            trace.Add(error);
            return new(Outcome<int, string>.Err(error + "b").WithSuppressed(Context("middle", 1), 0));
        });
        Effect<int, string, EmptyEnvironment> G(string error) => new((_, _) =>
        {
            trace.Add(error);
            return new((dies ? Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(defect)) : Outcome<int, string>.Ok(error.Length))
                .WithSuppressed(Context("last", 1), 0));
        });
        _ = F("a");
        _ = G("ab");
        Assert.Empty(trace);

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

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 128)]
    [InlineData(true, 128)]
    public async Task Provide_PureEnvironmentBuilders_ObeysContravariantIdentityAndComposition(bool pending, int padding)
    {
        // Given
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = Effect<int>.AccessAsync<int>(async (value, _) =>
        {
            if (pending) await gate.Task.ConfigureAwait(false);
            return value * 2;
        });
        static int F(string value) => value.Length;
        static string G(string[] values) => string.Concat(values);
        var original = Pad(source, padding);
        var identity = Pad(source.Provide<int>(static value => value), padding);
        var sequential = Pad(source.Provide<string>(F).Provide<string[]>(G), padding);
        var composed = Pad(source.Provide<string[]>(static values => F(G(values))), padding);

        // When
        var originalRun = original.RunExitAsync(3, CancellationToken.None);
        var identityRun = identity.RunExitAsync(3, CancellationToken.None);
        var sequentialRun = sequential.RunExitAsync(["ab", "c"], CancellationToken.None);
        var composedRun = composed.RunExitAsync(["ab", "c"], CancellationToken.None);
        bool[] completed = [originalRun.IsCompleted, identityRun.IsCompleted, sequentialRun.IsCompleted, composedRun.IsCompleted];
        gate.SetResult();
        var exits = await Task.WhenAll(originalRun.AsTask(), identityRun.AsTask(), sequentialRun.AsTask(), composedRun.AsTask())
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.All(completed, value => Assert.Equal(!pending, value));
        Assert.Equal(exits[0], exits[1]);
        Assert.Equal(exits[2], exits[3]);
        Assert.All(exits, exit => Assert.Equal<Exit<int, Never>>(new Ok<int>(6), exit));
    }

    [Fact]
    public async Task Provide_RecordingEnvironmentBuilders_PreservesTransformationOrder()
    {
        // Given
        // This spy checks evaluation order separately from the pure-builder law cases.
        var trace = new List<string>();
        var source = Effect<int>.Access(value => { trace.Add("run"); return value * 2; });
        int F(string value) { trace.Add("f"); return value.Length; }
        string G(string[] values) { trace.Add("g"); return string.Concat(values); }

        // When
        var sequential = await source.Provide<string>(F).Provide<string[]>(G).RunExitAsync(["ab", "c"], CancellationToken.None);
        var sequentialTrace = trace.ToArray();
        trace.Clear();
        var composed = await source.Provide<string[]>(values => F(G(values))).RunExitAsync(["ab", "c"], CancellationToken.None);

        // Then
        Assert.Equal(sequential, composed);
        Assert.Equal<Exit<int, Never>>(new Ok<int>(6), composed);
        Assert.Equal(new[] { "g", "f", "run" }, sequentialTrace);
        Assert.Equal(sequentialTrace, trace);
    }

    [Theory]
    [InlineData("none", 0)]
    [InlineData("none", 128)]
    [InlineData("source", 0)]
    [InlineData("source", 128)]
    [InlineData("f", 0)]
    [InlineData("f", 128)]
    [InlineData("g", 0)]
    [InlineData("g", 128)]
    public async Task AndThen_ConstantBinders_AssociatesPublicExit(string failure, int padding)
    {
        // Given
        var observer = new IOException("observer");
        Effect<int, string, EmptyEnvironment> source = failure == "source"
            ? Effect<EmptyEnvironment>.Fail("source") : Effect<EmptyEnvironment>.Succeed(5);
        Effect<long, string, EmptyEnvironment> F(int value) => failure == "f"
            ? Effect<EmptyEnvironment>.Fail("f") : Effect<EmptyEnvironment>.Succeed((long)value * 2);
        Effect<string, string, EmptyEnvironment> G(long value) => failure == "g"
            ? Effect<EmptyEnvironment>.Fail("g") : Effect<EmptyEnvironment>.Succeed("value:" + value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Result<string, string> expected = failure switch
        {
            "source" => Result.Err("source"),
            "f" => Result.Err("f"),
            "g" => Result.Err("g"),
            _ => Result.Ok("value:10"),
        };
        var leftCore = source.AndThen(F).AndThen(G);
        var rightCore = source.AndThen(value => F(value).AndThen(G));
        var left = Pad(leftCore, padding);
        var right = Pad(rightCore, padding);
        var folded = Effect<EmptyEnvironment>.FromResult(expected);
        var leftObserved = Pad(leftCore.Map<Unit>(_ => throw observer).MapErr<Unit>(_ => throw observer), padding);
        var rightObserved = Pad(rightCore.Map<Unit>(_ => throw observer).MapErr<Unit>(_ => throw observer), padding);
        var foldedObserved = Pad(folded.Map<Unit>(_ => throw observer).MapErr<Unit>(_ => throw observer), padding);

        // When
        var leftRun = left.RunExitAsync(CancellationToken.None);
        var rightRun = right.RunExitAsync(CancellationToken.None);
        var foldedRun = folded.RunExitAsync(CancellationToken.None);
        var leftObservedRun = leftObserved.RunExitAsync(CancellationToken.None);
        var rightObservedRun = rightObserved.RunExitAsync(CancellationToken.None);
        var foldedObservedRun = foldedObserved.RunExitAsync(CancellationToken.None);
        bool[] completed = [leftRun.IsCompletedSuccessfully, rightRun.IsCompletedSuccessfully, foldedRun.IsCompletedSuccessfully,
            leftObservedRun.IsCompletedSuccessfully, rightObservedRun.IsCompletedSuccessfully, foldedObservedRun.IsCompletedSuccessfully];
        var leftExit = await leftRun.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var rightExit = await rightRun.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var foldedExit = await foldedRun.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var leftObservedExit = await leftObservedRun.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var rightObservedExit = await rightObservedRun.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var foldedObservedExit = await foldedObservedRun.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.All(completed, Assert.True);
        Assert.Equal(((IUnion)expected).Value, foldedExit.Value);
        SameExit(foldedExit, leftExit);
        SameExit(foldedExit, rightExit);
        Assert.Same(observer, Assert.IsType<Die>(foldedObservedExit.Value).Exception);
        SameExit(foldedObservedExit, leftObservedExit);
        SameExit(foldedObservedExit, rightObservedExit);
    }

    [Theory]
    [InlineData("sourceOk", 0)]
    [InlineData("sourceOk", 128)]
    [InlineData("first", 0)]
    [InlineData("first", 128)]
    [InlineData("second", 0)]
    [InlineData("second", 128)]
    [InlineData("allErr", 0)]
    [InlineData("allErr", 128)]
    public async Task OrElse_ConstantRecoveries_AssociatesPublicExit(string recovery, int padding)
    {
        // Given
        var observer = new IOException("observer");
        Effect<int, string, EmptyEnvironment> source = recovery == "sourceOk"
            ? Effect<EmptyEnvironment>.Succeed(7) : Effect<EmptyEnvironment>.Fail("a");
        Effect<int, int, EmptyEnvironment> F(string error) => recovery == "first"
            ? Effect<EmptyEnvironment>.Succeed((error?.Length ?? 0) * 10) : Effect<EmptyEnvironment>.Fail(error?.Length ?? 0);
        Effect<int, bool, EmptyEnvironment> G(int error) => recovery == "allErr"
            ? Effect<EmptyEnvironment>.Fail(false) : Effect<EmptyEnvironment>.Succeed(error * 20);
        Result<int, bool> expected = recovery switch
        {
            "sourceOk" => Result.Ok(7),
            "first" => Result.Ok(10),
            "second" => Result.Ok(20),
            _ => Result.Err(false),
        };
        var leftCore = source.OrElse(F).OrElse(G);
        var rightCore = source.OrElse(error => F(error).OrElse(G));
        var left = Pad(leftCore, padding);
        var right = Pad(rightCore, padding);
        var folded = Effect<EmptyEnvironment>.FromResult(expected);
        var leftObserved = Pad(leftCore.Map<Unit>(_ => throw observer).MapErr<Unit>(_ => throw observer), padding);
        var rightObserved = Pad(rightCore.Map<Unit>(_ => throw observer).MapErr<Unit>(_ => throw observer), padding);
        var foldedObserved = Pad(folded.Map<Unit>(_ => throw observer).MapErr<Unit>(_ => throw observer), padding);

        // When
        var leftRun = left.RunExitAsync(CancellationToken.None);
        var rightRun = right.RunExitAsync(CancellationToken.None);
        var foldedRun = folded.RunExitAsync(CancellationToken.None);
        var leftObservedRun = leftObserved.RunExitAsync(CancellationToken.None);
        var rightObservedRun = rightObserved.RunExitAsync(CancellationToken.None);
        var foldedObservedRun = foldedObserved.RunExitAsync(CancellationToken.None);
        bool[] completed = [leftRun.IsCompletedSuccessfully, rightRun.IsCompletedSuccessfully, foldedRun.IsCompletedSuccessfully,
            leftObservedRun.IsCompletedSuccessfully, rightObservedRun.IsCompletedSuccessfully, foldedObservedRun.IsCompletedSuccessfully];
        var leftExit = await leftRun.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var rightExit = await rightRun.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var foldedExit = await foldedRun.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var leftObservedExit = await leftObservedRun.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var rightObservedExit = await rightObservedRun.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var foldedObservedExit = await foldedObservedRun.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.All(completed, Assert.True);
        Assert.Equal(((IUnion)expected).Value, foldedExit.Value);
        SameExit(foldedExit, leftExit);
        SameExit(foldedExit, rightExit);
        Assert.Same(observer, Assert.IsType<Die>(foldedObservedExit.Value).Exception);
        SameExit(foldedObservedExit, leftObservedExit);
        SameExit(foldedObservedExit, rightObservedExit);
    }

    private static Effect<T, E, R> Pad<T, E, R>(Effect<T, E, R> effect, int count)
    {
        for (var i = 0; i < count; i++) effect = effect.Map(static value => value);
        return effect;
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
