using System.Runtime.ExceptionServices;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class RecurrenceContextTests
{
    [Theory]
    [InlineData(false, 0, 100)]
    [InlineData(false, 99, 0)]
    [InlineData(false, 100, 100)]
    [InlineData(true, 0, 100)]
    [InlineData(true, 0, 0)]
    [InlineData(true, 50, 0)]
    [InlineData(true, 50, 49)]
    [InlineData(true, 50, 100)]
    [InlineData(true, 99, 0)]
    [InlineData(true, 99, 100)]
    [InlineData(true, 100, 100)]
    public async Task RunExitAsync_PolicyDies_KeepsPolicyContextBeforeLastAttempt(bool retry, int policyCount, int lastCount)
    {
        // Given
        var failure = new IOException("policy");
        var policyContext = Enumerable.Range(0, policyCount).Select(i => new Suppressed(new Failed($"policy {i}"), Relation.Parallel)).ToArray();
        var lastContext = Enumerable.Range(0, lastCount).Select(i => new Suppressed(new Failed($"last {i}"), Relation.Consequence)).ToArray();
        var last = (retry ? Outcome<int, string>.Err("last error") : Outcome<int, string>.Ok(42)).WithSuppressed([.. lastContext], 2);
        var attempts = 0;
        var source = new Effect<int, string, EmptyEnvironment>((_, _) => { attempts++; return new(last); });
        var decision = Outcome<bool, Never>.Die(ExceptionDispatchInfo.Capture(failure), [.. policyContext], omitted: 3);
        var policy = new Effect<bool, Never, EmptyEnvironment>((_, _) => new(decision));
        Suppressed[] expected = retry
            ? [.. policyContext, new(new Failed("last error"), Relation.Sequential), .. lastContext]
            : [.. policyContext, .. lastContext];

        // When
        var exit = retry
            ? await source.Retry(_ => policy).RunExitAsync(TestContext.Current.CancellationToken)
            : await source.Repeat(_ => policy).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(1, attempts);
        Assert.Equal(expected.Take(100), die.Suppressed);
        Assert.Equal(5 + Math.Max(0, expected.Length - 100), die.OmittedSuppressed);
        for (var i = 0; i < policyCount; i++) Assert.Same(policyContext[i], die.Suppressed[i]);
    }
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, int.MaxValue)]
    [InlineData(true, int.MaxValue)]
    public async Task RunExitAsync_PolicyContextIsFull_StillReadsDiscardedError(bool throws, int omitted)
    {
        // Given
        var error = new ObservedError(throws);
        var context = Enumerable.Range(0, 100).Select(i => new Suppressed(new Failed(i), Relation.Parallel)).ToArray();
        var last = Outcome<int, ObservedError>.Err(error).WithSuppressed([.. context], omitted);
        var source = new Effect<int, ObservedError, EmptyEnvironment>((_, _) => new(last));
        var failure = new IOException("policy");
        var decision = Outcome<bool, Never>.Die(ExceptionDispatchInfo.Capture(failure), [.. context], omitted: 3);
        var policy = new Effect<bool, Never, EmptyEnvironment>((_, _) => new(decision));

        // When
        var exit = await source.Retry(_ => policy).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(context, die.Suppressed);
        Assert.Equal(unchecked(omitted + 104), die.OmittedSuppressed);
        Assert.Equal(1, error.Reads);
    }

    private sealed class ObservedError(bool throws) : System.Runtime.CompilerServices.IUnion
    {
        internal int Reads { get; private set; }

        public object? Value
        {
            get
            {
                Reads++;
                if (throws) throw new IOException("error projection");
                return "last error";
            }
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(50, false)]
    [InlineData(99, false)]
    [InlineData(0, true)]
    [InlineData(50, true)]
    [InlineData(99, true)]
    public async Task RunExitAsync_RetryErrorIsProjected_KeepsProjectionBetweenPolicyAndAttemptContext(int policyCount, bool throws)
    {
        // Given
        var projectionFailure = new IOException("projection");
        var error = new ProjectedError(throws ? projectionFailure : null);
        var context = Enumerable.Range(0, 100).Select(i => new Suppressed(new Failed(i), Relation.Parallel)).ToArray();
        var last = Outcome<int, ProjectedError>.Err(error).WithSuppressed([.. context], 2);
        var source = new Effect<int, ProjectedError, EmptyEnvironment>((_, _) => new(last));
        var failure = new IOException("policy");
        var decision = Outcome<bool, Never>.Die(ExceptionDispatchInfo.Capture(failure), [.. context.Take(policyCount)], omitted: 3);
        var policy = new Effect<bool, Never, EmptyEnvironment>((_, _) => new(decision));

        // When
        var exit = await source.Retry(_ => policy).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(100, die.Suppressed.Count);
        Assert.Equal(context.Take(policyCount), die.Suppressed.Take(policyCount));
        Assert.Equal(context.Take(99 - policyCount), die.Suppressed.Skip(policyCount + 1));
        Assert.Equal(Relation.Sequential, die.Suppressed[policyCount].Relation);
        if (throws)
        {
            var projected = Assert.IsType<Die>(die.Suppressed[policyCount].Case.Value);
            Assert.Same(projectionFailure, projected.Exception);
            Assert.Same(error, Assert.IsType<Failed>(Assert.Single(projected.Suppressed).Case.Value).Error);
        }
        else
        {
            Assert.Equal("last error", Assert.IsType<Failed>(die.Suppressed[policyCount].Case.Value).Error);
        }
        Assert.Equal(6 + policyCount, die.OmittedSuppressed);
        Assert.Equal(1, error.Reads);
    }

    private sealed class ProjectedError(Exception? failure) : System.Runtime.CompilerServices.IUnion
    {
        internal int Reads { get; private set; }

        public object? Value
        {
            get
            {
                Reads++;
                if (failure is not null) throw failure;
                return "last error";
            }
        }
    }
}
