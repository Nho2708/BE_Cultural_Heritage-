using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Ai;

/// <summary>
/// Gọi Dịch vụ AI nội bộ (FastAPI, tiến trình riêng, không truy cập CSDL) ở <c>Ai:BaseUrl</c> khi <c>Ai:Mode = Remote</c>:
/// <c>POST /v1/compose-lesson</c>, JSON UTF-8 camelCase, header <c>X-Internal-Key</c> (khóa nội bộ giữa hai dịch vụ của nhóm, đặt bằng biến môi trường
/// <c>Ai__InternalKey</c>, không ghi vào appsettings) và <c>X-Correlation-Id</c>. Việc thử lại và thời gian chờ mỗi lần do <c>LessonAiService</c>
/// quyết định qua <see cref="CancellationToken"/>. Lỗi mạng, 5xx, 408, 429 và kết quả không đọc được là lỗi thử lại được; 400, 401, 403, 422 thì không.
/// </summary>
public sealed class HttpLessonAiClient(HttpClient http, IOptions<AiOptions> options) : ILessonAiClient
{
    public const string Path = "v1/compose-lesson";
    public const string KeyHeader = "X-Internal-Key";
    public const string CorrelationHeader = "X-Correlation-Id";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() },
    };

    public string ModelName => "remote-lesson-composer";

    public async Task<AiComposeResult> ComposeLessonAsync(AiComposeRequest request, string correlationId, CancellationToken ct)
    {
        var settings = options.Value;
        using var message = new HttpRequestMessage(HttpMethod.Post, Combine(settings.BaseUrl, Path))
        {
            Content = JsonContent.Create(request, options: Json),
        };
        message.Headers.TryAddWithoutValidation(KeyHeader, settings.InternalKey);
        message.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);

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
                var result = await response.Content.ReadFromJsonAsync<AiComposeResult>(Json, ct);
                return result ?? throw new AiServiceException("Dịch vụ AI trả phản hồi rỗng.", retryable: true);
            }
            catch (JsonException ex)
            {
                throw new AiServiceException("Dịch vụ AI trả kết quả sai lược đồ.", retryable: true, ex);
            }
        }
    }

    private static string Combine(string baseUrl, string path) => baseUrl.TrimEnd('/') + "/" + path;
}

/// <summary>
/// Kiểm cấu hình <c>Ai</c> lúc khởi động: chế độ Remote thiếu <c>BaseUrl</c> (địa chỉ tuyệt đối) hoặc <c>InternalKey</c> thì dừng và nêu mục còn thiếu;
/// các giá trị thời gian chờ, số lần thử lại phải hợp lý.
/// </summary>
public sealed class AiOptionsValidator : IValidateOptions<AiOptions>
{
    public ValidateOptionsResult Validate(string? name, AiOptions options)
    {
        var problems = new List<string>();
        if (options.AttemptTimeoutSeconds <= 0) problems.Add("Ai:AttemptTimeoutSeconds phải lớn hơn 0");
        if (options.HeritageAttemptTimeoutSeconds <= 0) problems.Add("Ai:HeritageAttemptTimeoutSeconds phải lớn hơn 0");
        if (options.MaxRetries is < 0 or > 5) problems.Add("Ai:MaxRetries phải từ 0 đến 5");
        if (options.Mode == AiMode.Remote)
        {
            if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                problems.Add("thiếu Ai:BaseUrl (địa chỉ tuyệt đối của Dịch vụ AI, vd http://localhost:8000)");
            if (string.IsNullOrWhiteSpace(options.InternalKey))
                problems.Add("thiếu Ai:InternalKey (đặt bằng biến môi trường Ai__InternalKey hoặc User Secrets)");
        }
        return problems.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Cấu hình Ai chưa đúng: " + string.Join("; ", problems) + ".");
    }
}
