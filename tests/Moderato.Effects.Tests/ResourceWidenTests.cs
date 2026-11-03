namespace Moderato.Effects.Tests;

public class ResourceWidenTests
{
    [Fact]
    public async Task Widen_AcquisitionSucceeds_KeepsTheValueAndRelease()
    {
        // Given
        var log = new List<string>();
        int? releasedValue = null;
        ExitCase releasedExit = default;
        Effect<int, NotFound, EmptyEnvironment> acquire = Effect<EmptyEnvironment>.Sync(() => { log.Add("acquire"); return 7; });
        var resource = Resource.Make(acquire, (value, exit) => Effect<EmptyEnvironment>.Sync(() =>
        {
            log.Add("release");
            releasedValue = value;
            releasedExit = exit;
            return Unit.Value;
        }));

        // When
        var exit = await resource.Widen<AppError>().Use<int>(value => Effect<EmptyEnvironment>.Sync(() =>
        {
            log.Add("body");
            return value * 2;
        })).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(14, Assert.IsType<Ok<int>>(exit.Value).Value);
        Assert.Equal(["acquire", "body", "release"], log);
        Assert.Equal(7, releasedValue);
        Assert.True(releasedExit is Succeeded);
    }

    [Fact]
    public async Task Widen_AcquisitionFails_KeepsTheErrorWithoutUsingOrReleasing()
    {
        // Given
        var expected = new NotFound(3);
        var released = 0;
        var used = 0;
        Effect<int, NotFound, EmptyEnvironment> acquire = Effect<EmptyEnvironment>.Fail(expected);
        var resource = Resource.Make(acquire, (_, _) => Effect<EmptyEnvironment>.Sync(() => { released++; return Unit.Value; }));

        // When
        var exit = await resource.Widen<AppError>()
            .Use<int>(value => Effect<EmptyEnvironment>.Sync(() => { used++; return value; }))
            .RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        var error = Assert.IsType<Err<AppError>>(exit.Value).Error;
        Assert.Same(expected, Assert.IsType<NotFound>(error.Value));
        Assert.Equal(0, used);
        Assert.Equal(0, released);
    }

    [Fact]
    public void Widen_DefaultResource_Throws()
    {
        // Given
        var resource = default(Resource<int, NotFound, EmptyEnvironment>);

        // When
        void Act() => resource.Widen<AppError>();

        // Then
        Assert.Throws<InvalidOperationException>(Act);
    }
}
