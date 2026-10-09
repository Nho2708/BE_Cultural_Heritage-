using System.Text.Json;
using DSVHVN.Application.Common;
using Microsoft.Extensions.Logging;

namespace DSVHVN.Infrastructure.ExternalServices;

/// <summary>
/// Dịch vụ giải mã tọa độ địa lý sử dụng OpenStreetMap / Komoot Photon API.
/// </summary>
public class GeocodingService(HttpClient httpClient, ILogger<GeocodingService> logger) : IGeocodingService
{
    public async Task<(decimal? Latitude, decimal? Longitude, bool Success)> ResolveAddressAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return (null, null, false);
        }

        try
        {
            var url = $"https://photon.komoot.io/api/?q={Uri.EscapeDataString(address)}&limit=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("AICulturalHeritagePlatform/1.0");

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Geocoding API trả về mã HTTP {StatusCode} cho địa chỉ: {Address}", response.StatusCode, address);
                return (null, null, false);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("features", out var features) && features.GetArrayLength() > 0)
            {
                var geometry = features[0].GetProperty("geometry");
                var coords = geometry.GetProperty("coordinates");
                var lon = (decimal)coords[0].GetDouble();
                var lat = (decimal)coords[1].GetDouble();
                return (lat, lon, true);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Lỗi khi giải mã địa chỉ tọa độ: {Address}", address);
        }

        return (null, null, false);
    }
}
