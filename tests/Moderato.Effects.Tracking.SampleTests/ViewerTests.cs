using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moderato.Effects;
using Moderato.Effects.Tracking.Samples.Execution;
using OpenTelemetry.Exporter;

[CollectionDefinition("Tracking sample", DisableParallelization = true)]
public sealed class TrackingCollection;

[Collection("Tracking sample")]
public sealed class ViewerTests
{
    [Fact]
    public async Task Home_NoExecutions_ShowsReadOnlyViewer()
    {
        // Given
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // When
        var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

        // Then
        Assert.Contains("Effect Tracker", html);
        Assert.Contains("Pause updates", html);
        Assert.Contains("No recorded executions", html);
        Assert.DoesNotContain("Run failure", html);
        Assert.DoesNotContain("Run success", html);
        Assert.DoesNotContain("Continue cleanup", html);
    }

    [Fact]
    public async Task Sha256_RealRequest_RecordsCompletedResourcesAndTraceCorrelation()
    {
        // Given
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // When
        using var response = await client.PostAsync("/sha256", new StringContent("hello"), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var digest = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(TestContext.Current.CancellationToken);
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", digest!["sha256"]);
        var snapshot = factory.Services.GetRequiredService<TrackingSession>().GetSnapshot();
        Assert.True(snapshot.IsComplete);
        Assert.Contains(snapshot.Entries, entry => entry.Name == "SHA256.Compute");
        Assert.Contains(snapshot.Entries, entry => entry.Kind == "resource" && entry.State == "Succeeded");
        Assert.Contains(snapshot.Entries, entry => entry.Kind == "release" && entry.State == "Succeeded");
        Assert.All(snapshot.Entries, entry => Assert.NotNull(entry.TraceId));
        Assert.All(snapshot.Entries, entry => Assert.NotNull(entry.SpanId));
        var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);
        Assert.Contains("SHA256.Compute", html);
        Assert.DoesNotContain("No recorded executions", html);
    }

    [Fact]
    public async Task Host_Stopped_ReleasesRecordingWindowBeforeDisposal()
    {
        // Given
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var host = factory.Services.GetRequiredService<IHost>();
        var disposal = factory.Services.GetRequiredService<PausedDisposal>();
        try
        {
            // When
            await host.StopAsync(TestContext.Current.CancellationToken);
            await disposal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Then
            using var next = EffectTracker.Record();
            Assert.Empty(next.GetSnapshot().Entries);
        }
        finally
        {
            disposal.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task Host_WildcardUrlsConfiguration_StillBindsLoopbackOnly()
    {
        // Given
        var sample = Path.GetFullPath("../../../../../samples/Moderato.Effects.Tracking.Samples", AppContext.BaseDirectory);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var start = new ProcessStartInfo(Path.GetFullPath("../../../" + executable, RuntimeEnvironment.GetRuntimeDirectory()))
        {
            WorkingDirectory = sample,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(sample, "bin", configuration, "net11.0/Moderato.Effects.Tracking.Samples.dll"));
        start.ArgumentList.Add("--Tracking:Port");
        start.ArgumentList.Add("0");
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add("http://0.0.0.0:0");
        start.Environment["OTEL_SDK_DISABLED"] = "true";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));

        // When
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            string? address = null;
            while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                if (!line.Contains("Now listening on:", StringComparison.Ordinal)) continue;
                address = line[(line.IndexOf("http://", StringComparison.Ordinal))..];
                break;
            }

            // Then
            Assert.NotNull(address);
            Assert.Equal("127.0.0.1", new Uri(address).Host);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            await errors;
        }
    }

    [Fact]
    public async Task Home_UntrustedHost_ReturnsBadRequest()
    {
        // Given
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "attacker.invalid";

        // When
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/", "GET")]
    [InlineData("/_blazor/negotiate?negotiateVersion=1", "POST")]
    public async Task Viewer_CrossOriginRequest_ReturnsForbidden(string path, string method)
    {
        // Given
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("Origin", "http://attacker.invalid");

        // When
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_SameOriginRequest_AllowsInteractiveConnection()
    {
        // Given
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));

        // When
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("connectionToken", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sha256_OversizedBody_ReturnsPayloadTooLarge(bool chunked)
    {
        // Given
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/sha256")
        {
            Content = new ByteArrayContent(new byte[1024 * 1024 + 1]),
        };
        request.Headers.TransferEncodingChunked = chunked;

        // When
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Tracking:Port"] = "0" }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<PausedDisposal>();
                services.ConfigureAll<OtlpExporterOptions>(options =>
            {
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.Endpoint = new Uri("http://collector.invalid");
                options.HttpClientFactory = () => new HttpClient(new Receiver());
            });
            });
        });
        factory.UseKestrel(0);
        return factory;
    }

    private sealed class PausedDisposal : IAsyncDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask DisposeAsync()
        {
            Entered.TrySetResult();
            await Release.Task;
        }
    }

    private sealed class Receiver : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }
}
