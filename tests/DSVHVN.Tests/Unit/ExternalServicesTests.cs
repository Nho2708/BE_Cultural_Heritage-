using System.Net;
using System.Text.Json;
using DSVHVN.Infrastructure.ExternalServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DSVHVN.Tests.Unit;

public sealed class ExternalServicesTests
{
    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    [Fact]
    public async Task Geocoding_ReturnsCoordinates_WhenApiReturnsFeature()
    {
        // Giả lập kết quả trả về từ Photon Komoot API cho Hoàng thành Thăng Long
        var jsonResponse = """
        {
            "features": [
                {
                    "geometry": {
                        "coordinates": [105.8402, 21.0364]
                    }
                }
            ]
        }
        """;

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.Contains("photon.komoot.io", req.RequestUri?.Host ?? string.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse)
            };
        });

        var client = new HttpClient(handler);
        var service = new GeocodingService(client, NullLogger<GeocodingService>.Instance);

        var (lat, lon, success) = await service.ResolveAddressAsync("19C Hoàng Diệu, Ba Đình, Hà Nội");

        Assert.True(success);
        Assert.Equal(21.0364m, lat);
        Assert.Equal(105.8402m, lon);
    }

    [Fact]
    public async Task Geocoding_ReturnsFalse_WhenAddressIsEmptyOrNotFound()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"features":[]}""")
        });

        var client = new HttpClient(handler);
        var service = new GeocodingService(client, NullLogger<GeocodingService>.Instance);

        var emptyResult = await service.ResolveAddressAsync("");
        Assert.False(emptyResult.Success);
        Assert.Null(emptyResult.Latitude);

        var notFoundResult = await service.ResolveAddressAsync("Địa chỉ không tồn tại xyz123");
        Assert.False(notFoundResult.Success);
        Assert.Null(notFoundResult.Latitude);
    }

    [Fact]
    public async Task AiServiceClient_SendsCorrectPayloadToFastApi()
    {
        string? capturedBody = null;

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.Equal("http://127.0.0.1:8000/api/heritage/process", req.RequestUri?.ToString());
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"summary": "Tóm tắt mẫu cho cấp 1"}""")
            };
        });

        var client = new HttpClient(handler);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiService:BaseUrl"] = "http://127.0.0.1:8000"
            })
            .Build();

        var service = new AiServiceClient(client, config, NullLogger<AiServiceClient>.Instance);

        var response = await service.ProcessHeritageAsync("Bài viết về Cố đô Huế", 1, "vi");

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody);
        Assert.Equal("Bài viết về Cố đô Huế", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("level").GetInt32());
        Assert.Equal("vi", doc.RootElement.GetProperty("target_language").GetString());
        Assert.Contains("Tóm tắt mẫu", response);
    }
}
