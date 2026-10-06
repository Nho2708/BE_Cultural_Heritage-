using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Discovery;
using DSVHVN.Domain.Enums;
using DSVHVN.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Xem trước bản nháp di sản (trang xem trước của web học tập). Đường dẫn nằm ngoài <c>/api/v1/public</c> để không lẫn vào tìm kiếm, bản đồ, gợi ý hay
/// API công khai. Mở được khi có <b>mã xem trước còn hạn</b> do quản trị hệ thống cấp <b>và</b> người mở đang đăng nhập bằng tài khoản quản trị hệ thống;
/// thiếu một trong hai, mã sai, hết hạn hay di sản không còn đều cùng một 404 (không cho biết di sản có tồn tại hay không). Mọi phản hồi, kể cả 404,
/// mang <c>X-Robots-Tag: noindex</c> và <c>Cache-Control: no-store</c>. Không ghi gì vào CSDL, không ghi lượt xem. Giới hạn như API công khai (429).
/// </summary>
[Route("api/v1/preview/heritages/{code}")]
[AllowAnonymous]
[EnableRateLimiting(PublicRateLimit.PolicyName)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status429TooManyRequests)]
public sealed class PreviewHeritagesController(HeritagePreviewService previews) : ApiControllerBase
{
    /// <summary>Bản nháp đang lưu của di sản, cùng dữ liệu với trang chi tiết công khai, kèm hạn của liên kết và trạng thái hiện tại của di sản.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<HeritagePreviewDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<HeritagePreviewDto>>> Get(string code, [FromQuery] string? lang, CancellationToken ct)
        => OkEnvelope(await previews.GetAsync(AdminActorOrNull(), code, PublicLanguage(lang), ct));

    /// <summary>Timeline đầy đủ của bản nháp (cùng điều kiện mở như trên).</summary>
    [HttpGet("timeline")]
    [ProducesResponseType<ApiResponse<PublicTimelineDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PublicTimelineDto>>> Timeline(string code, [FromQuery] string? lang, CancellationToken ct)
        => OkEnvelope(await previews.GetTimelineAsync(AdminActorOrNull(), code, PublicLanguage(lang), ct));

    // Endpoint cho phép ẩn danh nhưng token (nếu có) vẫn được xác thực và đối chiếu CSDL; chỉ vai trò quản trị hệ thống mới được xét mã.
    private Actor? AdminActorOrNull() =>
        User.Identity?.IsAuthenticated == true && User.FindFirst(JwtClaimNames.Role)?.Value == nameof(RoleCode.ADMIN) ? CurrentActor : null;
}
