using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class CompositionModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AndThen_ContextConcatenation_AssociatesAndPreservesTheBoundedPrefix(bool pending)
    {
        // Given
        int[] sizes = [0, 1, 99, 100, 101];
        var combinations = from a in sizes from b in sizes from c in sizes select (a, b, c);
        foreach (var (a, b, c) in combinations)
        {
            var firstContext = Context("first", a);
            var secondContext = Context("second", b);
            var lastContext = Context("last", c);
            var defect = new IOException("last");
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstOutcome = Outcome<int, string>.Ok(1).WithSuppressed(firstContext, 2);
            var first = new Effect<int, string, EmptyEnvironment>(async (_, _) =>
            {
                if (pending) await completion.Task;
                return firstOutcome;
            });
            var second = new Effect<int, string, EmptyEnvironment>((_, _) => new(Outcome<int, string>.Ok(2).WithSuppressed(secondContext, 3)));
            var last = new Effect<int, string, EmptyEnvironment>((_, _) => new(Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(defect), lastContext, omitted: 5)));

            // When
            var leftRun = first.AndThen(_ => second).AndThen(_ => last).RunExitAsync(CancellationToken.None);
            var rightRun = first.AndThen(_ => second.AndThen(_ => last)).RunExitAsync(CancellationToken.None);
            Assert.Equal(!pending, leftRun.IsCompleted);
            Assert.Equal(!pending, rightRun.IsCompleted);
            completion.SetResult();
            var left = await leftRun.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var right = await rightRun.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Then
            var expected = firstContext.Concat(secondContext).Concat(lastContext).Take(100).ToArray();
            foreach (var exit in new[] { left, right })
            {
                var die = Assert.IsType<Die>(exit.Value);
                Assert.Same(defect, die.Exception);
                Assert.Equal(expected, die.Suppressed);
                Assert.Equal(10 + Math.Max(0, a + b + c - 100), die.OmittedSuppressed);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MapAndThenMapErr_ResultModel_PreservesValuesAndErrorsAcrossAsyncCompletion(bool pending)
    {
        // Given
        var inputs = from value in Enumerable.Range(-3, 7) from fail in new[] { false, true } select (value, fail);
        foreach (var (value, fail) in inputs)
        {
            Result<int, string> initial = fail ? Result.Err("initial") : Result.Ok(value);
            static int Map(int value) => value + 1;
            static Result<int, string> Bind(int value) => value % 2 == 0 ? Result.Ok(value * 2) : Result.Err("odd");
            static string MapError(string error) => $"mapped: {error}";
            var expected = initial.Map(Map).AndThen(Bind).MapErr(MapError);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = Effect<EmptyEnvironment>.FromResultAsync<int, string>(async _ =>
            {
                if (pending) await completion.Task;
                return initial;
            });

            // When
            var running = source.Map(Map).AndThen(value => Effect<EmptyEnvironment>.FromResult(Bind(value)))
                .MapErr(MapError).RunAsync(CancellationToken.None);
            Assert.Equal(!pending, running.IsCompleted);
            completion.SetResult();
            var actual = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Then
            Assert.Equal(expected, actual);
        }
    }

    private static ImmutableArray<Suppressed> Context(string name, int count)
        => [.. Enumerable.Range(0, count).Select(index => new Suppressed(new Failed($"{name} {index}"), Relation.Sequential))];
}
