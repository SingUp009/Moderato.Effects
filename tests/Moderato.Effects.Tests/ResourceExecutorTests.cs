using System.Runtime.InteropServices;
using Microsoft.Extensions.Time.Testing;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class ResourceExecutorTests
{
    [Theory]
    [InlineData("Err")]
    [InlineData("Die")]
    [InlineData("Interrupted")]
    public async Task AndThen_DeepAcquisitionFails_ReleasesOnlyAcquiredValuesInReverseOrder(string failure)
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        var exception = new IOException("acquire");
        var released = new List<(int Value, ExitCase Exit, bool Cancellable)>();
        var uses = 0;
        Resource<int, string, EmptyEnvironment> Make(int index)
        {
            Effect<int, string, EmptyEnvironment> acquire = index == 256 ? failure switch
            {
                "Err" => Effect<EmptyEnvironment>.Fail("acquire"),
                "Die" => Effect<EmptyEnvironment>.Sync<int>(() => throw exception),
                _ => Effect<EmptyEnvironment>.Async<int>(token =>
                {
                    cancellation.Cancel();
                    return ValueTask.FromCanceled<int>(token);
                })
            } : Effect<EmptyEnvironment>.Succeed(index);
            return Resource.Make(acquire, (value, exit) => Effect<EmptyEnvironment>.Async(token =>
            {
                released.Add((value, exit, token.CanBeCanceled));
                return new ValueTask<Unit>(Unit.Value);
            }));
        }
        var resource = Make(0);
        for (var i = 1; i < 512; i++)
        {
            var index = i;
            resource = resource.AndThen(_ => Make(index));
        }

        // When
        var exit = await resource.Use(value =>
        {
            uses++;
            return Effect<EmptyEnvironment>.Succeed(value);
        }).RunExitAsync(cancellation.Token);

        // Then
        Assert.Equal(0, uses);
        Assert.Equal(Enumerable.Range(0, 256).Reverse(), released.Select(release => release.Value));
        Assert.All(released, release => Assert.False(release.Cancellable));
        if (failure == "Err")
        {
            Assert.Equal("acquire", Assert.IsType<Err<string>>(exit.Value).Error);
            Assert.All(released, release => Assert.Equal("acquire", Assert.IsType<Failed>(release.Exit.Value).Error));
        }
        else if (failure == "Die")
        {
            Assert.Same(exception, Assert.IsType<Die>(exit.Value).Exception);
            Assert.All(released, release => Assert.Same(exception, Assert.IsType<Die>(release.Exit.Value).Exception));
        }
        else
        {
            Assert.IsType<Interrupted>(exit.Value);
            Assert.All(released, release => Assert.IsType<Interrupted>(release.Exit.Value));
        }
    }

    [Fact]
    public async Task Use_DeepAcquisitionAndTwoFailingReleases_ReleasesAllValuesWithTheOriginalExit()
    {
        // Given
        var first = new IOException("first release");
        var last = new IOException("last release");
        var released = new List<(int Value, ExitCase Exit)>();
        Resource<int, string, EmptyEnvironment> Make(int value)
            => Resource.Make<int, string, EmptyEnvironment>(Effect<EmptyEnvironment>.Succeed(value), (original, exit) =>
                Effect<EmptyEnvironment>.Sync(() =>
                {
                    released.Add((original, exit));
                    if (original == 511) throw first;
                    if (original == 0) throw last;
                    return Unit.Value;
                }));
        var resource = Make(0);
        for (var i = 1; i < 512; i++)
        {
            var value = i;
            resource = resource.AndThen(_ => Make(value));
        }

        // When
        var exit = await resource.Use<int>(_ => Effect<EmptyEnvironment>.Fail("body")).RunExitAsync(CancellationToken.None);

        // Then
        Assert.Equal(Enumerable.Range(0, 512).Reverse(), released.Select(release => release.Value));
        Assert.All(released, release => Assert.Equal("body", Assert.IsType<Failed>(release.Exit.Value).Error));
        var die = Assert.IsType<Die>(exit.Value);
        Assert.Same(first, die.Exception);
        Assert.Equal("body", Assert.IsType<Failed>(die.Suppressed[0].Case.Value).Error);
        Assert.Same(last, Assert.IsType<Die>(die.Suppressed[1].Case.Value).Exception);
        Assert.Equal(2, die.Suppressed.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectMany_DeepHeterogeneousQuery_PreservesValuesAndOriginalReleaseArguments(bool pending)
    {
        // Given
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new List<int>();
        var source = Effect<EmptyEnvironment>.Async<int>(async _ =>
        {
            if (pending) await gate.Task.ConfigureAwait(false);
            return 0;
        });
        var resource = Resource.Make(source, (value, _) => Effect<EmptyEnvironment>.Sync(() =>
        {
            released.Add(value);
            return Unit.Value;
        }));
        for (var i = 1; i <= 512; i++)
        {
            resource = from first in resource
                       from second in Resource.Make(Effect<EmptyEnvironment>.Succeed(new Payload(first + 1)),
                           (original, _) => Effect<EmptyEnvironment>.Sync(() =>
                           {
                               released.Add(original.Value);
                               return Unit.Value;
                           }))
                       select second.Value;
        }

        // When
        var running = resource.Use(static value => Effect<EmptyEnvironment>.Succeed(value)).RunAsync(CancellationToken.None);
        var completed = running.IsCompletedSuccessfully;
        gate.SetResult();
        var result = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(!pending, completed);
        Assert.Equal<Result<int, Never>>(Result.Ok(512), result);
        Assert.Equal(Enumerable.Range(0, 513).Reverse(), released);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AndThen_DeepDefaultChild_DiesAndReleasesThePrefix(bool pending)
    {
        // Given
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new List<int>();
        var source = Effect<EmptyEnvironment>.Async<int>(async _ =>
        {
            if (pending) await gate.Task.ConfigureAwait(false);
            return 0;
        });
        var resource = Resource.Make(source, (value, _) => Effect<EmptyEnvironment>.Sync(() =>
        {
            released.Add(value);
            return Unit.Value;
        }));
        for (var i = 1; i <= 512; i++) resource = resource.Map(static value => value + 1);
        resource = resource.AndThen<int>(static _ => default);

        // When
        var running = resource.Use(static value => Effect<EmptyEnvironment>.Succeed(value)).RunExitAsync(CancellationToken.None);
        gate.SetResult();
        var exit = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.IsType<InvalidOperationException>(Assert.IsType<Die>(exit.Value).Exception);
        Assert.Equal([0], released);
    }

    [Theory]
    [InlineData("Gen")]
    [InlineData("Timeout")]
    [InlineData("All")]
    [InlineData("Race")]
    [InlineData("Fork")]
    [InlineData("Nested")]
    public async Task Use_DeepResourcesAcrossExecutionBoundaries_ReleasesEachRunsValuesExactlyOnce(string boundary)
    {
        // Given
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new FakeTimeProvider();
        var environment = new TimeEnvironment(time);
        var acquired = new List<int>();
        var released = new List<int>();
        var source = Effect<TimeEnvironment>.Async<int>(async _ =>
        {
            await gate.Task.ConfigureAwait(false);
            lock (acquired) acquired.Add(1);
            return 1;
        });
        var resource = Resource.Make(source, (value, _) => Effect<TimeEnvironment>.Sync(() =>
        {
            lock (released) released.Add(value);
            return Unit.Value;
        }));
        for (var i = 0; i < 512; i++)
        {
            resource = resource.AndThen(value => Resource.Make(Effect<TimeEnvironment>.Sync(() =>
                {
                    lock (acquired) acquired.Add(value + 1);
                    return value + 1;
                }), (original, _) =>
                Effect<TimeEnvironment>.Sync(() =>
                {
                    lock (released) released.Add(original);
                    return Unit.Value;
                })));
        }
        var body = resource.Use(static value => Effect<TimeEnvironment>.Succeed(value));
        Effect<int, Never, TimeEnvironment> work = boundary switch
        {
            "Gen" => Scope<Never, TimeEnvironment>.Gen(async scope => await scope.Acquire(resource)),
            "All" => Effect.All([body, body], Concurrency.Parallel).Map(static values => values.Sum()),
            "Race" => Effect.Race(body, body),
            "Fork" => body.Fork().Use(static fiber => fiber.Join()),
            "Nested" => Scope<Never, TimeEnvironment>.Gen(async scope =>
            {
                var fiber = await scope.Fork(body);
                return await scope.Run(fiber.Join());
            }),
            _ => body
        };

        // When
        var running = boundary is "Timeout" or "Nested"
            ? work.Timeout(TimeSpan.FromSeconds(1)).MapErr(static _ => "timeout").RunAsync(environment, CancellationToken.None)
            : work.MapErr<string>(static _ => throw new InvalidOperationException("Never has no error.")).RunAsync(environment, CancellationToken.None);
        var suspended = !running.IsCompleted;
        gate.SetResult();
        var result = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(suspended);
        Assert.Equal<Result<int, string>>(Result.Ok(boundary == "All" ? 1026 : 513), result);
        Assert.Equal(acquired.Order(), released.Order());
        if (boundary == "Race") Assert.InRange(released.Count, 513, 1026);
        else Assert.Equal(513 * (boundary == "All" ? 2 : 1), released.Count);
    }

    [Fact]
    public async Task Timeout_DeepAcquisitionIsCanceled_ReleasesThePrefixBeforeReturningTimeout()
    {
        // Given
        var time = new FakeTimeProvider();
        var environment = new TimeEnvironment(time);
        var released = new List<(int Value, bool Cancellable)>();
        var uses = 0;
        Resource<int, Never, TimeEnvironment> Make(int index)
            => Resource.Make(Effect<TimeEnvironment>.Async<int>(async token =>
            {
                if (index == 256) await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return index;
            }), (value, _) => Effect<TimeEnvironment>.Async(token =>
            {
                released.Add((value, token.CanBeCanceled));
                return new ValueTask<Unit>(Unit.Value);
            }));
        var resource = Make(0);
        for (var i = 1; i < 512; i++)
        {
            var index = i;
            resource = resource.AndThen(_ => Make(index));
        }
        var effect = resource.Use(value =>
        {
            uses++;
            return Effect<TimeEnvironment>.Succeed(value);
        }).Timeout(TimeSpan.FromSeconds(1));

        // When
        var running = effect.RunAsync(environment, CancellationToken.None);
        var suspended = !running.IsCompleted;
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(suspended);
        Assert.Equal<Result<int, TimeoutError>>(Result.Err(new TimeoutError(TimeSpan.FromSeconds(1))), result);
        Assert.Equal(0, uses);
        Assert.Equal(Enumerable.Range(0, 256).Reverse(), released.Select(release => release.Value));
        Assert.All(released, release => Assert.False(release.Cancellable));
    }

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private readonly record struct Payload(int Value);

    [Theory]
    [InlineData("throw", 0)]
    [InlineData("throw", 128)]
    [InlineData("faulted", 0)]
    [InlineData("faulted", 128)]
    [InlineData("pending", 0)]
    [InlineData("pending", 128)]
    public async Task AndThen_ChildAcquisitionThrows_PreservesEarlierContextAcrossCompletionPaths(string completion, int padding)
    {
        // Given
        var exception = new IOException("child");
        var context = new Suppressed(new Failed("earlier"), Relation.Sequential);
        var firstOutcome = Outcome<int, string>.Ok(1).WithSuppressed([context], 7);
        var source = new Effect<int, string, EmptyEnvironment>((_, _) => new(firstOutcome));
        var released = 0;
        var first = Resource.Make(source, (_, _) => Effect<EmptyEnvironment>.Sync(() =>
        {
            released++;
            return Unit.Value;
        }));
        var gate = new TaskCompletionSource<Outcome<int, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new Resource<int, string, EmptyEnvironment>((_, _, _) => completion switch
        {
            "throw" => throw exception,
            "faulted" => ValueTask.FromException<Outcome<int, string>>(exception),
            _ => new(gate.Task)
        });
        var resource = first.AndThen(_ => child);
        for (var i = 0; i < padding; i++) resource = resource.Map(static value => value);

        // When
        var running = resource.Use(static value => Effect<EmptyEnvironment>.Succeed(value)).RunExitAsync(CancellationToken.None);
        var completed = running.IsCompletedSuccessfully;
        if (completion == "pending") gate.SetException(exception);
        var exit = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(completion != "pending", completed);
        var die = Assert.IsType<Die>(exit.Value);
        Assert.Same(exception, die.Exception);
        Assert.Equal([context], die.Suppressed);
        Assert.Equal(7, die.OmittedSuppressed);
        Assert.Equal(1, released);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task AndThen_SuspendedAcquisitions_PreservesDirectAndIterativeAmbientContexts(bool pendingFirst, bool pendingSecond, bool suppressFlow)
    {
        // Given
        var scenarios = new[] { 0, 128 }.Select(padding =>
        {
            var local = new AsyncLocal<string?> { Value = "caller" };
            var seen = new List<string>();
            var first = new PendingValue<int>();
            var second = new PendingValue<int>();
            var acquire = pendingFirst ? Effect<EmptyEnvironment>.Async<int>(_ => first.Task)
                : Effect<EmptyEnvironment>.Sync(() =>
                {
                    local.Value = "first";
                    return 1;
                });
            var resource = Resource.Make(acquire, (_, _) => Effect<EmptyEnvironment>.Sync(() =>
                {
                    seen.Add($"release-first:{local.Value}");
                    return Unit.Value;
                }))
                .AndThen(_ =>
                {
                    seen.Add($"binder:{local.Value}");
                    local.Value = "binder";
                    var next = pendingSecond ? Effect<EmptyEnvironment>.Async<int>(_ => second.Task)
                        : Effect<EmptyEnvironment>.Sync(() =>
                        {
                            local.Value = "second";
                            return 2;
                        });
                    return Resource.Make(next, (_, _) => Effect<EmptyEnvironment>.Sync(() =>
                    {
                        seen.Add($"release-second:{local.Value}");
                        return Unit.Value;
                    }));
                });
            for (var i = 0; i < padding; i++) resource = resource.Map(static value => value);
            var effect = resource.Map(value =>
                {
                    seen.Add($"map:{local.Value}");
                    return value;
                })
                .Use(value => Effect<EmptyEnvironment>.Sync(() =>
                {
                    seen.Add($"use:{local.Value}");
                    local.Value = "used";
                    return value;
                }));
            return (effect, first, second, local, seen);
        }).ToArray();
        var results = new List<Result<int, Never>>();
        var callerValues = new List<string?>();
        var completed = new List<bool>();

        // When
        foreach (var scenario in scenarios)
        {
            ValueTask<Result<int, Never>> running;
            if (suppressFlow)
            {
                using (ExecutionContext.SuppressFlow()) running = scenario.effect.RunAsync(CancellationToken.None);
            }
            else
            {
                running = scenario.effect.RunAsync(CancellationToken.None);
            }
            callerValues.Add(scenario.local.Value);
            completed.Add(running.IsCompletedSuccessfully);
            if (pendingFirst)
            {
                await scenario.first.Registered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                scenario.first.Complete(1);
            }
            if (pendingSecond)
            {
                await scenario.second.Registered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                scenario.second.Complete(2);
            }
            results.Add(await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }

        // Then
        Assert.Equal(["caller", "caller"], callerValues);
        Assert.All(completed, value => Assert.Equal(!pendingFirst && !pendingSecond, value));
        Assert.All(results, result => Assert.Equal<Result<int, Never>>(Result.Ok(2), result));
        Assert.Equal(scenarios[0].seen, scenarios[1].seen);
    }
}
