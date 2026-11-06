namespace Moderato.Effects.Tests;

public class LateRecordedErrorTests
{
    [Theory]
    [InlineData("Run", "None", false, false)]
    [InlineData("Run", "None", false, true)]
    [InlineData("Run", "None", true, false)]
    [InlineData("Run", "None", true, true)]
    [InlineData("Run", "AsTask", false, false)]
    [InlineData("Run", "AsTask", false, true)]
    [InlineData("Run", "AsTask", true, false)]
    [InlineData("Run", "AsTask", true, true)]
    [InlineData("Run", "Preserve", false, false)]
    [InlineData("Run", "Preserve", false, true)]
    [InlineData("Run", "Preserve", true, false)]
    [InlineData("Run", "Preserve", true, true)]
    [InlineData("Acquire", "None", false, false)]
    [InlineData("Acquire", "None", false, true)]
    [InlineData("Acquire", "None", true, false)]
    [InlineData("Acquire", "None", true, true)]
    [InlineData("Acquire", "AsTask", false, false)]
    [InlineData("Acquire", "AsTask", false, true)]
    [InlineData("Acquire", "AsTask", true, false)]
    [InlineData("Acquire", "AsTask", true, true)]
    [InlineData("Acquire", "Preserve", false, false)]
    [InlineData("Acquire", "Preserve", false, true)]
    [InlineData("Acquire", "Preserve", true, false)]
    [InlineData("Acquire", "Preserve", true, true)]
    public async Task Gen_LateErrorDuringDrain_PreservesRecordedFailure(string method, string adapter, bool bodyDies, bool brokenError)
    {
        // Given
        object error = brokenError ? new BrokenError() : "late error";
        var completion = new TaskCompletionSource<Result<int, object>>();
        var bodyFailure = new IOException("body failure");
        Task<int>? abandoned = null;
        CancellationTokenRegistration registration = default;
        ExitCase? releaseExit = null;
        var operation = Effect<EmptyEnvironment>.FromResultAsync<int, object>(_ => new(completion.Task));
        var resource = Resource.Make(operation, (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
        var effect = Scope<object, EmptyEnvironment>.Gen<int>(scope =>
        {
            scope.Defer(exit => Effect<EmptyEnvironment>.Sync(() =>
            {
                releaseExit = exit;
                registration.Dispose();
                return Unit.Value;
            }));
#pragma warning disable MEFF310
            var step = method == "Acquire" ? scope.Acquire(resource) : scope.Run(operation);
#pragma warning restore MEFF310
            abandoned = adapter switch
            {
                "AsTask" => step.AsTask(),
                "Preserve" => step.Preserve().AsTask(),
                _ => null,
            };
            registration = scope.CancellationToken.Register(() =>
            {
                completion.SetResult(new Err<object>(error));
                if (abandoned is null) return;
#pragma warning disable MEFF306
                try { abandoned.GetAwaiter().GetResult(); }
                catch (ScopeFailure) { }
#pragma warning restore MEFF306
            });
            if (bodyDies) throw bodyFailure;
            return new(42);
        });

        // When
        var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        if (adapter != "None") Assert.True(abandoned!.IsFaulted);
        if (bodyDies)
        {
            var die = Assert.IsType<Die>(exit.Value);
            Assert.Same(bodyFailure, die.Exception);
            Assert.Same(bodyFailure, Assert.IsType<Die>(releaseExit!.Value.Value).Exception);
            var suppressed = Assert.Single(die.Suppressed);
            Assert.Equal(Relation.Sequential, suppressed.Relation);
            if (brokenError)
            {
                var getterFailure = Assert.IsType<Die>(suppressed.Case.Value);
                Assert.Equal("error getter", getterFailure.Exception.Message);
                Assert.Same(error, Assert.IsType<Failed>(Assert.Single(getterFailure.Suppressed).Case.Value).Error);
            }
            else
            {
                Assert.Same(error, Assert.IsType<Failed>(suppressed.Case.Value).Error);
            }
        }
        else if (brokenError)
        {
            var die = Assert.IsType<Die>(exit.Value);
            Assert.Equal("error getter", die.Exception.Message);
            Assert.Equal("error getter", Assert.IsType<Die>(releaseExit!.Value.Value).Exception.Message);
            Assert.Same(error, Assert.IsType<Failed>(Assert.Single(die.Suppressed).Case.Value).Error);
        }
        else
        {
            Assert.Same(error, Assert.IsType<Err<object>>(exit.Value).Error);
            Assert.Same(error, Assert.IsType<Failed>(releaseExit!.Value.Value).Error);
        }
    }
}
