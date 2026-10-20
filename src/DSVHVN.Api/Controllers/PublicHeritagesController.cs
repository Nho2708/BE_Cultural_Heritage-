using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Discovery;
using DSVHVN.Application.Guests;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Models;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khám phá di sản công khai của web học tập: trang chủ, tìm kiếm và lọc, bản đồ, chi tiết, timeline. Khách và học sinh xem được,
/// không cần đăng nhập; chỉ thấy di sản đã xuất bản. Giới hạn 300 yêu cầu mỗi phút mỗi IP (vượt thì 429).
/// Nội dung theo ngôn ngữ: mọi endpoint về di sản nhận <c>lang</c> (vi, en, ko, zh-Hans, ja) hoặc tiêu đề <c>Accept-Language</c>; thiếu hoặc sai mã thì tiếng Việt. Tiếng Anh chỉ hiện bản
/// đã duyệt; tiếng Hàn, Trung, Nhật hiện bản dịch máy (cờ <c>isMachineTranslated</c>); chưa có bản hiện hành thì lùi về tiếng Việt (cờ <c>isFallback</c>). Học sinh đã đăng nhập luôn nhận tiếng Việt.
/// </summary>
[Route("api/v1/public")]
[AllowAnonymous]
[EnableRateLimiting(PublicRateLimit.PolicyName)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status429TooManyRequests)]
public sealed class PublicHeritagesController(
    DiscoveryService discovery, PublicSummaryService summaries, PublicModelService models, GuestService guests) : ApiControllerBase
{
    private const int StaticCatalogSeconds = 3600;

    /// <summary>
    /// Ghi lịch sử của khách khi yêu cầu mang <c>X-Device-Key</c> (và <c>X-Client-Platform</c>); yêu cầu của học sinh đã đăng nhập, hoặc không có mã, thì không ghi.
    /// Mã hoặc nền tảng sai định dạng thì 400 ngay từ đầu, trước khi làm việc chính. Việc ghi chạy sau khi việc chính thành công và không bao giờ làm hỏng nó.
    /// </summary>
    private (string Key, ClientPlatform? Platform)? GuestTracking()
    {
        if (IsStudentSession) return null;
        var key = OptionalDeviceKey();
        return key is null ? null : (key, ClientPlatformHeader());
    }

    private Task TrackAsync((string Key, ClientPlatform? Platform)? tracking, LanguageCode language, GuestActivityType type, string? slug, string? query, CancellationToken ct) =>
        tracking is { } t ? guests.TryRecordAsync(t.Key, t.Platform, language, type, slug, query, ct) : Task.CompletedTask;

    /// <summary>Trang chủ: tối đa 8 di sản nổi bật (UNESCO hoặc quốc gia đặc biệt, mới cập nhật trước), tổng số, số theo loại và theo vùng.</summary>
    [HttpGet("home")]
    [ProducesResponseType<ApiResponse<HomeDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<HomeDto>>> Home([FromQuery] string? lang, CancellationToken ct)
        => OkEnvelope(await discovery.GetHomeAsync(PublicLanguage(lang), ct));

    /// <summary>Năm ngôn ngữ của bộ chọn: mã (vi, en, ko, zh-Hans, ja), tên viết bằng chính ngôn ngữ đó, mã giọng đọc của thiết bị. Danh mục cố định nên trình duyệt được lưu tạm 1 giờ.</summary>
    [HttpGet("languages")]
    [ResponseCache(Duration = StaticCatalogSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType<ApiResponse<IReadOnlyList<LanguageOptionDto>>>(StatusCodes.Status200OK)]
    public ActionResult<ApiResponse<IReadOnlyList<LanguageOptionDto>>> LanguageOptions()
        => OkEnvelope(PublicSummaryService.ListLanguages());

    /// <summary>34 tỉnh/thành theo mã, kèm vùng miền, cho khách dựng ô lọc. Danh mục cố định nên trình duyệt được lưu tạm 1 giờ.</summary>
    [HttpGet("provinces")]
    [ResponseCache(Duration = StaticCatalogSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType<ApiResponse<IReadOnlyList<ProvinceDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ProvinceDto>>>> Provinces(CancellationToken ct)
        => OkEnvelope(await discovery.ListProvincesAsync(ct));

    /// <summary>Danh mục cho mọi ô lọc: tỉnh, vùng, loại, cấp xếp hạng, thời kỳ (khoảng năm cố định). Trình duyệt được lưu tạm 1 giờ.</summary>
    [HttpGet("filters")]
    [ResponseCache(Duration = StaticCatalogSeconds, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType<ApiResponse<FilterCatalogDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<FilterCatalogDto>>> Filters(CancellationToken ct)
        => OkEnvelope(await discovery.GetFilterCatalogAsync(ct));

    /// <summary>
    /// Tìm và lọc di sản đã xuất bản, phân trang (<c>pageSize</c> mặc định 20, tối đa 100). <c>keyword</c> 2-100 ký tự, không phân biệt
    /// hoa thường và dấu ("thang long" tìm ra "Thăng Long"), tìm trên tên, mô tả ngắn, địa chỉ, nội dung. <c>region</c> là Bắc, Trung hoặc Nam.
    /// <c>period</c>: PREHISTORIC (đến 180 TCN), NORTHERN_RULE (179 TCN-938), INDEPENDENT_FEUDAL (939-1857), MODERN (1858-1944),
    /// CONTEMPORARY (1945 đến nay). Các điều kiện kết hợp theo VÀ. Khách gửi kèm <c>X-Device-Key</c> và có từ khóa thì từ khóa được ghi vào lịch sử của thiết bị.
    /// </summary>
    [HttpGet("heritages")]
    [ProducesResponseType<ApiResponse<PagedResult<HeritageCardDto>>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<PagedResult<HeritageCardDto>>>> Search(
        [FromQuery] string? keyword, [FromQuery] int? provinceId, [FromQuery] string? region,
        [FromQuery] HeritageType? heritageType, [FromQuery] RecognitionLevel? recognitionLevel, [FromQuery] HeritagePeriod? period,
        [FromQuery] int? page, [FromQuery] int? pageSize, [FromQuery] string? lang, CancellationToken ct)
    {
        var tracking = GuestTracking();
        var language = PublicLanguage(lang);
        var result = await discovery.SearchAsync(
            new HeritageFilter(keyword, provinceId, region, heritageType, recognitionLevel, period), language, page, pageSize, ct);
        // Chỉ ghi từ khóa của trang đầu: chuyển trang không phải là một lượt tìm mới.
        if (!string.IsNullOrWhiteSpace(keyword) && result.Page == 1)
            await TrackAsync(tracking, language, GuestActivityType.SEARCH, null, keyword, ct);
        return OkEnvelope(result);
    }

    /// <summary>
    /// Ghim bản đồ theo cùng bộ lọc với tìm kiếm (không phân trang): chỉ di sản có tọa độ, tối đa 500 ghim, kèm số di sản khớp
    /// và số di sản khớp nhưng không có vị trí trên bản đồ.
    /// </summary>
    [HttpGet("heritages/map")]
    [ProducesResponseType<ApiResponse<HeritageMapDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<HeritageMapDto>>> Map(
        [FromQuery] string? keyword, [FromQuery] int? provinceId, [FromQuery] string? region,
        [FromQuery] HeritageType? heritageType, [FromQuery] RecognitionLevel? recognitionLevel, [FromQuery] HeritagePeriod? period,
        [FromQuery] string? lang, CancellationToken ct)
        => OkEnvelope(await discovery.GetMapAsync(
            new HeritageFilter(keyword, provinceId, region, heritageType, recognitionLevel, period), PublicLanguage(lang), ct));

    /// <summary>Chi tiết di sản theo đường dẫn (<c>slug</c>): nội dung, ảnh kèm ghi công, nguồn, vị trí, timeline rút gọn. Khách gửi kèm <c>X-Device-Key</c> thì lượt xem được ghi vào lịch sử của thiết bị.</summary>
    [HttpGet("heritages/{slug}")]
    [ProducesResponseType<ApiResponse<PublicHeritageDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<PublicHeritageDetailDto>>> Detail(string slug, [FromQuery] string? lang, CancellationToken ct)
    {
        var tracking = GuestTracking();
        var language = PublicLanguage(lang);
        var detail = await discovery.GetDetailAsync(slug, language, ct);
        await TrackAsync(tracking, language, GuestActivityType.VIEW, detail.Slug, null, ct);
        return OkEnvelope(detail);
    }

    /// <summary>
    /// Tóm tắt của di sản theo ngôn ngữ (nút "Tóm tắt" và "Nghe"): <c>detailedText</c> để đọc, <c>shortText</c> để nghe bằng giọng đọc của thiết bị hoặc trình duyệt. Lấy từ bản đã lưu, không gọi AI.
    /// Ngôn ngữ chưa có bản hiện hành thì trả tóm tắt tiếng Việt kèm cờ <c>isFallback</c> (nghe bằng giọng tiếng Việt). <c>narration</c> là thuyết minh tạo sẵn (MP3 giọng tự host:
    /// <c>audioUrl</c>, <c>durationMs</c>, <c>voice</c>, <c>sentences[]</c> với mốc mili giây) của đúng bản tóm tắt đang trả, null khi chưa có hoặc chữ đã đổi. Di sản chưa có tóm tắt tiếng Việt đã duyệt thì 404 kèm mã
    /// SUMMARY_UNAVAILABLE ("Di sản này chưa có tóm tắt"): không hiện cổng quảng cáo, không tính lượt. Khách gửi kèm <c>X-Device-Key</c> thì lượt đọc tóm tắt được ghi vào lịch sử (chỉ khi có tóm tắt).
    /// <para>
    /// <b>Cổng quảng cáo:</b> khi cổng đang hoạt động, khách chưa mở khóa di sản này trên thiết bị của mình (xem quảng cáo có thưởng của Google, hiệu lực 24 giờ) nhận 403 kèm mã
    /// <c>AD_UNLOCK_REQUIRED</c>; thiếu <c>X-Device-Key</c> cũng coi là chưa mở khóa. Học sinh đã đăng nhập luôn mở thẳng; thiết bị khai báo <c>X-Ads-Unavailable: true</c> mở thẳng khi môi trường cho phép.
    /// Phản hồi không được lưu đệm vì phụ thuộc thiết bị.
    /// </para>
    /// </summary>
    [HttpGet("heritages/{slug}/summary")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<PublicSummaryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<PublicSummaryDto>>> Summary(string slug, [FromQuery] string? lang, CancellationToken ct)
    {
        var tracking = GuestTracking();
        var language = PublicLanguage(lang);
        var summary = await summaries.GetAsync(slug, language, Viewer(), ct);
        await TrackAsync(tracking, language, GuestActivityType.READ_SUMMARY, summary.Slug, null, ct);
        return OkEnvelope(summary);
    }

    /// <summary>
    /// Mô hình 3D đang hiển thị của di sản (nút "Xem 3D", trình xem dùng chung): đường dẫn tệp <c>.glb</c>, dung lượng, ảnh xem trước và ghi công (tác giả, giấy phép, nguồn; không dịch).
    /// Xem miễn phí, không qua quảng cáo. Di sản không có mô hình đang hiển thị thì 404 kèm mã MODEL_UNAVAILABLE ("Không tải được mô hình 3D. Bạn có thể xem bộ ảnh của di sản."):
    /// trình xem quay về bộ ảnh. Chi tiết di sản có cờ <c>hasModel</c> để bật nút. Khách gửi kèm <c>X-Device-Key</c> thì lượt xem 3D được ghi vào lịch sử (chỉ khi có mô hình).
    /// </summary>
    [HttpGet("heritages/{slug}/model")]
    [ProducesResponseType<ApiResponse<PublicModelDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<PublicModelDto>>> Model(string slug, CancellationToken ct)
    {
        var tracking = GuestTracking();
        var model = await models.GetAsync(slug, ct);
        await TrackAsync(tracking, PublicLanguage(null), GuestActivityType.VIEW_3D, slug, null, ct);
        return OkEnvelope(model);
    }

    /// <summary>Timeline đầy đủ của một di sản: giai đoạn theo thứ tự, sự kiện theo năm-tháng-ngày, ảnh của sự kiện.</summary>
    [HttpGet("heritages/{slug}/timeline")]
    [ProducesResponseType<ApiResponse<PublicTimelineDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<PublicTimelineDto>>> Timeline(string slug, [FromQuery] string? lang, CancellationToken ct)
        => OkEnvelope(await discovery.GetTimelineAsync(slug, PublicLanguage(lang), ct));
}
