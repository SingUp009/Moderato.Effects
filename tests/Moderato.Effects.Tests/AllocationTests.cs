using Moderato.Effects.Testing;

namespace Moderato.Effects.Tests;

public class AllocationTests
{
    public static TheoryData<AllocationCase, bool> Cases
    {
        get
        {
            var cases = new TheoryData<AllocationCase, bool>();
            foreach (var scenario in Enum.GetValues<AllocationCase>())
            {
                cases.Add(scenario, false);
                cases.Add(scenario, true);
            }

            return cases;
        }
    }

    public static TheoryData<SchedulingAllocationCase, bool> SchedulingCases
    {
        get
        {
            var cases = new TheoryData<SchedulingAllocationCase, bool>();
            foreach (var scenario in Enum.GetValues<SchedulingAllocationCase>())
            {
                cases.Add(scenario, false);
                cases.Add(scenario, true);
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PrebuiltSynchronousEffect_WithNoCancellation_AllocatesZeroBytes(AllocationCase scenario, bool exit)
    {
        var (effect, expected) = AllocationScenarios.Create(scenario);
        if (exit)
        {
            Exit<int, string> expectedExit = expected.TryGetOk(out var value) ? new(new Ok<int>(value))
                : expected.TryGetErr(out var error) ? new(new Err<string>(error))
                : throw new InvalidOperationException("The scenario must have an initialized expected result.");
            await AssertZeroAllocation(() => effect.RunExitAsync(CancellationToken.None), expectedExit);
        }
        else
        {
            await AssertZeroAllocation(() => effect.RunAsync(CancellationToken.None), expected);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValueTypeEnvironmentAndError_DoNotBoxOnEachRun(bool fail)
    {
        Result<int, int> input = fail ? Result.Err(7) : Result.Ok(42);
        Result<int, int> expected = fail ? Result.Err(8) : Result.Ok(43);
        var effect = Effect<int>.FromResult(input).Map(static x => x + 1).MapErr(static error => error + 1);
        await AssertZeroAllocation(() => effect.RunAsync(0, CancellationToken.None), expected);
    }

    [Theory]
    [MemberData(nameof(SchedulingCases))]
    public async Task BuiltInScheduling_WithNoCancellation_AllocatesZeroBytes(SchedulingAllocationCase scenario, bool exit)
    {
        var (effect, expected) = SchedulingAllocationScenarios.Create(scenario);
        if (exit)
        {
            Exit<int, string> expectedExit = expected.TryGetOk(out var value) ? new(new Ok<int>(value))
                : expected.TryGetErr(out var error) ? new(new Err<string>(error))
                : throw new InvalidOperationException("The scenario must have an initialized expected result.");
            await AssertZeroAllocation(() => effect.RunExitAsync(CancellationToken.None), expectedExit);
        }
        else
        {
            await AssertZeroAllocation(() => effect.RunAsync(CancellationToken.None), expected);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task BuiltInScheduling_WithValueTypeClock_DoesNotBox(int operation, bool exit)
    {
        var (effect, expected) = ValueClockAllocationScenarios.Create(operation);
        if (exit)
        {
            Exit<int, string> expectedExit = operation == 1 ? new(new Err<string>("failed")) : new(new Ok<int>(20));
            await AssertZeroAllocation(() => effect.RunExitAsync(default, CancellationToken.None), expectedExit);
        }
        else
        {
            await AssertZeroAllocation(() => effect.RunAsync(default, CancellationToken.None), expected);
        }
    }

    private static async Task AssertZeroAllocation<T>(Func<ValueTask<T>> run, T expected)
    {
        // Warm up generic statics, cached delegates and tiered compilation before
        // measuring. Assertions and the test runner stay outside this region.
        for (var i = 0; i < 1024; i++) await run();

        T result = default!;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 4096; i++)
        {
            var task = run();
            if (!task.IsCompletedSuccessfully) throw new InvalidOperationException("The allocation contract requires synchronous completion.");
            result = await task;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, result);
        Assert.Equal(0L, allocated);
    }
}
