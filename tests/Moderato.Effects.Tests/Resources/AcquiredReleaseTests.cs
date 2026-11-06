using Moderato.Results;

namespace Moderato.Effects.Tests;

public class AcquiredReleaseTests
{
    [Theory]
    [InlineData("Ok", false)]
    [InlineData("Err", false)]
    [InlineData("Die", false)]
    [InlineData("Ok", true)]
    [InlineData("Err", true)]
    [InlineData("Die", true)]
    public async Task Acquire_ValueAndReferenceResourcesMixWithDefer_PreservesValuesOrderAndBodyExit(string kind, bool releaseThrows)
    {
        // Given
        var sequence = 0;
        var bodyFailure = new IOException("body");
        var cleanupFailure = new IOException("cleanup");
        var events = new List<(string Name, int Value, ExitCase Exit)>();
        var acquired = new List<Box>();
        var released = new List<Box>();
        var resource = Resource.Make(Effect<EmptyEnvironment>.Sync(() => ++sequence), (value, exit) =>
            Effect<EmptyEnvironment>.Sync(() => { events.Add(("value", value, exit)); return Unit.Value; }));
        var work = Scope<string, EmptyEnvironment>.Gen<int>(async scope =>
        {
            var value = await scope.Acquire(resource);
            scope.Defer(exit => Effect<EmptyEnvironment>.Sync(() => { events.Add(("middle", value, exit)); return Unit.Value; }));
            var box = new Box(value);
            acquired.Add(box);
            await scope.Acquire(Resource.Make(Effect<EmptyEnvironment>.Succeed(box), (original, exit) =>
                Effect<EmptyEnvironment>.Sync(() =>
                {
                    released.Add(original);
                    events.Add(("reference", original.Value, exit));
                    if (releaseThrows) throw cleanupFailure;
                    return Unit.Value;
                })));
            scope.Defer(exit => Effect<EmptyEnvironment>.Sync(() => { events.Add(("last", value, exit)); return Unit.Value; }));
            if (kind == "Err") throw scope.Fail("body error");
            if (kind == "Die") throw bodyFailure;
            return value;
        });
        var exits = new Exit<int, string>[2];

        // When
        for (var i = 0; i < exits.Length; i++) exits[i] = await work.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(8, events.Count);
        Assert.Equal(2, acquired.Count);
        Assert.Equal(2, released.Count);
        for (var i = 0; i < exits.Length; i++)
        {
            Assert.Same(acquired[i], released[i]);
            var recorded = events.Skip(i * 4).Take(4).ToArray();
            Assert.Equal(["last", "reference", "middle", "value"], recorded.Select(entry => entry.Name));
            Assert.All(recorded, entry =>
            {
                Assert.Equal(i + 1, entry.Value);
                Assert.Equal(recorded[0].Exit, entry.Exit);
            });
            if (kind == "Ok") Assert.True(recorded[0].Exit.Value is Succeeded);
            else if (kind == "Err") Assert.Equal("body error", Assert.IsType<Failed>(recorded[0].Exit.Value).Error);
            else Assert.Same(bodyFailure, Assert.IsType<Die>(recorded[0].Exit.Value).Exception);

            if (kind == "Die" || releaseThrows)
            {
                Assert.True(exits[i].TryGetValue(out Die? die));
                Assert.Same(kind == "Die" ? bodyFailure : cleanupFailure, die.Exception);
                Assert.Equal(releaseThrows && kind != "Ok" ? 1 : 0, die.Suppressed.Count);
                if (releaseThrows && kind == "Die") Assert.Same(cleanupFailure, Assert.IsType<Die>(die.Suppressed[0].Case.Value).Exception);
                if (releaseThrows && kind == "Err") Assert.Equal("body error", Assert.IsType<Failed>(die.Suppressed[0].Case.Value).Error);
            }
            else if (kind == "Err")
            {
                Assert.True(exits[i].TryGetValue(out Err<string> error));
                Assert.Equal("body error", error.Error);
            }
            else
            {
                Assert.True(exits[i].TryGetValue(out Ok<int> ok));
                Assert.Equal(i + 1, ok.Value);
            }
        }
    }

    private sealed record Box(int Value);
}
