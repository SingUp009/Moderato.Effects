using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class InterruptedReleaseTests
{
    public static TheoryData<bool, string, string, bool> Cases
    {
        get
        {
            var cases = new TheoryData<bool, string, string, bool>();
            foreach (var resource in new[] { false, true })
            {
                foreach (var body in new[] { "Ok", "Err", "Die", "Interrupted" })
                {
                    foreach (var shape in new[] { "Direct", "Gen", "AllSequential", "AllParallel", "Resource" })
                    {
                        foreach (var pending in new[] { false, true })
                            cases.Add(resource, body, shape, pending);
                    }
                }
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RunExitAsync_ReleaseReturnsInterrupted_ReportsDefectAndFinishesOtherReleases(
        bool resource, string bodyKind, string shape, bool pending)
    {
        // Given
        var dead = await InterruptedFiber();
        var bodyFailure = new IOException("body");
        var releases = new List<(string Name, ExitCase Exit)>();
        var finished = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joined = dead.Join().Tap(_ => finished = true);
        var interrupted = shape switch
        {
            "Gen" => Scope<Never, EmptyEnvironment>.Gen(async scope => await scope.Run(joined)),
            "AllSequential" => Effect.All([joined], Concurrency.Sequential).Map(static _ => Unit.Value),
            "AllParallel" => Effect.All([joined], Concurrency.Parallel).Map(static _ => Unit.Value),
            "Resource" => Resource.Make(Effect<EmptyEnvironment>.Succeed(Unit.Value),
                static (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value)).Use(_ => joined),
            _ => joined
        };
        Effect<int, string, EmptyEnvironment> body = bodyKind switch
        {
            "Err" => Effect<EmptyEnvironment>.Fail("body"),
            "Die" => Effect<EmptyEnvironment>.Sync<int>(() => throw bodyFailure),
            "Interrupted" => dead.Join().Map(static _ => 42),
            _ => Effect<EmptyEnvironment>.Succeed(42)
        };

        Effect<Unit, Never, EmptyEnvironment> Release(string name, ExitCase exit)
        {
            releases.Add((name, exit));
            if (name != "middle") return Effect<EmptyEnvironment>.Succeed(Unit.Value);
            var step = interrupted;
            return Effect<EmptyEnvironment>.Async<Unit>(async token =>
            {
                Assert.False(token.CanBeCanceled);
                entered.SetResult();
                if (pending) await resume.Task;
                return Unit.Value;
            }).AndThen(_ => step);
        }

        Effect<int, string, EmptyEnvironment> work;
        if (resource)
        {
            Resource<string, string, EmptyEnvironment> Make(string name)
                => Resource.Make<string, string, EmptyEnvironment>(Effect<EmptyEnvironment>.Succeed(name), Release);
            work = Make("first").AndThen(_ => Make("middle")).AndThen(_ => Make("last")).Use(_ => body);
        }
        else
        {
            work = Scope<string, EmptyEnvironment>.Gen(async scope =>
            {
                scope.Defer(exit => Release("first", exit));
                scope.Defer(exit => Release("middle", exit));
                scope.Defer(exit => Release("last", exit));
                return await scope.Run(body);
            });
        }

        // When
        var running = work.RunExitAsync(CancellationToken.None);
        await entered.Task;
        if (pending) Assert.False(running.IsCompleted);
        resume.SetResult();
        var exit = await running;

        // Then
        Assert.False(finished);
        Assert.Equal(["last", "middle", "first"], releases.Select(release => release.Name));
        Assert.All(releases, release => Assert.Equal(releases[0].Exit, release.Exit));
        var die = Assert.IsType<Die>(exit.Value);
        if (bodyKind == "Die")
        {
            Assert.Same(bodyFailure, die.Exception);
            var secondary = Assert.Single(die.Suppressed);
            Assert.Equal(Relation.Sequential, secondary.Relation);
            Assert.IsType<InvalidOperationException>(Assert.IsType<Die>(secondary.Case.Value).Exception);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(die.Exception);
            if (bodyKind == "Ok")
            {
                Assert.Empty(die.Suppressed);
            }
            else
            {
                var secondary = Assert.Single(die.Suppressed);
                Assert.Equal(Relation.Sequential, secondary.Relation);
                if (bodyKind == "Err") Assert.Equal("body", Assert.IsType<Failed>(secondary.Case.Value).Error);
                else Assert.IsType<Interrupted>(secondary.Case.Value);
            }
        }
        Assert.All(releases, release =>
        {
            switch (bodyKind)
            {
                case "Ok": Assert.IsType<Succeeded>(release.Exit.Value); break;
                case "Err": Assert.Equal("body", Assert.IsType<Failed>(release.Exit.Value).Error); break;
                case "Die": Assert.Same(bodyFailure, Assert.IsType<Die>(release.Exit.Value).Exception); break;
                default: Assert.IsType<Interrupted>(release.Exit.Value); break;
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunExitAsync_ManyInterruptedReleases_PreservesFailureBoundAndFinishesEveryRelease(bool bodyFails)
    {
        // Given
        var dead = await InterruptedFiber();
        var bodyFailure = new IOException("body");
        var released = new List<int>();
        var work = Scope<Never, EmptyEnvironment>.Gen(_ =>
        {
            for (var i = 0; i < 102; i++)
            {
                var index = i;
#pragma warning disable MEFF305
                _.Defer(_ =>
                {
                    released.Add(index);
                    return dead.Join();
                });
#pragma warning restore MEFF305
            }
            if (bodyFails) throw bodyFailure;
            return new ValueTask<int>(42);
        });

        // When
        var exit = await work.RunExitAsync(CancellationToken.None);

        // Then
        Assert.Equal(Enumerable.Range(0, 102).Reverse(), released);
        var die = Assert.IsType<Die>(exit.Value);
        if (bodyFails) Assert.Same(bodyFailure, die.Exception);
        else Assert.IsType<InvalidOperationException>(die.Exception);
        Assert.Equal(100, die.Suppressed.Count);
        Assert.Equal(bodyFails ? 2 : 1, die.OmittedSuppressed);
        Assert.All(die.Suppressed, failure =>
        {
            Assert.Equal(Relation.Sequential, failure.Relation);
            Assert.IsType<InvalidOperationException>(Assert.IsType<Die>(failure.Case.Value).Exception);
        });
    }

    [Fact]
    public async Task Use_InterruptedReleaseCarriesContext_PreservesContextAndOmittedCount()
    {
        // Given
        var context = new Suppressed(new Failed("earlier"), Relation.Consequence);
        var interrupted = Outcome<Unit, Never>.Interrupted().WithSuppressed([context], 7);
        var release = new Effect<Unit, Never, EmptyEnvironment>((_, _) => new(interrupted));
        var resource = Resource.Make(Effect<EmptyEnvironment>.Succeed(42), (_, _) => release);

        // When
        var exit = await resource.Use(Effect<EmptyEnvironment>.Succeed).RunExitAsync(CancellationToken.None);

        // Then
        var die = Assert.IsType<Die>(exit.Value);
        Assert.IsType<InvalidOperationException>(die.Exception);
        Assert.Same(context, Assert.Single(die.Suppressed));
        Assert.Equal(7, die.OmittedSuppressed);
    }

    [Fact]
    public async Task RunAsync_ReleaseReturnsInterrupted_ThrowsInsteadOfReportingSuccess()
    {
        // Given
        var dead = await InterruptedFiber();
        var work = Resource.Make(Effect<EmptyEnvironment>.Succeed(42), (_, _) => dead.Join())
            .Use(Effect<EmptyEnvironment>.Succeed);

        // When
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await work.RunAsync(CancellationToken.None));

        // Then
        Assert.Equal("A resource release was interrupted before completing.", exception.Message);
    }

    private static async Task<Fiber<Unit, Never, EmptyEnvironment>> InterruptedFiber()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forever = Effect<EmptyEnvironment>.Async<Unit>(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Unit.Value;
        });
        var result = await forever.Fork().Use(fiber => Effect<EmptyEnvironment>.Async<Fiber<Unit, Never, EmptyEnvironment>>(async _ =>
        {
            await started.Task;
            return fiber;
        })).RunAsync(CancellationToken.None);
        return Assert.IsType<Ok<Fiber<Unit, Never, EmptyEnvironment>>>(result.Value).Value;
    }
}
