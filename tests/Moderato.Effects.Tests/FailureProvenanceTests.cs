using Microsoft.Extensions.Time.Testing;
using Moderato.Results;

namespace Moderato.Effects.Tests;

public class FailureProvenanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task All_RecoveredFailureRunsAgain_UsesTheNewFailureOrigin(bool retry)
    {
        // Given
        Effect<int, string, DefaultEnvironment> sameFailure = Effect<DefaultEnvironment>.Fail("recovered then failed again");
        for (var run = 0; run < 3; run++)
        {
            var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var siblingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSibling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = 0;
            Effect<Unit, string, DefaultEnvironment> gate = Effect<DefaultEnvironment>.Async(async ct =>
            {
                if (++attempts == 2)
                {
                    _ = ct.Register(() => releaseSibling.TrySetResult());
                    secondStarted.SetResult();
                    await releaseSecond.Task;
                }
                return Unit.Value;
            });
            var attempt = gate.AndThen(_ => sameFailure);
            var recovered = retry ? attempt.Retry(Schedule.Immediately.UpTo(1)) : attempt.OrElse(_ => attempt);
            var earlier = Effect<DefaultEnvironment>.FromResultAsync<int, string>(async _ =>
            {
                await Task.WhenAll(secondStarted.Task, siblingStarted.Task);
                return Result.Err("earlier independent failure");
            });
            Effect<int, string, DefaultEnvironment> sibling = Effect<DefaultEnvironment>.Async(async ct =>
            {
                _ = ct.Register(() => releaseSecond.TrySetResult());
                siblingStarted.SetResult();
                await releaseSibling.Task;
                return 0;
            });
            var inner = Effect.All([earlier, sibling], Concurrency.Parallel).Map(static values => values.Length);

            // When
            var exit = await Effect.All([recovered, inner], Concurrency.Parallel)
                .RunExitAsync(DefaultEnvironment.Value, CancellationToken.None).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Then
            Assert.Equal(2, attempts);
            Assert.True(exit.TryGetValue(out Err<string> error));
            Assert.Equal("earlier independent failure", error.Error);
        }
    }
    [Theory]
    [InlineData("map", false)]
    [InlineData("map", true)]
    [InlineData("resourceMap", false)]
    [InlineData("resourceMap", true)]
    [InlineData("resource", false)]
    [InlineData("resource", true)]
    [InlineData("gen", false)]
    [InlineData("gen", true)]
    public async Task All_FailureWaitsInComposition_PreservesItsOriginAndClassification(string boundary, bool laterDefect)
    {
        // Given
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var defect = new IOException("later defect");
        Effect<int, string, EmptyEnvironment> early = Effect<EmptyEnvironment>.Fail("early");
        var release = Effect<EmptyEnvironment>.Async(async _ =>
        {
            held.SetResult();
            await finish.Task;
            return Unit.Value;
        });
        string Map(string error)
        {
            held.SetResult();
            finish.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            return error;
        }
        Effect<Unit, string, EmptyEnvironment> acquire = Effect<EmptyEnvironment>.Succeed(Unit.Value);
        var first = boundary switch
        {
            "map" => early.MapErr(Map),
            "resourceMap" => Resource.Make(early, static (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value))
                .MapErr(Map).Use(static value => Effect<EmptyEnvironment>.Succeed(value)),
            "resource" => Resource.Make(acquire, (_, _) => release).Use(_ => early),
            _ => Scope<string, EmptyEnvironment>.Gen<int>(async scope =>
            {
                scope.Defer(_ => release);
                return await scope.Run(early);
            }),
        };
        var later = Effect<EmptyEnvironment>.FromResultAsync<int, string>(async ct =>
        {
            _ = ct.Register(() => finish.TrySetResult());
            await held.Task;
            if (laterDefect) throw defect;
            return Result.Err("later");
        });

        // When
        var exit = await Effect.All([first, later], Concurrency.Parallel).RunExitAsync(CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        if (laterDefect)
        {
            Assert.True(exit.TryGetValue(out Die? die));
            Assert.Same(defect, die.Exception);
            var suppressed = Assert.Single(die.Suppressed);
            Assert.Equal("early", Assert.IsType<Failed>(suppressed.Case.Value).Error);
            Assert.Equal(Relation.Parallel, suppressed.Relation);
        }
        else
        {
            Assert.True(exit.TryGetValue(out Err<string> error));
            Assert.Equal("early", error.Error);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fork_EarlierDefectFinishesDuringCleanup_KeepsTheEarlierDefect(bool duringRelease)
    {
        // Given
        var early = new IOException("early");
        var later = new IOException("later");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = Effect<EmptyEnvironment>.Async(async _ =>
        {
            held.SetResult();
            await finish.Task;
            return Unit.Value;
        });
        var first = Resource.Make(Effect<EmptyEnvironment>.Succeed(Unit.Value), (_, _) => release)
            .Use(_ => Effect<EmptyEnvironment>.Sync<int>(() => throw early));
        var second = Effect<EmptyEnvironment>.Async<int>(async _ =>
        {
            await held.Task;
            throw later;
        });
        var work = Scope<Never, EmptyEnvironment>.Gen<int>(async scope =>
        {
            if (!duringRelease) _ = scope.CancellationToken.Register(() => finish.TrySetResult());
            var a = await scope.Fork(first);
            var b = await scope.Fork(second);
            if (duringRelease)
            {
                scope.Defer(_ => Effect<EmptyEnvironment>.Sync(() => { finish.TrySetResult(); return Unit.Value; }));
                await Task.Delay(Timeout.InfiniteTimeSpan, scope.CancellationToken);
            }
            else
            {
                await a.Join().RunExitAsync(CancellationToken.None);
                await b.Join().RunExitAsync(CancellationToken.None);
            }
            return 0;
        });

        // When
        var exit = await work.RunExitAsync(CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(early, die.Exception);
        var other = Assert.Single(die.Suppressed);
        Assert.Same(later, Assert.IsType<Die>(other.Case.Value).Exception);
        Assert.NotEqual(Relation.Consequence, other.Relation);
    }

    [Fact]
    public async Task Race_TimeoutWaitsForCleanup_KeepsTheTimeoutOccurrence()
    {
        // Given
        var clock = new FakeTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delay = TimeSpan.FromSeconds(1);
        var first = Effect<TimeEnvironment>.Async(async ct =>
        {
            _ = ct.Register(() => expired.TrySetResult());
            started.SetResult();
            await finish.Task;
            return 1;
        }).Timeout(delay).TapErr(_ => observed.SetResult());
        var failure = Effect<TimeEnvironment>.FromResultAsync<int, TimeoutError>(async _ =>
        {
            await expired.Task;
            return Result.Err(new TimeoutError(TimeSpan.FromSeconds(2)));
        });
        Effect<Unit, TimeoutError, TimeEnvironment> acquire = Effect<TimeEnvironment>.Succeed(Unit.Value);
        var second = Resource.Make(acquire, (_, _) => Effect<TimeEnvironment>.Async(async _ =>
        {
            finish.SetResult();
            await observed.Task;
            return Unit.Value;
        })).Use(_ => failure);

        // When
        var running = Effect.Race(first, second).RunExitAsync(new TimeEnvironment(clock), CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        clock.Advance(delay);
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Err<TimeoutError> error));
        Assert.Equal(new TimeoutError(delay), error.Error);
    }

    [Fact]
    public async Task Fork_BodyFailedBeforeChildButStillReleasing_KeepsBodyAndChildFailures()
    {
        // Given
        var early = new IOException("body");
        var later = new IOException("child");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = Resource.Make(Effect<EmptyEnvironment>.Succeed(Unit.Value), (_, _) => Effect<EmptyEnvironment>.Async(async _ =>
        {
            held.SetResult();
            await finish.Task;
            return Unit.Value;
        })).Use(_ => Effect<EmptyEnvironment>.Sync<int>(() => throw early));
        var child = Effect<EmptyEnvironment>.Async<int>(async _ =>
        {
            await held.Task;
            throw later;
        });
        var work = Scope<Never, EmptyEnvironment>.Gen<int>(async scope =>
        {
            _ = scope.CancellationToken.Register(() => finish.TrySetResult());
            await scope.Fork(child);
            return await scope.Run(body);
        });

        // When
        var exit = await work.RunExitAsync(CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(early, die.Exception);
        var other = Assert.Single(die.Suppressed);
        Assert.Same(later, Assert.IsType<Die>(other.Case.Value).Exception);
        Assert.Equal(Relation.Parallel, other.Relation);
    }

    [Fact]
    public async Task Gen_CaughtFailureExposesMutableValueError_KeepsTheOriginalError()
    {
        // Given
        var work = Scope<MutableError, EmptyEnvironment>.Gen<int>(scope =>
        {
            try
            {
                throw scope.Fail(new MutableError(1));
            }
            catch (ScopeFailure failure)
            {
                _ = ((System.Runtime.CompilerServices.IUnion)failure.Error!).Value;
            }
            return new(42);
        });

        // When
        var exit = await work.RunExitAsync(CancellationToken.None);

        // Then
        Assert.True(exit.TryGetValue(out Err<MutableError> error));
        Assert.Equal(1, error.Error.Count);
    }

    private struct MutableError(int count) : System.Runtime.CompilerServices.IUnion
    {
        internal int Count = count;

        object System.Runtime.CompilerServices.IUnion.Value => ++Count;
    }

}
