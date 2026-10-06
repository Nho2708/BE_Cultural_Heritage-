using DSVHVN.Application.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Hai địa chỉ công khai cho VNPay (không đăng nhập, bảo vệ bằng chữ ký HMAC-SHA512):
/// IPN (máy chủ VNPay gọi máy chủ này, duy nhất đổi trạng thái giao dịch, hóa đơn, gói) và trang trả về (trình duyệt của người trả).
/// </summary>
[Route("api/v1/payments/vnpay")]
[AllowAnonymous]
[ApiController]
public sealed class VnPayController(VnPayCallbackService callbacks) : ControllerBase
{
    /// <summary>
    /// IPN của VNPay (HTTPS GET, tham số <c>vnp_*</c> trên query string). Trả đúng khuôn của VNPay (hai thuộc tính RspCode và Message):
    /// 00 đã ghi nhận, 01 không thấy giao dịch, 02 giao dịch đã xử lý trước đó (IPN lặp, không đổi dữ liệu), 04 sai số tiền, 97 sai chữ ký, 99 lỗi khác.
    /// Địa chỉ này khai trong trang quản lý cửa hàng của VNPay.
    /// </summary>
    [HttpGet("ipn")]
    [Produces("application/json")]
    [ProducesResponseType<IpnResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Ipn(CancellationToken ct)
    {
        var response = await callbacks.HandleIpnAsync(QueryOf(Request), HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        // Tên thuộc tính theo đúng tài liệu VNPay (chữ hoa đầu), không theo quy ước camelCase của API.
        return new JsonResult(new Dictionary<string, string> { ["RspCode"] = response.RspCode, ["Message"] = response.Message });
    }

    /// <summary>
    /// Trang VNPay đưa trình duyệt về sau khi trả. Chỉ kiểm chữ ký rồi chuyển sang trang Kết quả thanh toán của web quản trị (302); không đổi dữ liệu.
    /// Chữ ký sai thì chuyển kèm <c>error=invalid</c>.
    /// </summary>
    [HttpGet("return")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public IActionResult Return() => Redirect(callbacks.ResolveReturnUrl(QueryOf(Request)));

    private static Dictionary<string, string> QueryOf(HttpRequest request) =>
        request.Query.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
}
