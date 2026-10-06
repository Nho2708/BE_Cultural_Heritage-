using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Recommendations;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Ai;

/// <summary>
/// Gọi Dịch vụ AI nội bộ ở <c>Ai:BaseUrl</c> khi <c>Ai:Mode = Remote</c>: <c>POST /v1/rank-related</c>, JSON UTF-8 camelCase, header <c>X-Internal-Key</c>.
/// Thời gian chờ do <see cref="RelatedRanker"/> đặt (2 giây) qua <see cref="CancellationToken"/>; lỗi hay quá giờ thì máy chủ tự tính phương án dự phòng,
/// nên không thử lại ở đây. Chỉ gửi id học sinh dạng số, cấp học, mức khó mục tiêu, thuộc tính di sản; không có họ tên, mã học sinh, tên lớp.
/// </summary>
public sealed class HttpRecommendationAiClient(HttpClient http, IOptions<AiOptions> options) : IRecommendationAiClient
{
    public const string Path = "v1/rank-related";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record Response(List<RelatedHeritage>? Related);

    public async Task<IReadOnlyList<RelatedHeritage>> RankRelatedAsync(RelatedRequest request, CancellationToken ct)
    {
        var settings = options.Value;
        using var message = new HttpRequestMessage(HttpMethod.Post, settings.BaseUrl.TrimEnd('/') + "/" + Path)
        {
            Content = JsonContent.Create(request, options: Json),
        };
        message.Headers.TryAddWithoutValidation(HttpLessonAiClient.KeyHeader, settings.InternalKey);

        using var response = await http.SendAsync(message, ct);
        if (!response.IsSuccessStatusCode) throw new AiServiceException($"Dịch vụ AI trả mã {(int)response.StatusCode}.", retryable: false);
        var body = await response.Content.ReadFromJsonAsync<Response>(Json, ct);
        return body?.Related ?? throw new AiServiceException("Dịch vụ AI trả phản hồi rỗng.", retryable: false);
    }
}
