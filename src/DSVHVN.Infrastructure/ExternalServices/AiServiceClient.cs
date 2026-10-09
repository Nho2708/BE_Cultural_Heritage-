using System.Text;
using System.Text.Json;
using DSVHVN.Application.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DSVHVN.Infrastructure.ExternalServices;

/// <summary>
/// Client kết nối sang AI Service (Python FastAPI) xử lý tóm tắt đa cấp độ và sinh câu hỏi trắc nghiệm.
/// </summary>
public class AiServiceClient(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<AiServiceClient> logger) : IAiServiceClient
{
    public async Task<string> ProcessHeritageAsync(
        string text,
        int level,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = configuration["AiService:BaseUrl"]?.TrimEnd('/') ?? "http://127.0.0.1:8000";
        var endpoint = $"{baseUrl}/api/heritage/process";

        var payload = new
        {
            text,
            level,
            target_language = targetLanguage
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            var response = await httpClient.PostAsync(endpoint, content, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Lỗi khi gọi AI Service tại {Endpoint}", endpoint);
            throw;
        }
    }
}
