namespace DSVHVN.Application.Billing;

/// <summary>
/// Webhook của PayOS sau khi đã kiểm chữ ký (HMAC-SHA256 bằng checksum key trên các trường của <c>data</c> sắp theo tên).
/// <see cref="Amount"/> là số tiền đồng. Thành công khi <c>success</c> là true và <c>data.code</c> = "00".
/// </summary>
public sealed record PayOsWebhook(
    bool SignatureValid,
    long? PaymentId,
    long? Amount,
    bool IsSuccess,
    string? Code,
    string? Reference,
    string? PaymentLinkId,
    string RawJson);

/// <summary>Phần xác thực dữ liệu PayOS gửi về, dùng cho <see cref="PayOsCallbackService"/>. Không gọi mạng.</summary>
public interface IPayOsGateway : IPaymentGateway
{
    PayOsWebhook ParseWebhook(string rawBody);

    /// <summary>
    /// Trang trả về của PayOS không có chữ ký nên chỉ lấy mã giao dịch từ <c>orderCode</c> để chuyển sang trang kết quả; trang đó chỉ đọc
    /// và chỉ mở được bởi quản trị của đúng trường nên không lộ gì.
    /// </summary>
    string BuildResultPageUrl(IReadOnlyDictionary<string, string> query);
}
