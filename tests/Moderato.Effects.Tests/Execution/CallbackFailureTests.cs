using Moderato.Results;

namespace Moderato.Effects.Tests;

public class CallbackFailureTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task RunExitAsync_CallbackThrows_KeepsFrameworkFlatteningSemantics(int shape)
    {
        // Given
        var first = new IOException("first callback failure");
        var second = new IOException("second callback failure");
        Exception callback = shape switch
        {
            0 => first,
            1 => new AggregateException("single", first),
            2 => new AggregateException("outer", new AggregateException("inner", first)),
            3 => new AggregateException("multiple", first, second),
            4 => new AggregateException("empty"),
            5 => new AggregateException("mixed", new AggregateException("empty"), first),
            6 => new AggregateException("outer", new AggregateException("empty")),
            _ => new AggregateException("branches", new AggregateException("inner", first), second),
        };
        using var reference = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(reference.Token);
        using var referenceRegistration = linked.Token.Register(() => throw callback);
        var expected = Assert.Throws<AggregateException>(() => reference.Cancel()).Flatten();
        using var caller = new CancellationTokenSource();
        var body = new IOException("body failed");
        var failure = Effect<CancellationTokenSource>.AccessAsync<int>((source, ct) =>
        {
            using var registration = ct.Register(() => throw callback);
            source.Cancel();
            throw body;
        });
        var work = Resource.Make(Effect<CancellationTokenSource>.Succeed(Unit.Value),
                static (_, _) => Effect<CancellationTokenSource>.Succeed(Unit.Value))
            .Use(_ => failure);

        // When
        var exit = await work.RunExitAsync(caller, caller.Token);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(body, die.Exception);
        var context = Assert.Single(die.Suppressed);
        Assert.Equal(Relation.Consequence, context.Relation);
        var actual = Assert.IsType<Die>(context.Case.Value).Exception;
        if (expected.InnerExceptions.Count == 1)
        {
            Assert.Same(expected.InnerExceptions[0], actual);
        }
        else
        {
            var aggregate = Assert.IsType<AggregateException>(actual);
            Assert.Equal(expected.Message, aggregate.Message);
            Assert.Equal(expected.InnerExceptions, aggregate.InnerExceptions);
        }
    }
}
