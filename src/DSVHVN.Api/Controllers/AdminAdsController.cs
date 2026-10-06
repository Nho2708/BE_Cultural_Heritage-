using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Ads;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khu Quản trị hệ thống: thống kê khách và lượt mở khóa bằng quảng cáo Google có thưởng. Chỉ đọc, chỉ ADMIN; giáo viên, quản trị trường, học sinh nhận 403.
/// Không có doanh thu quảng cáo: xem ở bảng điều khiển AdMob và Ad Manager (kèm liên kết trong phản hồi).
/// </summary>
[Route("api/v1/admin/ads")]
[Authorize(Policy = Policies.Admin)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class AdminAdsController(AdStatsService stats) : ApiControllerBase
{
    /// <summary>
    /// Thống kê trong khoảng <c>from</c> đến <c>to</c> (ngày Việt Nam <c>yyyy-MM-dd</c>, mặc định 30 ngày gần nhất, tối đa 366 ngày): số lượt mở khóa theo ngày, nền tảng (Android, iOS, web), mạng quảng cáo
    /// (kèm nguồn báo: máy chủ xác minh hay trình duyệt báo), di sản được mở khóa nhiều nhất; số lượt bị từ chối theo lý do (chữ ký sai, đơn vị quảng cáo lạ, thiếu hoặc sai tham số, thời điểm lệch,
    /// mã giao dịch web sai hoặc hết hạn, vượt giới hạn tần suất; lần gọi lại trùng mã giao dịch không tính); số thiết bị khách hoạt động, ngôn ngữ dùng nhiều, di sản được xem, nghe, xem 3D nhiều.
    /// </summary>
    [HttpGet("stats")]
    [ProducesResponseType<ApiResponse<AdStatsDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<AdStatsDto>>> Stats([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
        => OkEnvelope(await stats.GetAsync(CurrentActor, from, to, ct));
}
