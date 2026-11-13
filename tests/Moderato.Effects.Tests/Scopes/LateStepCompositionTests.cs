namespace Moderato.Effects.Tests;

public class LateStepCompositionTests
{
    private static Effect<int, string, EmptyEnvironment> EndsBeforeItsStep(bool stepDies, Action? beforeReturn = null)
        => Effect.Gen<int, string, EmptyEnvironment>(async scope =>
        {
#pragma warning disable MEFF310
            _ = scope.Run(Effect<EmptyEnvironment>.TryAsync<IOException, int, string>(async ct =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    if (stepDies) throw new InvalidOperationException("late defect");
                    throw new IOException("closed");
                }
                return 0;
            }, static _ => "late"));
#pragma warning restore MEFF310
            await Task.Yield();
            beforeReturn?.Invoke();
            return 1;
        });

    private static Effect<int, string, EmptyEnvironment> Compose(string composition, Effect<int, string, EmptyEnvironment> block)
    {
        Effect<int, string, EmptyEnvironment> other = Effect<EmptyEnvironment>.Succeed(2);
        return composition switch
        {
            "alone" => block,
            "all" => Effect.All([block]).Map(static values => values[0]),
            "parallel" => Effect.All([block, other], Concurrency.Parallel).Map(static values => values[0]),
            "tuple" => Effect.All(block, other).Map(static values => values.Item1),
            "race" => Effect.Race(block, block),
            "raceFirst" => Effect.RaceFirst(block, block),
            _ => Effect.AllErrors([block]).Map(static values => values[0]).MapErr(static errors => errors[0]),
        };
    }

    [Theory]
    [InlineData("alone")]
    [InlineData("all")]
    [InlineData("parallel")]
    [InlineData("tuple")]
    [InlineData("race")]
    [InlineData("raceFirst")]
    [InlineData("allErrors")]
    public async Task Gen_LateStepTurnsTheBlocksEndIntoErr_EndsWithThatErrInAnyComposition(string composition)
    {
        // Given
        var effect = Compose(composition, EndsBeforeItsStep(stepDies: false));

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(new Err<string>("late"), exit);
    }

    [Theory]
    [InlineData("alone")]
    [InlineData("all")]
    [InlineData("race")]
    public async Task Gen_LateStepDiesWhenTheBlockEnds_DiesInAnyComposition(string composition)
    {
        // Given
        var effect = Compose(composition, EndsBeforeItsStep(stepDies: true));

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Equal("late defect", die.Exception.Message);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("race")]
    public async Task Gen_CallerCancelsBeforeTheLateStepFails_GroupIsInterrupted(string composition)
    {
        // Given
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var effect = Compose(composition, EndsBeforeItsStep(stepDies: false, beforeReturn: caller.Cancel));

        // When
        var exit = await effect.RunExitAsync(EmptyEnvironment.Value, caller.Token);

        // Then
        Assert.True(exit.TryGetValue(out Interrupted? _));
    }
}
