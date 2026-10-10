using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Ads;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Địa chỉ cho xác minh phía máy chủ (server-side verification) của Google AdMob: khi người xem app nhận thưởng, AdMob gọi <c>GET</c> về đây (khai địa chỉ này trong bảng điều khiển AdMob,
/// mục xác minh phía máy chủ của đơn vị quảng cáo có thưởng). Không đăng nhập: được bảo vệ bằng chữ ký ECDSA của Google. Đây là căn cứ DUY NHẤT để mở khóa trên app.
/// <para>
/// App gắn mã thiết bị làm <c>user_id</c> và mã di sản làm <c>custom_data</c> khi tải quảng cáo. Hợp lệ (kể cả lần gọi lại trùng <c>transaction_id</c>) thì 200 để Google không gọi lại.
/// Thiếu hoặc sai chữ ký thì 400. Chữ ký đúng nhưng đơn vị quảng cáo lạ, thiếu hoặc sai tham số, thời điểm lệch quá 1 giờ thì vẫn 200 với <c>recorded</c> = false và lý do
/// (lời gọi thử của nút kiểm địa chỉ trong bảng điều khiển AdMob thuộc loại này); cả hai trường hợp đều không mở khóa và ghi nhật ký thao tác. Giới hạn như API công khai.
/// </para>
/// </summary>
[Route("api/v1/ads/admob/ssv")]
[AllowAnonymous]
[EnableRateLimiting(PublicRateLimit.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdMobVerificationController(AdRewardService rewards) : ApiControllerBase
{
    /// <summary>
    /// Nhận lời gọi xác minh của AdMob: <c>ad_network</c>, <c>ad_unit</c>, <c>custom_data</c>, <c>reward_amount</c>, <c>reward_item</c>, <c>timestamp</c>, <c>transaction_id</c>, <c>user_id</c>,
    /// <c>signature</c>, <c>key_id</c>. Chữ ký kiểm trên chuỗi truy vấn thô đứng trước <c>&amp;signature=</c> bằng khóa công khai của Google (<c>key_id</c>).
    /// </summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<AdRewardResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<AdRewardResultDto>>> Verify(CancellationToken ct)
    {
        var query = Request.Query.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
        return OkEnvelope(await rewards.HandleAdMobAsync(Request.QueryString.Value, query, ct));
    }
}
