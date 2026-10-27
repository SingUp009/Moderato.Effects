using System.Runtime.ExceptionServices;
using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects.Tests;

public class ResourceProjectionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SelectMany_PreparedResources_AllocatesLikeAndThen(int shape)
    {
        // Given
        var first = Resource.Make(Effect<EmptyEnvironment>.Succeed(1), static (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
        var second = Resource.Make(Effect<EmptyEnvironment>.Succeed(42), static (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
        Resource<int, string, EmptyEnvironment> typedFirst = first;
        Resource<int, string, EmptyEnvironment> typedSecond = second;
        Resource<int, string, EmptyEnvironment> query = shape switch
        {
            0 => from a in typedFirst from b in typedSecond select b,
            1 => from a in typedFirst from b in second select b,
            2 => from a in first from b in typedSecond select b,
            _ => from a in first from b in second select b,
        };
        var control = typedFirst.AndThen(_ => typedSecond);
        var effects = new[] { control, query }.Select(resource => resource.Use(static value => Effect<EmptyEnvironment>.Succeed(value))).ToArray();
        foreach (var effect in effects)
            for (var i = 0; i < 1024; i++) await effect.RunAsync(CancellationToken.None);
        long[] allocated = [long.MaxValue, long.MaxValue];
        var results = new int[2];
        var completedAtOnce = true;

        // When
        for (var scenario = 0; scenario < effects.Length; scenario++)
        {
            for (var window = 0; window < 3; window++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 4096; i++)
                {
                    var run = effects[scenario].RunAsync(CancellationToken.None);
                    completedAtOnce &= run.IsCompletedSuccessfully;
                    results[scenario] = (await run).UnwrapOr(-1);
                }
                allocated[scenario] = Math.Min(allocated[scenario], GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }

        // Then
        Assert.True(completedAtOnce);
        Assert.Equal([42, 42], results);
        Assert.True(allocated[1] <= allocated[0], $"AndThen: {allocated[0]}; SelectMany: {allocated[1]}");
    }
    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, false, true)]
    [InlineData(0, true, false)]
    [InlineData(0, true, true)]
    [InlineData(1, false, false)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    [InlineData(1, true, true)]
    [InlineData(2, false, false)]
    [InlineData(2, false, true)]
    [InlineData(2, true, false)]
    [InlineData(2, true, true)]
    [InlineData(3, false, false)]
    [InlineData(3, false, true)]
    [InlineData(3, true, false)]
    [InlineData(3, true, true)]
    public async Task SelectMany_ResultProjection_PreservesValuesAndReverseRelease(int shape, bool suspended, bool throws)
    {
        // Given
        var sequence = 0;
        var projections = 0;
        var uses = 0;
        var failure = new IOException("projection");
        var released = new List<(int Value, ExitCase Exit)>();
        var first = Resource.Make(Effect<EmptyEnvironment>.Sync(() => ++sequence), (value, exit) =>
            Effect<EmptyEnvironment>.Sync(() => { released.Add((value, exit)); return Unit.Value; }));
        Resource<int, string, EmptyEnvironment> typedFirst = first;
        Resource<int, Never, EmptyEnvironment> Next(int value)
        {
            var acquire = suspended ? Effect<EmptyEnvironment>.Async<int>(async ct =>
            {
                await Task.Delay(20, ct).ConfigureAwait(false);
                return value * 10;
            }) : Effect<EmptyEnvironment>.Succeed(value * 10);
            return Resource.Make(acquire, (original, exit) =>
                Effect<EmptyEnvironment>.Sync(() => { released.Add((original, exit)); return Unit.Value; }));
        }
        Resource<int, string, EmptyEnvironment> NextTyped(int value) => Next(value);
        int Project(int a, int b)
        {
            projections++;
            if (throws) throw failure;
            return a + b;
        }
        var exits = new Exit<int, string>[2];

        // When
        Resource<int, string, EmptyEnvironment> query = shape switch
        {
            0 => from a in typedFirst from b in NextTyped(a) select Project(a, b),
            1 => from a in typedFirst from b in Next(a) select Project(a, b),
            2 => from a in first from b in NextTyped(a) select Project(a, b),
            _ => from a in first from b in Next(a) select Project(a, b),
        };
        var work = query.Use(value => { uses++; return Effect<EmptyEnvironment>.Succeed(value); });
        for (var i = 0; i < exits.Length; i++)
            exits[i] = await work.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(2, sequence);
        Assert.Equal(2, projections);
        Assert.Equal(throws ? 0 : 2, uses);
        Assert.Equal(4, released.Count);
        for (var i = 0; i < exits.Length; i++)
        {
            Assert.Equal((i + 1) * 10, released[i * 2].Value);
            Assert.Equal(i + 1, released[i * 2 + 1].Value);
            Assert.Equal(released[i * 2].Exit, released[i * 2 + 1].Exit);
            if (throws)
            {
                Assert.True(exits[i].TryGetValue(out Die? die));
                Assert.Same(failure, die.Exception);
                Assert.Same(failure, Assert.IsType<Die>(released[i * 2].Exit.Value).Exception);
            }
            else
            {
                Assert.True(exits[i].TryGetValue(out Ok<int> ok));
                Assert.Equal((i + 1) * 11, ok.Value);
                Assert.True(released[i * 2].Exit.Value is Succeeded);
            }
        }
    }
    [Theory]
    [InlineData("first", 1)]
    [InlineData("first", 100)]
    [InlineData("selector", 1)]
    [InlineData("selector", 100)]
    [InlineData("acquire", 1)]
    [InlineData("acquire", 100)]
    [InlineData("project", 1)]
    [InlineData("project", 100)]
    [InlineData("use", 1)]
    [InlineData("use", 100)]
    public async Task SelectMany_StageFails_PreservesAcquisitionContextAndReleases(string stage, int count)
    {
        // Given
        var failure = new IOException(stage);
        var firstContext = Enumerable.Range(0, count).Select(i => new Suppressed(new Failed($"first {i}"), Relation.Sequential)).ToArray();
        var secondContext = Enumerable.Range(0, count).Select(i => new Suppressed(new Failed($"second {i}"), Relation.Parallel)).ToArray();
        var firstOutcome = (stage == "first" ? Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(failure))
            : Outcome<int, string>.Ok(2)).WithSuppressed([.. firstContext], 3);
        var secondOutcome = (stage == "acquire" ? Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(failure))
            : Outcome<int, string>.Ok(3)).WithSuppressed([.. secondContext], 5);
        var released = new List<int>();
        var first = Resource.Make(new Effect<int, string, EmptyEnvironment>((_, _) => new(firstOutcome)), (value, _) =>
            Effect<EmptyEnvironment>.Sync(() => { released.Add(value); return Unit.Value; }));
        var second = Resource.Make(new Effect<int, string, EmptyEnvironment>((_, _) => new(secondOutcome)), (value, _) =>
            Effect<EmptyEnvironment>.Sync(() => { released.Add(value); return Unit.Value; }));
        Resource<int, string, EmptyEnvironment> Select(int value) => stage == "selector" ? throw failure : second;
        int Project(int a, int b) => stage == "project" ? throw failure : a + b;
        Suppressed[] expected = stage is "first" or "selector" ? firstContext : [.. firstContext, .. secondContext];

        // When
        var query = from a in first from b in Select(a) select Project(a, b);
        var exit = await query.Use(value => Effect<EmptyEnvironment>.Sync<int>(() => throw failure))
            .RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(expected.Take(100), die.Suppressed);
        Assert.Equal((stage is "first" or "selector" ? 3 : 8) + Math.Max(0, expected.Length - 100), die.OmittedSuppressed);
        Assert.Equal(stage == "first" ? [] : stage is "selector" or "acquire" ? [2] : new[] { 3, 2 }, released);
    }
}
