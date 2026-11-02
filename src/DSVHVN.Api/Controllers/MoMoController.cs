using System.Globalization;
using System.Text.Json;
using DSVHVN.Application.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Hai địa chỉ công khai cho MoMo (không đăng nhập, bảo vệ bằng chữ ký HMAC-SHA256):
/// IPN (máy chủ MoMo gọi máy chủ này, duy nhất đổi trạng thái giao dịch, hóa đơn, gói) và trang trả về (trình duyệt của người trả).
/// </summary>
[Route("api/v1/payments/momo")]
[AllowAnonymous]
[ApiController]
public sealed class MoMoController(MoMoCallbackService callbacks) : ControllerBase
{
    /// <summary>
    /// IPN của MoMo (HTTPS POST, thân JSON). Đã xử lý (kể cả IPN lặp) thì trả 204 để MoMo thôi gửi lại; chữ ký sai, không thấy giao dịch hoặc lệch số tiền thì 400
    /// và không đổi dữ liệu. Địa chỉ này là <c>MoMo:IpnUrl</c>, gửi kèm mỗi yêu cầu tạo giao dịch.
    /// </summary>
    [HttpPost("ipn")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Ipn([FromBody] JsonElement body, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object) return BadRequest();
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in body.EnumerateObject())
        {
            parameters[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => string.Empty,
            };
        }

        var result = await callbacks.HandleIpnAsync(parameters, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        return StatusCode(result.HttpStatus);
    }

    /// <summary>
    /// Trang MoMo đưa trình duyệt về sau khi trả. Chỉ kiểm chữ ký rồi chuyển sang trang Kết quả thanh toán của web quản trị (302); không đổi dữ liệu.
    /// Chữ ký sai thì chuyển kèm <c>error=invalid</c>.
    /// </summary>
    [HttpGet("return")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public IActionResult Return() =>
        Redirect(callbacks.ResolveReturnUrl(Request.Query.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal)));
}
