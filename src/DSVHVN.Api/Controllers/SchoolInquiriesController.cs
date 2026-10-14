using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.SchoolInquiries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Biểu mẫu liên hệ của trang Dành cho nhà trường (web học tập), không cần đăng nhập. Thông tin được gửi qua email tới mọi quản trị hệ thống đang hoạt động
/// và các địa chỉ khai ở <c>SchoolInquiry:Recipients</c>; không lưu CSDL và không gửi thư xác nhận về địa chỉ người nhập.
/// Giới hạn 5 lần mỗi 10 phút mỗi IP (vượt thì 429).
/// </summary>
[Route("api/v1/public/school-inquiries")]
[AllowAnonymous]
[EnableRateLimiting(PublicRateLimit.SchoolInquiryPolicyName)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status429TooManyRequests)]
public sealed class SchoolInquiriesController(SchoolInquiryService inquiries) : ApiControllerBase
{
    /// <summary>
    /// Gửi liên hệ của trường. Bắt buộc: <c>schoolName</c>, <c>contactName</c>, <c>email</c>, <c>message</c> (tối đa 2000 ký tự); tùy chọn: <c>province</c>, <c>position</c>, <c>phone</c>.
    /// <c>website</c> là ô bẫy ẩn, người thật để trống: có giá trị thì vẫn trả 202 nhưng không gửi thư. Chưa gửi được thư (chưa có người nhận hoặc máy chủ thư lỗi) thì 503.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<object>>> Submit(SchoolInquiryRequest request, CancellationToken ct)
    {
        await inquiries.SubmitAsync(request, ct);
        return StatusCode(StatusCodes.Status202Accepted,
            ApiResponse.Ok<object>(null, Messages.SchoolInquiryReceived.Text, HttpContext.TraceIdentifier));
    }
}
