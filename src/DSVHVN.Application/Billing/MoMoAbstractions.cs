namespace DSVHVN.Application.Billing;

/// <summary>
/// Dữ liệu MoMo gửi về (IPN dạng JSON hoặc query của trang trả về, cùng một bộ trường và cùng cách ký) sau khi đã kiểm chữ ký.
/// <see cref="Amount"/> là số tiền đồng MoMo ghi. Thành công khi <c>resultCode</c> = 0.
/// </summary>
public sealed record MoMoCallback(
    bool SignatureValid,
    long? PaymentId,
    long? Amount,
    string? PartnerCode,
    int? ResultCode,
    string? TransId,
    string? Message,
    IReadOnlyDictionary<string, string> Parameters)
{
    public bool IsSuccess => ResultCode == 0;
}

/// <summary>Phần xác thực dữ liệu MoMo gửi về, dùng cho <see cref="MoMoCallbackService"/>. Không gọi mạng.</summary>
public interface IMoMoGateway : IPaymentGateway
{
    /// <summary>Đọc và kiểm chữ ký HMAC-SHA256. Thiếu khóa cấu hình thì chữ ký coi như không hợp lệ.</summary>
    MoMoCallback Parse(IReadOnlyDictionary<string, string> parameters);

    string BuildResultPageUrl(long? paymentId, bool signatureValid);
}
