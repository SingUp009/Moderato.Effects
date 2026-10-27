using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class RecurrenceAwaitTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task RetryOrRepeat_PendingExceptionWithoutCallerIsolation_PreservesFailureContext(bool retry, bool policyFails, bool cancellationException)
    {
        // Given
        Exception failure = cancellationException ? new OperationCanceledException() : new IOException("failed");
        var carried = new Suppressed(new Failed("carried"), Relation.Parallel);
        var last = (retry ? Outcome<int, string>.Err("last") : Outcome<int, string>.Ok(42)).WithSuppressed([carried], 2);
        var bodyCompletion = new TaskCompletionSource<Outcome<int, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var policyCompletion = new TaskCompletionSource<Outcome<bool, Never>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var source = new Effect<int, string, EmptyEnvironment>((_, _) =>
        {
            calls++;
            return policyFails ? new(last) : new(bodyCompletion.Task);
        });
        var policy = new Effect<bool, Never, EmptyEnvironment>((_, _) => new(policyCompletion.Task));
        var effect = retry ? source.Retry(_ => policy) : source.Repeat(_ => policy);
        Assert.Equal(0, calls);

        // When
        // None keeps caller isolation from masking the recurrence's own exception boundaries.
        var running = effect.RunExitAsync(CancellationToken.None);
        Assert.False(running.IsCompleted);
        if (policyFails) policyCompletion.SetException(failure);
        else bodyCompletion.SetException(failure);
        var exit = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(1, calls);
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Suppressed[] expected = policyFails
            ? retry ? [new(new Failed("last"), Relation.Sequential), carried] : [carried]
            : [];
        Assert.Equal(expected, die.Suppressed);
        Assert.Equal(policyFails ? 2 : 0, die.OmittedSuppressed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetryOrRepeat_StepsChangeAsyncLocal_IsolatesEachStep(bool retry, bool pending)
    {
        // Given
        var local = new AsyncLocal<int> { Value = 5 };
        var bodies = new List<int>();
        var policies = new List<int>();
        Result<int, string> end = retry ? Result.Err("last") : Result.Ok(42);
        var completion = new TaskCompletionSource<Result<int, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = Effect<EmptyEnvironment>.FromResultAsync<int, string>(_ =>
        {
            bodies.Add(local.Value);
            local.Value = 10;
            return pending ? new(completion.Task) : new ValueTask<Result<int, string>>(end);
        });
        Effect<bool, Never, EmptyEnvironment> Decide(int count)
        {
            policies.Add(local.Value);
            local.Value = 20;
            return Effect<EmptyEnvironment>.Succeed(count < 2);
        }
        var effect = retry ? source.Retry(ctx => Decide(ctx.Attempt)) : source.Repeat(ctx => Decide(ctx.Repetition));

        // When
        var running = effect.RunAsync(CancellationToken.None);
        if (pending) Assert.False(running.IsCompleted);
        completion.SetResult(end);
        var result = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal<Result<int, string>>(retry ? Result.Err("last") : Result.Ok(42), result);
        Assert.Equal([5, 5], bodies);
        Assert.Equal([5, 5], policies);
        Assert.Equal(5, local.Value);
    }
}
