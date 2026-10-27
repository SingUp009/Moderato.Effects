namespace Moderato.Effects.Tests;

public class ReleaseReuseTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UseOrGen_OverlappingReleases_PreserveResultsAndIgnoreCallerCancellation(bool gen, bool fail)
    {
        for (var batch = 0; batch < 3; batch++)
        {
            // Given
            const int COUNT = 32;
            var environments = Enumerable.Range(0, COUNT).Select(i => new Environment(batch * COUNT + i, fail)).ToArray();
            var callers = Enumerable.Range(0, COUNT).Select(_ => new CancellationTokenSource()).ToArray();
            var resource = Resource.Make(Effect<Environment>.Access(env => env.Value), (value, exit) =>
                Effect<Environment>.AccessAsync<Unit>(async (env, token) =>
                {
                    env.Acquired = value;
                    env.Exit = exit;
                    env.Token = token;
                    env.Started.SetResult();
                    await env.Finish.Task.ConfigureAwait(false);
                    if (env.Failure is { } failure) throw failure;
                    return Unit.Value;
                }));
            var effect = gen
                ? Scope<Never, Environment>.Gen(async scope => await scope.Acquire(resource))
                : resource.Use(value => Effect<Environment>.Succeed(value));
            var runs = new Task<Exit<int, Never>>[COUNT];
            try
            {
                // When
                for (var i = 0; i < COUNT; i++) runs[i] = effect.RunExitAsync(environments[i], callers[i].Token).AsTask();
                await Task.WhenAll(environments.Select(env => env.Started.Task)).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                for (var i = COUNT - 1; i >= 0; i--)
                {
                    callers[i].Cancel();
                    Assert.False(runs[i].IsCompleted);
                    environments[i].Finish.SetResult();
                }
                var results = await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

                // Then
                for (var i = 0; i < COUNT; i++)
                {
                    var env = environments[i];
                    Assert.Equal(env.Value, env.Acquired);
                    Assert.IsType<Succeeded>(env.Exit.Value);
                    Assert.False(env.Token.CanBeCanceled);
                    if (fail)
                    {
                        Assert.True(results[i].TryGetValue(out Die? die));
                        Assert.Same(env.Failure, die.Exception);
                        Assert.Empty(die.Suppressed);
                        Assert.Equal(0, die.OmittedSuppressed);
                    }
                    else
                    {
                        Assert.True(results[i].TryGetValue(out Ok<int> value));
                        Assert.Equal(env.Value, value.Value);
                    }
                }
            }
            finally
            {
                foreach (var caller in callers) caller.Dispose();
            }
        }
    }

    private sealed class Environment(int value, bool fail)
    {
        public int Value { get; } = value;
        public IOException? Failure { get; } = fail ? new IOException($"release {value}") : null;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Acquired;
        public ExitCase Exit;
        public CancellationToken Token;
    }
}
