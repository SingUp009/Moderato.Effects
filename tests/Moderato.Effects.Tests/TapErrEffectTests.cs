namespace Moderato.Effects.Tests;

public class TapErrEffectTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1000)]
    [InlineData(true, 1000)]
    public async Task TapErr_ObserverFails_KeepsOriginalError(bool asynchronous, int depth)
    {
        // Given
        var defect = new IOException("observer");
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Fail("original");
        for (var i = 0; i < depth; i++) effect = effect.Map(static value => value + 1);
        var observed = effect.TapErr(_ => Effect<EmptyEnvironment>.Async<Unit>(async _ =>
        {
            if (asynchronous) await Task.Yield();
            throw defect;
        }));

        // When
        var exit = await observed.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        var die = Assert.IsType<Die>(exit.Value);
        Assert.Same(defect, die.Exception);
        var suppressed = Assert.Single(die.Suppressed);
        Assert.Equal(Relation.Sequential, suppressed.Relation);
        Assert.Equal("original", Assert.IsType<Failed>(suppressed.Case.Value).Error);
    }

    [Fact]
    public async Task TapErr_ObserverSucceeds_LeavesErrorForRecovery()
    {
        // Given
        var seen = new List<string>();
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Fail("original");
        var observed = effect.TapErr(error => Effect<EmptyEnvironment>.Sync(() => { seen.Add(error); return 7; }));

        // When
        var exit = await observed.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal("original", Assert.IsType<Err<string>>(exit.Value).Error);
        Assert.Equal(new[] { "original" }, seen);
    }

    [Fact]
    public async Task TapErr_Ok_SkipsObserver()
    {
        // Given
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Succeed(7);
        var called = false;
        var observed = effect.TapErr(_ => Effect<EmptyEnvironment>.Sync(() => { called = true; return Unit.Value; }));

        // When
        var exit = await observed.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(7, Assert.IsType<Ok<int>>(exit.Value).Value);
        Assert.False(called);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TapErr_InvalidObserver_KeepsErrorAsContext(bool throws)
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Fail("original");
        var observed = effect.TapErr<Unit>(_ => throws ? throw new IOException("construction") : default);

        // When
        var exit = await observed.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        var die = Assert.IsType<Die>(exit.Value);
        Assert.Equal("original", Assert.IsType<Failed>(Assert.Single(die.Suppressed).Case.Value).Error);
    }

    [Fact]
    public async Task TapErr_ObserverUsesEnvironmentAndIsCanceled_ReturnsInterrupted()
    {
        // Given
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var environment = new object();
        object? observedEnvironment = null;
        var effect = Effect<object>.Fail("original").TapErr(_ => Effect<object>.AccessAsync<Unit>((services, token) =>
        {
            observedEnvironment = services;
            caller.Cancel();
            return ValueTask.FromCanceled<Unit>(token);
        }));

        // When
        var exit = await effect.RunExitAsync(environment, caller.Token);

        // Then
        Assert.IsType<Interrupted>(exit.Value);
        Assert.Same(environment, observedEnvironment);
    }

    [Fact]
    public void TapErr_NullEffectObserver_ThrowsArgumentNullException()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Fail("original");

        // When
        var exception = Assert.Throws<ArgumentNullException>(() => effect.TapErr<Unit>(null!));

        // Then
        Assert.Equal("action", exception.ParamName);
    }

    [Fact]
    public async Task TapErr_ErrorAndObserverHaveContext_PreservesTheirOrder()
    {
        // Given
        var earlier = new IOException("earlier");
        var later = new IOException("later");
        var final = new IOException("final");
        var effect = RacedContext(new Result<Unit, string>(new Err<string>("original")), earlier)
            .TapErr(_ => RacedContext(new Result<Unit, Never>(new Ok<Unit>(Unit.Value)), later))
            .TapErr(_ => throw final);

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        var die = Assert.IsType<Die>(exit.Value);
        Assert.Same(final, die.Exception);
        Assert.Equal(3, die.Suppressed.Count);
        Assert.Equal("original", Assert.IsType<Failed>(die.Suppressed[0].Case.Value).Error);
        Assert.Same(earlier, Assert.IsType<Die>(die.Suppressed[1].Case.Value).Exception);
        Assert.Same(later, Assert.IsType<Die>(die.Suppressed[2].Case.Value).Exception);
    }

    private static Effect<Unit, E, EmptyEnvironment> RacedContext<E>(Result<Unit, E> result, Exception defect)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winner = Effect<EmptyEnvironment>.FromResultAsync<Unit, E>(async _ =>
        {
            await entered.Task;
            return result;
        });
        Effect<Unit, E, EmptyEnvironment> loser = Effect<EmptyEnvironment>.Async<Unit>(async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw defect; }
            return Unit.Value;
        });
        return Effect.RaceFirst(winner, loser);
    }

    [Fact]
    public async Task TapErr_LegacyThrowOnlyLambda_RemainsUnambiguous()
    {
        // Given
        var effect = Effect<EmptyEnvironment>.Fail("original");

        // When
        var exit = await effect.TapErr(_ => throw new IOException("observer"))
            .RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.IsType<Die>(exit.Value);
    }
}
