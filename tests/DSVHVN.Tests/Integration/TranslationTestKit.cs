using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.Discovery;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Lessons;
using Microsoft.EntityFrameworkCore;
using static DSVHVN.Tests.Integration.DiscoveryTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Dựng dữ liệu và gọi API cho các test đa ngôn ngữ: di sản đã xuất bản có giai đoạn, sự kiện và chú thích ảnh để có đủ bốn loại phần chữ cần dịch,
/// gọi dịch, duyệt, từ chối, đọc nội dung công khai theo ngôn ngữ.
/// </summary>
public sealed class TranslationTestKit(ApiFactory factory)
{
    public const string AdminRoot = "/api/v1/admin";
    public const string PublicRoot = "/api/v1/public";

    public DiscoveryTestKit Discovery { get; } = new(factory);
    public ApiFactory Factory { get; } = factory;
    public HttpClient Http => Discovery.Http;

    public Task<string> AdminAsync() => Discovery.Api.AdminTokenAsync();

    /// <summary>Di sản đã dựng cho test: chi tiết, giai đoạn, sự kiện, ảnh của di sản và ảnh của sự kiện.</summary>
    public sealed record Seeded(HeritageDetailDto Heritage, TimelineDto Phase, EventDto Event, long HeritageMediaId, long EventMediaId, string Tag);

    /// <summary>Di sản có nội dung tiếng Việt, một giai đoạn có mô tả, một sự kiện có mô tả, ảnh của di sản và ảnh của sự kiện đều có chú thích.</summary>
    public async Task<Seeded> SeedAsync(string adminToken, bool publish = true, string? name = null, string? extra = null)
    {
        var tag = Unique();
        var spec = new Spec(name ?? $"Hoàng thành {tag}", ShortDescription: $"Trung tâm quyền lực {tag}.", Extra: extra);
        var heritage = publish ? await Discovery.PublishedAsync(adminToken, spec) : await Discovery.DraftAsync(adminToken, spec);
        var phase = await Discovery.AddPhaseAsync(adminToken, heritage.Id, $"Thời Lý {tag}", 1010, 1225);
        var item = await Discovery.AddEventAsync(adminToken, phase.Id, $"Dời đô {tag}", 1010, 7, 21);
        var eventMedia = await Expect(Http.PostAsync<MediaDto>($"{AdminRoot}/events/{item.Id}/media", new
        {
            mediaType = "IMAGE", url = $"https://upload.wikimedia.org/{tag}-su-kien.jpg", caption = $"Chiếu dời đô {tag}",
            author = "Nguyễn Văn An", license = "CC BY-SA 4.0", source = "Wikimedia Commons",
        }, adminToken), HttpStatusCode.Created);
        var detail = await Expect(Http.GetAsync<HeritageDetailDto>($"{AdminRoot}/heritages/{heritage.Id}", adminToken));
        return new Seeded(detail, phase, item, detail.Media.Single().Id, eventMedia.Id, tag);
    }

    public Task<TranslationOverviewDto> GenerateAsync(string token, long heritageId) =>
        Expect(Http.PostAsync<TranslationOverviewDto>($"{AdminRoot}/heritages/{heritageId}/translations/generate", null, token));

    public Task<TranslationOverviewDto> OverviewAsync(string token, long heritageId) =>
        Expect(Http.GetAsync<TranslationOverviewDto>($"{AdminRoot}/heritages/{heritageId}/translations", token));

    public Task<ReviewResultDto> ApproveAsync(string token, long heritageId, string language, string kind) =>
        Expect(Http.PostAsync<ReviewResultDto>($"{AdminRoot}/heritages/{heritageId}/translations/{language}/approve", new { kind }, token));

    public Task<ReviewResultDto> RejectAsync(string token, long heritageId, string language, string kind, string? reason = null) =>
        Expect(Http.PostAsync<ReviewResultDto>($"{AdminRoot}/heritages/{heritageId}/translations/{language}/reject", new { kind, reason }, token));

    public static TranslationCellDto Cell(TranslationOverviewDto overview, TranslationKind kind, string language) =>
        (kind == TranslationKind.TRANSLATION ? overview.Translations : overview.Summaries).Single(c => c.Language == language);

    /// <summary>Gọi API công khai với ngôn ngữ qua tham số <c>lang</c> hoặc tiêu đề <c>Accept-Language</c>.</summary>
    public async Task<(HttpResponseMessage Response, Envelope<T> Body)> PublicGetAsync<T>(string path, string? lang = null, string? acceptLanguage = null,
        string? token = null)
    {
        var url = lang is null ? path : path + (path.Contains('?') ? "&" : "?") + "lang=" + Uri.EscapeDataString(lang);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (acceptLanguage is not null) request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        if (token is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await Http.SendAsync(request);
        var envelope = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<Envelope<T>>(response.Content, HttpTestExtensions.Json);
        return (response, envelope!);
    }

    public async Task<T> PublicAsync<T>(string path, string? lang = null, string? acceptLanguage = null, string? token = null)
    {
        var (response, body) = await PublicGetAsync<T>(path, lang, acceptLanguage, token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {body.ErrorCode}: {body.Message}");
        return body.Data!;
    }

    public Task<PublicHeritageDetailDto> DetailAsync(string slug, string? lang = null, string? acceptLanguage = null, string? token = null) =>
        PublicAsync<PublicHeritageDetailDto>($"{PublicRoot}/heritages/{slug}", lang, acceptLanguage, token);

    public Task<PublicSummaryDto> SummaryAsync(string slug, string? lang = null, string? acceptLanguage = null, string? token = null) =>
        PublicAsync<PublicSummaryDto>($"{PublicRoot}/heritages/{slug}/summary", lang, acceptLanguage, token);

    public Task<PublicTimelineDto> TimelineAsync(string slug, string? lang = null) =>
        PublicAsync<PublicTimelineDto>($"{PublicRoot}/heritages/{slug}/timeline", lang);

    public Task<PagedResult<HeritageCardDto>> SearchAsync(string query, string? lang = null) =>
        PublicAsync<PagedResult<HeritageCardDto>>($"{PublicRoot}/heritages?pageSize=100&{query}", lang);

    public static string K(string text) => "keyword=" + Uri.EscapeDataString(text);

    /// <summary>Duyệt mọi thứ của một di sản như quản trị sẽ làm: tóm tắt tiếng Việt, tóm tắt và bản dịch tiếng Anh.</summary>
    public async Task ApproveAllAsync(string token, long heritageId)
    {
        await ApproveAsync(token, heritageId, "vi", "SUMMARY");
        await ApproveAsync(token, heritageId, "en", "BOTH");
    }

    public Task<List<T>> DbAsync<T>(Func<DSVHVN.Infrastructure.Persistence.AppDbContext, Task<List<T>>> query) => Factory.WithDbAsync(query);

    public Task<List<AiGeneration>> GenerationsAsync(long heritageId) =>
        Factory.WithDbAsync(db => db.AiGenerations.AsNoTracking().Where(g => g.HeritageId == heritageId).OrderBy(g => g.Id).ToListAsync());
}
