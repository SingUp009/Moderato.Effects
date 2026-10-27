using Microsoft.Extensions.Time.Testing;

namespace Moderato.Effects.Tests;

public class ScheduleReuseTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetryOrRepeat_OverlappingScheduledRuns_PreserveResultsAndCancellation(bool retry, bool cancel)
    {
        for (var batch = 0; batch < 3; batch++)
        {
            // Given
            const int COUNT = 32;
            var environments = Enumerable.Range(0, COUNT).Select(i => new Environment(batch * COUNT + i)).ToArray();
            var callers = Enumerable.Range(0, COUNT).Select(_ => new CancellationTokenSource()).ToArray();
            Effect<int, int, Environment> source = Effect<Environment>.Access(env => env.Value);
            Effect<int, int, Environment> body = retry
                ? source.AndThen<int>(value => Effect<Environment>.Fail(value))
                : source;
            var schedule = Schedule.Spaced(TimeSpan.FromHours(1)).UpTo(1);
            var effect = retry ? body.Retry(schedule) : body.Repeat(schedule);
            var runs = new Task<Exit<int, int>>[COUNT];
            try
            {
                // When
                for (var i = 0; i < COUNT; i++)
                {
                    var run = effect.RunExitAsync(environments[i], callers[i].Token);
                    Assert.False(run.IsCompleted);
                    runs[i] = run.AsTask();
                }
                for (var i = COUNT - 1; i >= 0; i--)
                {
                    if (cancel && i % 2 != 0) callers[i].Cancel();
                    else environments[i].Clock.Advance(TimeSpan.FromHours(1));
                }
                var results = await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

                // Then
                for (var i = 0; i < COUNT; i++)
                {
                    if (cancel && i % 2 != 0)
                    {
                        Assert.True(results[i].TryGetValue(out Interrupted? _));
                    }
                    else if (retry)
                    {
                        Assert.True(results[i].TryGetValue(out Err<int> error));
                        Assert.Equal(environments[i].Value, error.Error);
                    }
                    else
                    {
                        Assert.True(results[i].TryGetValue(out Ok<int> value));
                        Assert.Equal(environments[i].Value, value.Value);
                    }
                }
            }
            finally
            {
                foreach (var caller in callers) caller.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetryOrRepeat_WaitChangesAsyncLocal_IsolatesTheWait(bool retry, bool pending)
    {
        // Given
        var local = new AsyncLocal<int> { Value = 5 };
        var environment = new Environment(42, local);
        var observed = new List<int>();
        Effect<int, int, Environment> source = Effect<Environment>.Sync(() =>
        {
            observed.Add(local.Value);
            return 42;
        });
        var body = retry ? source.AndThen<int>(value => Effect<Environment>.Fail(value)) : source;
        var schedule = Schedule.Spaced(pending ? TimeSpan.FromHours(1) : TimeSpan.Zero).UpTo(1);
        var effect = retry ? body.Retry(schedule) : body.Repeat(schedule);

        // When
        var running = effect.RunAsync(environment, CancellationToken.None);
        if (pending)
        {
            Assert.False(running.IsCompleted);
            environment.Clock.Advance(TimeSpan.FromHours(1));
        }
        var result = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, int>>(retry ? Result.Err(42) : Result.Ok(42), result);
        Assert.Equal([5, 5], observed);
        Assert.Equal(5, local.Value);
        Assert.Equal(2, environment.Resolutions);
    }

    private sealed class Environment(int value, AsyncLocal<int>? local = null) : IHas<TimeProvider>
    {
        public int Value { get; } = value;
        public FakeTimeProvider Clock { get; } = new();
        public int Resolutions { get; private set; }
        TimeProvider IHas<TimeProvider>.Service
        {
            get
            {
                if (++Resolutions > 1 && local is not null) local.Value = 99;
                return Clock;
            }
        }
    }
}
