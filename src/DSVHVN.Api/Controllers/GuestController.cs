using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Guests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khách tham quan trên web học tập và app (không đăng nhập). Khách nhận diện bằng <b>mã thiết bị ẩn danh</b> ở tiêu đề <c>X-Device-Key</c> (UUID phiên bản 4 do app hoặc trình duyệt sinh;
/// sai định dạng thì 400). Máy chủ chỉ giữ mã, nền tảng, ngôn ngữ, cờ "Gần bạn" và lịch sử; không ghi IP, không ghi vị trí, không ghép với tài khoản học sinh. Học sinh đã đăng nhập không dùng
/// các chức năng này (403 <c>GUEST_ONLY</c>) và không bị ghi lịch sử. Phản hồi không được lưu đệm (dữ liệu riêng của thiết bị). Giới hạn như API công khai (429).
/// <para>
/// Lịch sử tự ghi khi khách gọi các API công khai kèm <c>X-Device-Key</c> (tìm kiếm có từ khóa, chi tiết di sản, tóm tắt, mô hình 3D); riêng "nghe thuyết minh" chạy ở thiết bị nên app gọi
/// <c>POST activities</c>. Mọi lượt ghi đều bỏ lượt lặp trong 10 phút.
/// </para>
/// </summary>
[Route("api/v1/public/guest")]
[AllowAnonymous]
[EnableRateLimiting(PublicRateLimit.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status429TooManyRequests)]
public sealed class GuestController(GuestService guests, GuestRecommendationService recommendations) : ApiControllerBase
{
    /// <summary>
    /// Đăng ký thiết bị. Có <c>X-Device-Key</c> thì tạo bản ghi nếu chưa có (201), đã có thì cập nhật cài đặt (200); không có tiêu đề (client không tự sinh được mã an toàn) thì máy chủ cấp một mã mới
    /// và trả trong <c>deviceKey</c> để client giữ lại (201). Thân yêu cầu có thể bỏ trống hoặc <c>{}</c>.
    /// </summary>
    [HttpPost("devices")]
    [ProducesResponseType<ApiResponse<GuestDeviceDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<GuestDeviceDto>>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ApiResponse<GuestDeviceDto>>> Register([FromBody] GuestDeviceRequest? request, CancellationToken ct)
    {
        RequireGuest();
        var (device, created) = await guests.RegisterAsync(
            Request.Headers[GuestHeaders.DeviceKey].ToString(), request ?? new GuestDeviceRequest(null, null, null), ClientPlatformHeader(), ct);
        return created ? CreatedEnvelope(device) : OkEnvelope(device);
    }

    /// <summary>Cài đặt của thiết bị (ngôn ngữ, nền tảng, cờ "Gần bạn"). Mã chưa từng thấy thì trả mặc định với <c>isRegistered</c> false và không tạo bản ghi.</summary>
    [HttpGet("device")]
    [ProducesResponseType<ApiResponse<GuestDeviceDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<GuestDeviceDto>>> Device([FromQuery] string? lang, CancellationToken ct)
    {
        RequireGuest();
        return OkEnvelope(await guests.GetDeviceAsync(RequiredDeviceKey(), PublicLanguage(lang), ct));
    }

    /// <summary>
    /// Đổi cài đặt của thiết bị: ngôn ngữ (vi, en, ko, zh-Hans, ja), bật hoặc tắt "Gần bạn" (<c>locationAllowed</c>, chỉ là cờ nhớ lựa chọn; vị trí không lưu). Trường không gửi thì giữ nguyên.
    /// Thiết bị chưa có bản ghi thì tạo.
    /// </summary>
    [HttpPut("device")]
    [ProducesResponseType<ApiResponse<GuestDeviceDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<GuestDeviceDto>>> UpdateDevice([FromBody] GuestDeviceRequest request, CancellationToken ct)
    {
        RequireGuest();
        return OkEnvelope(await guests.UpdateSettingsAsync(RequiredDeviceKey(), request, ClientPlatformHeader(), ct));
    }

    /// <summary>
    /// Ghi một lượt vào lịch sử (dùng cho "nghe thuyết minh" và cho client muốn tự ghi): <c>type</c> là SEARCH (kèm <c>query</c> 2-100 ký tự), VIEW, READ_SUMMARY, LISTEN hoặc VIEW_3D (kèm <c>slug</c> của di sản đã xuất bản,
    /// không có thì 404). Ngôn ngữ của lượt lấy từ <c>lang</c> hoặc <c>Accept-Language</c>. <c>recorded</c> false khi lượt lặp trong 10 phút (<c>DUPLICATE</c>) hoặc yêu cầu của học sinh đã đăng nhập (<c>STUDENT_SESSION</c>,
    /// không ghi và không báo lỗi để app học sinh gọi nhầm cũng không hỏng).
    /// </summary>
    [HttpPost("activities")]
    [ProducesResponseType<ApiResponse<GuestActivityResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<GuestActivityResultDto>>> Record(
        [FromBody] RecordGuestActivityRequest request, [FromQuery] string? lang, CancellationToken ct)
    {
        var key = RequiredDeviceKey();
        var platform = ClientPlatformHeader();
        if (request.Type is not { } type) throw AppException.Validation("type", Messages.Required("loại lượt"));
        var language = PublicLanguage(lang);
        if (IsStudentSession) return OkEnvelope(new GuestActivityResultDto(false, "STUDENT_SESSION"));
        var recorded = await guests.RecordAsync(key, platform, language, type, request.Slug, request.Query, ct);
        return OkEnvelope(new GuestActivityResultDto(recorded, recorded ? null : "DUPLICATE"));
    }

    /// <summary>
    /// Lịch sử của thiết bị, mới nhất trước, trong 90 ngày (<c>pageSize</c> mặc định 20, tối đa 100): từ khóa đã tìm và di sản đã xem, đọc tóm tắt, nghe, xem 3D (thẻ di sản theo ngôn ngữ yêu cầu).
    /// Chỉ trả cho yêu cầu mang đúng mã; thiết bị chưa có bản ghi thì rỗng.
    /// </summary>
    [HttpGet("history")]
    [ProducesResponseType<ApiResponse<PagedResult<GuestHistoryItemDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedResult<GuestHistoryItemDto>>>> History(
        [FromQuery] int? page, [FromQuery] int? pageSize, [FromQuery] string? lang, CancellationToken ct)
    {
        RequireGuest();
        return OkEnvelope(await guests.ListHistoryAsync(RequiredDeviceKey(), PublicLanguage(lang), page, pageSize, ct));
    }

    /// <summary>Xóa thật một mục của lịch sử (xóa trên máy chủ, không xóa mềm). Mục không có hoặc của thiết bị khác thì 404.</summary>
    [HttpDelete("history/{id:long}")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<object>>> DeleteHistoryItem(long id, CancellationToken ct)
    {
        RequireGuest();
        await guests.DeleteOneAsync(RequiredDeviceKey(), id, ct);
        return OkEnvelope<object>(null, Messages.Success("Xóa mục lịch sử").Text);
    }

    /// <summary>Xóa thật toàn bộ lịch sử của thiết bị; gợi ý quay về mặc định. Không xóa cài đặt, không xóa lượt mở khóa quảng cáo còn hạn. Trả số mục đã xóa.</summary>
    [HttpDelete("history")]
    [ProducesResponseType<ApiResponse<GuestDeletedDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<GuestDeletedDto>>> DeleteHistory(CancellationToken ct)
    {
        RequireGuest();
        var deleted = await guests.DeleteAllAsync(RequiredDeviceKey(), ct);
        return OkEnvelope(new GuestDeletedDto(deleted), Messages.Success("Xóa lịch sử").Text);
    }

    /// <summary>
    /// "Gợi ý cho bạn": tối đa 6 di sản đã xuất bản giống các di sản khách đã xem, đọc tóm tắt, nghe, xem 3D (cùng loại, cùng tỉnh hoặc vùng, cùng thời kỳ), bỏ di sản đã xem từ 3 lần.
    /// Mỗi di sản kèm các điểm chung (<c>reasons</c>) và di sản giống nhất (<c>becauseOfName</c>) để giao diện tự ghép câu theo ngôn ngữ. Không gửi <c>X-Device-Key</c>, chưa có lịch sử hoặc vừa xóa hết thì
    /// trả di sản nổi bật như trang chủ (<c>isPersonalized</c> false). Không gọi mô hình ngôn ngữ, không tính lượt AI, không lưu kết quả.
    /// </summary>
    [HttpGet("recommendations")]
    [ProducesResponseType<ApiResponse<GuestRecommendationsDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<GuestRecommendationsDto>>> Recommendations([FromQuery] string? lang, CancellationToken ct)
    {
        RequireGuest();
        return OkEnvelope(await recommendations.RecommendAsync(OptionalDeviceKey(), PublicLanguage(lang), ct));
    }

    /// <summary>
    /// "Gần bạn": tối đa 6 di sản đã xuất bản có tọa độ, gần vị trí của khách nhất, kèm khoảng cách (<c>distanceMeters</c>, <c>distanceText</c>: dưới 1000 m hiện mét, từ 1000 m hiện km làm tròn 1 chữ số).
    /// Vị trí gửi trong <b>thân yêu cầu</b> (không để trên đường dẫn, để không lọt vào nhật ký yêu cầu của máy chủ): <c>latitude</c>, <c>longitude</c> làm tròn 3 chữ số thập phân. Máy chủ chỉ tính khoảng cách
    /// trong bộ nhớ: không lưu, không ghi vào lịch sử, không gửi sang Dịch vụ AI. Vị trí ngoài khung tọa độ Việt Nam (nới 1 độ mỗi phía) thì danh sách rỗng, <c>unavailableReason</c> là <c>OUTSIDE_VIETNAM</c>.
    /// Không cần <c>X-Device-Key</c> (cờ cho phép "Gần bạn" của thiết bị nằm ở <c>PUT device</c>).
    /// </summary>
    [HttpPost("nearby")]
    [ProducesResponseType<ApiResponse<NearbyDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<NearbyDto>>> Nearby([FromBody] NearbyRequest request, [FromQuery] string? lang, CancellationToken ct)
    {
        RequireGuest();
        return OkEnvelope(await recommendations.NearbyAsync(request, PublicLanguage(lang), ct));
    }
}
