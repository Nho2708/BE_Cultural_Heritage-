using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Questions;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Ai;

/// <summary>
/// Gọi Dịch vụ AI nội bộ (FastAPI, tiến trình riêng, không truy cập CSDL) ở <c>Ai:BaseUrl</c> khi <c>Ai:Mode = Remote</c>:
/// <c>POST /v1/generate-questions</c>, JSON UTF-8 camelCase, header <c>X-Internal-Key</c> và <c>X-Correlation-Id</c> như soạn nội dung bài học.
/// Việc thử lại và thời gian chờ mỗi lần do <c>QuestionAiService</c> quyết định qua <see cref="CancellationToken"/>. Lỗi mạng, 5xx, 408, 429 và kết quả
/// không đọc được là lỗi thử lại được; 400, 401, 403, 422 thì không.
/// </summary>
public sealed class HttpQuestionAiClient(HttpClient http, IOptions<AiOptions> options) : IQuestionAiClient
{
    public const string Path = "v1/generate-questions";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() },
    };

    public string ModelName => "remote-question-generator";

    public async Task<AiQuestionResult> GenerateQuestionsAsync(AiQuestionRequest request, string correlationId, CancellationToken ct)
    {
        var settings = options.Value;
        using var message = new HttpRequestMessage(HttpMethod.Post, settings.BaseUrl.TrimEnd('/') + "/" + Path)
        {
            Content = JsonContent.Create(request, options: Json),
        };
        message.Headers.TryAddWithoutValidation(HttpLessonAiClient.KeyHeader, settings.InternalKey);
        message.Headers.TryAddWithoutValidation(HttpLessonAiClient.CorrelationHeader, correlationId);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new AiServiceException("Không kết nối được dịch vụ AI.", retryable: true, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var retryable = (int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
                throw new AiServiceException($"Dịch vụ AI trả mã {(int)response.StatusCode}.", retryable);
            }

            try
            {
                var result = await response.Content.ReadFromJsonAsync<AiQuestionResult>(Json, ct);
                return result ?? throw new AiServiceException("Dịch vụ AI trả phản hồi rỗng.", retryable: true);
            }
            catch (JsonException ex)
            {
                throw new AiServiceException("Dịch vụ AI trả kết quả sai lược đồ.", retryable: true, ex);
            }
        }
    }
}
