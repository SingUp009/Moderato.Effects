namespace Moderato.Effects.Tests;

public class ZeroTimeoutTests
{
    public static TheoryData<string, string, bool> Cases
    {
        get
        {
            var cases = new TheoryData<string, string, bool>();
            foreach (var shape in new[] { "Constant", "Sync", "Async", "Gen", "Resource" })
            {
                foreach (var overload in new[] { "Never", "TimeoutError", "Union", "Mapped", "Result" })
                {
                    foreach (var canceled in new[] { false, true }) cases.Add(shape, overload, canceled);
                }
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Timeout_ZeroDuration_DoesNotStartWorkOrTimers(string shape, string overload, bool canceled)
    {
        // Given
        var bodyRuns = 0;
        var releases = 0;
        var clock = new NoTimerClock();
        var environment = new TimeEnvironment(clock);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (canceled) cancellation.Cancel();
        Effect<int, Never, TimeEnvironment> effect = shape switch
        {
            "Sync" => Effect<TimeEnvironment>.Sync(() => ++bodyRuns),
            "Async" => Effect<TimeEnvironment>.Async(async _ =>
            {
                bodyRuns++;
                await Task.Yield();
                return 42;
            }),
            "Gen" => Scope<Never, TimeEnvironment>.Gen(async scope =>
            {
                bodyRuns++;
                scope.Defer(_ => Effect<TimeEnvironment>.Sync(() => { releases++; return Unit.Value; }));
                await Task.Yield();
                return 42;
            }),
            "Resource" => Resource.Make(Effect<TimeEnvironment>.Sync(() => ++bodyRuns),
                (_, _) => Effect<TimeEnvironment>.Sync(() => { releases++; return Unit.Value; }))
                .Use(static value => Effect<TimeEnvironment>.Succeed(value)),
            _ => Effect<TimeEnvironment>.Succeed(42),
        };
        Effect<int, TimeoutError, TimeEnvironment> existingTimeout = effect;
        Effect<int, RequestError, TimeEnvironment> union = effect;
        Effect<int, string, TimeEnvironment> mapped = effect;

        // When
        var outcome = overload switch
        {
            "TimeoutError" => (await existingTimeout.Timeout(TimeSpan.Zero).RunExitAsync(environment, cancellation.Token)).Value,
            "Union" => (await union.Timeout(TimeSpan.Zero).RunExitAsync(environment, cancellation.Token)).Value,
            "Mapped" => (await mapped.Timeout(TimeSpan.Zero, static _ => "expired").RunExitAsync(environment, cancellation.Token)).Value,
            "Result" => (await effect.TimeoutResult(TimeSpan.Zero).RunExitAsync(environment, cancellation.Token)).Value,
            _ => (await effect.Timeout(TimeSpan.Zero).RunExitAsync(environment, cancellation.Token)).Value,
        };

        // Then
        if (canceled)
        {
            Assert.IsType<Interrupted>(outcome);
        }
        else if (overload == "Mapped")
        {
            Assert.Equal("expired", Assert.IsType<Err<string>>(outcome).Error);
        }
        else
        {
            var error = overload switch
            {
                "Union" => Assert.IsType<TimeoutError>(Assert.IsType<Err<RequestError>>(outcome).Error.Value),
                "Result" => Assert.IsType<Err<TimeoutError>>(Assert.IsType<Ok<Result<int, TimeoutError>>>(outcome).Value.Value).Error,
                _ => Assert.IsType<Err<TimeoutError>>(outcome).Error,
            };
            Assert.Equal(TimeSpan.Zero, error.After);
        }
        Assert.Equal(0, bodyRuns);
        Assert.Equal(0, releases);
        Assert.Equal(0, clock.Calls);
    }

    [Fact]
    public async Task Timeout_ZeroDuration_DoesNotReadClockService()
    {
        // Given
        var effect = Effect<UnreadableClockEnvironment>.Succeed(42);

        // When
        var exit = await effect.Timeout(TimeSpan.Zero).RunExitAsync(new UnreadableClockEnvironment(), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(TimeSpan.Zero, Assert.IsType<Err<TimeoutError>>(exit.Value).Error.After);
    }

    [Fact]
    public async Task Timeout_ZeroDurationMappingThrows_ReturnsTheDefect()
    {
        // Given
        var defect = new InvalidOperationException("mapper");
        Effect<int, string, TimeEnvironment> effect = Effect<TimeEnvironment>.Succeed(42);

        // When
        var exit = await effect.Timeout(TimeSpan.Zero, _ => throw defect)
            .RunExitAsync(new TimeEnvironment(new NoTimerClock()), TestContext.Current.CancellationToken);

        // Then
        Assert.Same(defect, Assert.IsType<Die>(exit.Value).Exception);
    }

    [Fact]
    public async Task Timeout_ZeroDurationRerun_MapsOncePerRun()
    {
        // Given
        var calls = 0;
        Effect<int, string, TimeEnvironment> effect = Effect<TimeEnvironment>.Succeed(42);
        var limited = effect.Timeout(TimeSpan.Zero, _ => $"expired {++calls}");
        var environment = new TimeEnvironment(new NoTimerClock());

        // When
        var first = await limited.RunExitAsync(environment, TestContext.Current.CancellationToken);
        var second = await limited.RunExitAsync(environment, TestContext.Current.CancellationToken);

        // Then
        Assert.Equal("expired 1", Assert.IsType<Err<string>>(first.Value).Error);
        Assert.Equal("expired 2", Assert.IsType<Err<string>>(second.Value).Error);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_ZeroDurationInsideResource_ReleasesPreviouslyAcquiredValue(bool releaseThrows)
    {
        // Given
        var releases = 0;
        ExitCase? seen = null;
        var defect = new InvalidOperationException("release");
        Resource<int, TimeoutError, TimeEnvironment> resource = Resource.Make(
            Effect<TimeEnvironment>.Succeed(42),
            (_, exit) => Effect<TimeEnvironment>.Sync(() =>
            {
                seen = exit;
                releases++;
                if (releaseThrows) throw defect;
                return Unit.Value;
            }));
        var effect = resource.Use(static value => Effect<TimeEnvironment>.Succeed(value).Timeout(TimeSpan.Zero));

        // When
        var exit = await effect.RunExitAsync(new TimeEnvironment(new NoTimerClock()), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(1, releases);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<TimeoutError>(Assert.IsType<Failed>(seen!.Value.Value).Error).After);
        if (releaseThrows)
        {
            var die = Assert.IsType<Die>(exit.Value);
            Assert.Same(defect, die.Exception);
            Assert.IsType<TimeoutError>(Assert.IsType<Failed>(Assert.Single(die.Suppressed).Case.Value).Error);
        }
        else
        {
            Assert.Equal(TimeSpan.Zero, Assert.IsType<Err<TimeoutError>>(exit.Value).Error.After);
        }
    }

    [Fact]
    public async Task Timeout_ZeroDurationAroundThrowingBody_ReturnsTimeoutWithoutCallingBody()
    {
        // Given
        var effect = Effect<TimeEnvironment>.Sync<int>(() => throw new InvalidOperationException("body"));

        // When
        var exit = await effect.Timeout(TimeSpan.Zero)
            .RunExitAsync(new TimeEnvironment(new NoTimerClock()), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(TimeSpan.Zero, Assert.IsType<Err<TimeoutError>>(exit.Value).Error.After);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_ZeroDurationAroundConstantError_DoesNotRunTheFailedDescription(bool asResult)
    {
        // Given
        Effect<int, RequestError, TimeEnvironment> effect = Effect<TimeEnvironment>.Fail<RequestError>(new NotFound(42));
        var environment = new TimeEnvironment(new NoTimerClock());

        // When
        var outcome = asResult
            ? (await effect.TimeoutResult(TimeSpan.Zero).RunExitAsync(environment, TestContext.Current.CancellationToken)).Value
            : (await effect.Timeout(TimeSpan.Zero).RunExitAsync(environment, TestContext.Current.CancellationToken)).Value;

        // Then
        var error = asResult
            ? Assert.IsType<Err<TimeoutError>>(Assert.IsType<Ok<Result<int, TimeoutError>>>(outcome).Value.Value).Error
            : Assert.IsType<TimeoutError>(Assert.IsType<Err<RequestError>>(outcome).Error.Value);
        Assert.Equal(TimeSpan.Zero, error.After);
    }

    [Fact]
    public void Timeout_ZeroDurationOnDefaultEffect_StillRejectsInvalidDescription()
    {
        // Given
        Effect<int, Never, TimeEnvironment> effect = default;

        // When
        var exception = Record.Exception(() => effect.Timeout(TimeSpan.Zero));

        // Then
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void Timeout_ZeroDurationWithNullMapper_StillRejectsInvalidDelegate()
    {
        // Given
        Effect<int, string, TimeEnvironment> effect = Effect<TimeEnvironment>.Succeed(42);

        // When
        var exception = Record.Exception(() => effect.Timeout(TimeSpan.Zero, null!));

        // Then
        Assert.IsType<ArgumentNullException>(exception);
    }

    private sealed class NoTimerClock : TimeProvider
    {
        internal int Calls { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Calls++;
            throw new InvalidOperationException("A zero deadline needs no timer.");
        }
    }

    private sealed class UnreadableClockEnvironment : IHas<TimeProvider>
    {
        TimeProvider IHas<TimeProvider>.Service => throw new InvalidOperationException("A zero deadline needs no clock.");
    }
}
