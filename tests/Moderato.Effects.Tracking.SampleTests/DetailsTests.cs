using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moderato.Effects;
using Moderato.Effects.Tracking.Samples.Components.Execution;

[Collection("Tracking sample")]
public sealed class DetailsTests
{
    [Theory]
    [InlineData("http://localhost:18888", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(null, false)]
    public async Task EntryDetails_TracingContext_RendersCorrelationWithSafeLink(string? viewer, bool linkExpected)
    {
        // Given
        using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var recording = EffectTracker.Record();
        await Effect<EmptyEnvironment>.Succeed(1).WithName("Read<Order>").RunAsync(cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = recording.GetSnapshot();
        var entry = snapshot.Entries.First(item => item.Kind == "op");
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        // When
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<EntryDetails>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Snapshot"] = snapshot, ["Entry"] = entry, ["TraceViewerUrl"] = viewer,
            }))).ToHtmlString());

        // Then
        Assert.Contains(activity.TraceId.ToString(), html);
        Assert.Contains(activity.SpanId.ToString(), html);
        Assert.Contains("Read&lt;Order&gt;", html);
        Assert.Equal(linkExpected, html.Contains("Open trace", StringComparison.Ordinal));
        Assert.DoesNotContain("javascript:", html);
        if (linkExpected)
        {
            Assert.Contains($"/traces/detail/{activity.TraceId}?spanId={activity.SpanId}", html);
            Assert.Contains("rel=\"noopener noreferrer\"", html);
        }
    }

    [Fact]
    public async Task ExecutionTree_EvictedParent_LabelsUnavailableParent()
    {
        // Given
        using var recording = EffectTracker.Record(3);
        await Effect<EmptyEnvironment>.Succeed(1).RunAsync(cancellationToken: TestContext.Current.CancellationToken);
        await Effect<EmptyEnvironment>.Succeed(2).RunAsync(cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = recording.GetSnapshot();
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        // When
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<ExecutionTree>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Entries"] = snapshot.Entries }))).ToHtmlString());

        // Then
        Assert.False(snapshot.IsComplete);
        Assert.Contains("unavailable", html);
    }
}
