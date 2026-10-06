using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Ads;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Quảng cáo Google có thưởng cho khách tham quan (không đăng nhập): khách xem hết một quảng cáo ngắn để mở <b>tóm tắt và nghe thuyết minh của một di sản</b> trong 24 giờ trên đúng thiết bị đó;
/// mọi nội dung khác miễn phí. Khách nhận diện bằng mã thiết bị ẩn danh ở <c>X-Device-Key</c>. Học sinh đã đăng nhập không thấy quảng cáo: các API ghi nhận (<c>web/start</c>,
/// <c>web/rewarded</c>) trả 403 <c>GUEST_ONLY</c>, còn <c>unlock</c> trả "mở thẳng". Phản hồi không được lưu đệm. Giới hạn như API công khai (429).
/// <para>
/// App dùng AdMob: lời gọi xác minh phía máy chủ của Google ở <c>/api/v1/ads/admob/ssv</c> mới mở khóa; app chỉ hỏi <c>unlock</c> (mỗi 2 giây, tối đa 20 giây) rồi mở nếu có.
/// Web dùng Ad Manager: <c>web/start</c> cấp mã giao dịch một lần, <c>web/rewarded</c> báo sự kiện "đã cấp thưởng".
/// </para>
/// </summary>
[Route("api/v1/public/ads")]
[AllowAnonymous]
[EnableRateLimiting(PublicRateLimit.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status429TooManyRequests)]
public sealed class AdsController(AdUnlockService unlocks, AdRewardService rewards) : ApiControllerBase
{
    /// <summary>
    /// Cấu hình cho giao diện: cổng đang hoạt động không (<c>gateActive</c>), thời hạn mở khóa (24 giờ), mức xếp hạng nội dung tối đa của yêu cầu quảng cáo (<c>G</c>, phù hợp mọi lứa tuổi),
    /// đơn vị quảng cáo của Android, iOS và web, và việc máy chủ có chấp nhận khai báo "thiết bị không có quảng cáo" không. Mã đơn vị đến từ cấu hình môi trường
    /// (đơn vị thử của Google khi phát triển). Không có thông tin riêng của thiết bị.
    /// </summary>
    [HttpGet("config")]
    [ProducesResponseType<ApiResponse<AdConfigDto>>(StatusCodes.Status200OK)]
    public ActionResult<ApiResponse<AdConfigDto>> Config() => OkEnvelope(unlocks.GetConfig());

    /// <summary>
    /// Trạng thái mở khóa của di sản (<c>slug</c>) trên thiết bị của yêu cầu: <c>gateRequired</c>, <c>unlocked</c> và hạn <c>expiresAt</c> (giờ máy chủ). App hỏi lại mỗi 2 giây (tối đa 20 giây)
    /// sau khi xem hết quảng cáo, chờ máy chủ nhận xác minh của Google. Học sinh đã đăng nhập, cổng tắt hoặc thiết bị không có quảng cáo thì <c>gateRequired</c> false kèm lý do ở <c>openReason</c>.
    /// Di sản không có hoặc chưa xuất bản thì 404.
    /// </summary>
    [HttpGet("unlock")]
    [ProducesResponseType<ApiResponse<AdUnlockStatusDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AdUnlockStatusDto>>> Unlock([FromQuery] string? slug, CancellationToken ct)
        => OkEnvelope(await unlocks.GetStatusAsync(Viewer(), slug, ct));

    /// <summary>
    /// Web: khách bắt đầu xem quảng cáo. Máy chủ cấp mã giao dịch dùng một lần (<c>transactionId</c>, hết hạn sau 10 phút, gắn thiết bị và di sản) kèm đơn vị quảng cáo cần tải (<c>adUnitPath</c>).
    /// Di sản đang còn mở khóa thì <c>alreadyUnlocked</c> true và không cấp mã (không mời xem thêm). Cần <c>X-Device-Key</c>. Môi trường chưa cấu hình đơn vị quảng cáo cho web thì 422 <c>AD_UNAVAILABLE</c>.
    /// </summary>
    [HttpPost("web/start")]
    [ProducesResponseType<ApiResponse<WebRewardStartDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<WebRewardStartDto>>> StartWeb([FromBody] StartWebRewardRequest request, CancellationToken ct)
    {
        RequireGuest();
        RequiredDeviceKey();
        return OkEnvelope(await rewards.StartWebAsync(Viewer(), request.Slug, ct));
    }

    /// <summary>
    /// Web: trình duyệt báo đã nhận sự kiện "đã cấp thưởng" của Google (không phải "sẵn sàng" hay "đã đóng"); gửi lại <c>transactionId</c> đã được cấp, <c>slug</c> của di sản và <c>adUnit</c> đã phát.
    /// Hợp lệ thì ghi lượt mở khóa nguồn "trình duyệt báo" (24 giờ) và trả <c>recorded</c> true; gửi lại cùng mã giao dịch hoặc di sản đang còn mở khóa thì vẫn 200 với <c>recorded</c> false
    /// (<c>reason</c> là <c>DUPLICATE</c> hoặc <c>ALREADY_UNLOCKED</c>), không ghi thêm và không kéo dài hạn. Mã sai chữ ký, sai thiết bị hoặc di sản, hết hạn, đơn vị quảng cáo lạ thì 400
    /// <c>AD_REWARD_REJECTED</c>. Vượt giới hạn 10 lượt mỗi giờ mỗi thiết bị hoặc 60 lượt mỗi giờ mỗi IP thì 429 <c>AD_REWARD_RATE_LIMITED</c> kèm <c>Retry-After</c>.
    /// </summary>
    [HttpPost("web/rewarded")]
    [ProducesResponseType<ApiResponse<AdRewardResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<AdRewardResultDto>>> RewardedWeb([FromBody] WebRewardReportRequest request, CancellationToken ct)
    {
        RequireGuest();
        RequiredDeviceKey();
        return OkEnvelope(await rewards.ReportWebAsync(Viewer(), request, ClientIp, ct));
    }
}
