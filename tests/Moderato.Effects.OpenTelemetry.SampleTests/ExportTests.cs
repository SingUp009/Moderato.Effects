using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

public sealed class ExportTests
{
    [Fact]
    public async Task Sha256_RealRequest_ExportsEffectTraceAndRuntimeMetricsThroughOtlp()
    {
        // Given
        ConcurrentQueue<(string Path, string ContentType, byte[] Body)> received = new();
        await using var factory = CreateFactory(received);
        using var client = factory.CreateClient();

        // When
        using var response = await client.PostAsync("/sha256", new StringContent("hello"), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var digest = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(TestContext.Current.CancellationToken);
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", digest!["sha256"]);
        Assert.True(factory.Services.GetRequiredService<TracerProvider>().ForceFlush());
        Assert.True(factory.Services.GetRequiredService<MeterProvider>().ForceFlush());
        var traces = received.Where(item => Encoding.UTF8.GetString(item.Body).Contains("Effect run", StringComparison.Ordinal)).ToArray();
        var metrics = received.Where(item => Encoding.UTF8.GetString(item.Body).Contains("moderato.effects.completed", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(traces);
        Assert.NotEmpty(metrics);
        Assert.All(traces.Concat(metrics), item => Assert.Equal("application/x-protobuf", item.ContentType));
        var traceText = string.Concat(traces.Select(item => Encoding.UTF8.GetString(item.Body)));
        Assert.Contains("Moderato.Effects", traceText);
        Assert.Contains("Effect run", traceText);
        Assert.Contains("Effect scope", traceText);
        Assert.Contains("Effect acquire", traceText);
        Assert.Contains("Effect resource", traceText);
        Assert.Contains("Effect release", traceText);
        var metricText = string.Concat(metrics.Select(item => Encoding.UTF8.GetString(item.Body)));
        Assert.Contains("moderato.effects.completed", metricText);
        Assert.Contains("moderato.effects.duration", metricText);
        Assert.Contains("dotnet.gc.heap.total_allocated", metricText);
        Assert.DoesNotContain("hello", traceText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sha256_OversizedBody_ReturnsPayloadTooLarge(bool chunked)
    {
        // Given
        await using var factory = CreateFactory(new());
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

    private static WebApplicationFactory<Program> CreateFactory(ConcurrentQueue<(string Path, string ContentType, byte[] Body)> received)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.ConfigureAll<OtlpExporterOptions>(options =>
            {
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.Endpoint = new Uri("http://collector.invalid");
                options.HttpClientFactory = () => new HttpClient(new Receiver(received));
            })));
        factory.UseKestrel(0);
        return factory;
    }

    private sealed class Receiver(ConcurrentQueue<(string Path, string ContentType, byte[] Body)> received) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            request.Content!.CopyTo(buffer, null, cancellationToken);
            received.Enqueue((request.RequestUri!.AbsolutePath, request.Content.Headers.ContentType!.MediaType!, buffer.ToArray()));
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            received.Enqueue((request.RequestUri!.AbsolutePath, request.Content!.Headers.ContentType!.MediaType!,
                await request.Content.ReadAsByteArrayAsync(cancellationToken)));
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }
    }
}
