using DSVHVN.Application.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Hai địa chỉ công khai cho PayOS: webhook (máy chủ PayOS gọi máy chủ này, bảo vệ bằng chữ ký HMAC-SHA256, duy nhất đổi trạng thái giao dịch, hóa đơn, gói)
/// và trang trả về (trình duyệt của người trả, cả khi thanh toán xong lẫn khi bấm hủy; PayOS không ký trang này nên nó không đổi dữ liệu).
/// </summary>
[Route("api/v1/payments/payos")]
[AllowAnonymous]
[ApiController]
public sealed class PayOsController(PayOsCallbackService callbacks) : ControllerBase
{
    /// <summary>
    /// Webhook của PayOS (HTTPS POST, thân JSON). Luôn trả 200 kể cả khi chữ ký sai hay không thấy giao dịch (chỉ ghi nhật ký) để PayOS không gửi lại mãi và để
    /// bước khai địa chỉ webhook ở my.payos.vn (PayOS gửi thử một lần) thành công; lỗi hệ thống (CSDL) trả 500 để PayOS gửi lại.
    /// </summary>
    [HttpPost("webhook")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        await callbacks.HandleWebhookAsync(body, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        return Ok(new { success = true });
    }

    /// <summary>
    /// Trang PayOS đưa trình duyệt về (sau khi trả hoặc khi hủy). Chuyển sang trang Kết quả thanh toán của web quản trị (302), không đổi dữ liệu:
    /// giao dịch chỉ thành công khi webhook tới. Giao dịch bị hủy ở PayOS hiện "đang chờ xác nhận" cho tới khi tác vụ nền đóng nó (30 phút).
    /// </summary>
    [HttpGet("return")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public IActionResult Return() =>
        Redirect(callbacks.ResolveReturnUrl(Request.Query.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal)));
}
