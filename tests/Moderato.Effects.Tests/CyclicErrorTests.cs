using System.Runtime.CompilerServices;

namespace Moderato.Effects.Tests;

public class CyclicErrorTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    public async Task Use_ErrorUnionContainsAReferenceCycle_DiesAndReleasesWithoutReadingTwice(int count, int cycleStart)
    {
        // Given
        var nodes = Enumerable.Range(0, count).Select(_ => new LinkedError()).ToArray();
        for (var i = 0; i < count - 1; i++) nodes[i].Next = nodes[i + 1];
        nodes[^1].Next = nodes[cycleStart];
        var released = 0;
        ExitCase releaseExit = default;
        var resource = Resource.Make<int, LinkedError, EmptyEnvironment>(Effect<EmptyEnvironment>.Succeed(1), (_, exit) =>
            Effect<EmptyEnvironment>.Sync(() => { released++; releaseExit = exit; return Unit.Value; }));

        // When
        var exit = await resource.Use<int>(_ => Effect<EmptyEnvironment>.Fail(nodes[0]))
            .RunExitAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        var die = Assert.IsType<Die>(exit.Value);
        Assert.IsType<InvalidOperationException>(die.Exception);
        Assert.Equal("The error union contains a reference cycle.", die.Exception.Message);
        Assert.Equal(1, released);
        Assert.Same(die.Exception, Assert.IsType<Die>(releaseExit.Value).Exception);
        Assert.Same(nodes[cycleStart], Assert.IsType<Failed>(Assert.Single(die.Suppressed).Case.Value).Error);
        Assert.All(nodes, node => Assert.Equal(1, node.Reads));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(1025)]
    public async Task Use_ErrorUnionChainIsFinite_ReadsEachValueOnceAndKeepsTheError(int count)
    {
        // Given
        var nodes = Enumerable.Range(0, count).Select(_ => new LinkedError()).ToArray();
        for (var i = 0; i < count - 1; i++) nodes[i].Next = nodes[i + 1];
        nodes[^1].Next = "leaf";
        ExitCase releaseExit = default;
        var resource = Resource.Make<int, LinkedError, EmptyEnvironment>(Effect<EmptyEnvironment>.Succeed(1), (_, exit) =>
            Effect<EmptyEnvironment>.Sync(() => { releaseExit = exit; return Unit.Value; }));

        // When
        var exit = await resource.Use<int>(_ => Effect<EmptyEnvironment>.Fail(nodes[0]))
            .RunExitAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Same(nodes[0], Assert.IsType<Err<LinkedError>>(exit.Value).Error);
        Assert.Equal("leaf", Assert.IsType<Failed>(releaseExit.Value).Error);
        Assert.All(nodes, node => Assert.Equal(1, node.Reads));
    }

    private sealed class LinkedError : IUnion
    {
        internal object? Next { get; set; }

        internal int Reads { get; private set; }

        public object? Value
        {
            get
            {
                // Bound a regression's spin so the old implementation cannot hang the test host.
                if (++Reads > 4) throw new InvalidOperationException("Test watchdog: value was read repeatedly.");
                return Next;
            }
        }

        public override bool Equals(object? obj) => throw new InvalidOperationException("User equality must not run.");

        public override int GetHashCode() => throw new InvalidOperationException("User hashing must not run.");
    }
}
