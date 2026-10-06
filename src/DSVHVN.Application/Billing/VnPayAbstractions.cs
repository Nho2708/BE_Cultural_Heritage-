namespace DSVHVN.Application.Billing;

/// <summary>Một yêu cầu thanh toán gửi VNPay: mã tham chiếu là mã dòng <c>payments</c>, số tiền là đồng Việt Nam (chưa nhân 100).</summary>
public sealed record VnPayPaymentRequest(
    long PaymentId,
    decimal Amount,
    string OrderInfo,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    string IpAddress);

/// <summary>
/// Các tham số VNPay gửi về (qua IPN hoặc trình duyệt) sau khi đã kiểm chữ ký. <see cref="Amount"/> là số tiền VNPay ghi, đã nhân 100.
/// </summary>
public sealed record VnPayCallback(
    bool SignatureValid,
    long? PaymentId,
    long? Amount,
    string? TmnCode,
    string? ResponseCode,
    string? TransactionStatus,
    string? TransactionNo,
    IReadOnlyDictionary<string, string> Parameters)
{
    /// <summary>Thành công khi cả mã phản hồi và mã trạng thái giao dịch đều là "00".</summary>
    public bool IsSuccess => ResponseCode == "00" && TransactionStatus == "00";
}

/// <summary>
/// Cổng VNPay sandbox: dựng địa chỉ thanh toán có chữ ký (HMAC-SHA512 theo tài liệu VNPay) và kiểm chữ ký của dữ liệu VNPay gửi về.
/// Không gọi mạng. Mã cửa hàng và khóa bí mật chỉ nằm trong cấu hình (User Secrets hoặc biến môi trường), không có trong mã nguồn.
/// </summary>
public interface IVnPayGateway
{
    /// <summary>Đã có mã cửa hàng và khóa bí mật. Chưa có thì không tạo được địa chỉ thanh toán và IPN bị từ chối.</summary>
    bool IsConfigured { get; }

    string CreatePaymentUrl(VnPayPaymentRequest request);

    /// <summary>Đọc và kiểm chữ ký các tham số <c>vnp_*</c>. Thiếu khóa cấu hình thì chữ ký coi như không hợp lệ.</summary>
    VnPayCallback Parse(IReadOnlyDictionary<string, string> query);

    /// <summary>Địa chỉ trang kết quả thanh toán của web quản trị mà trình duyệt được chuyển tới; không kèm dữ liệu nhạy cảm.</summary>
    string BuildResultPageUrl(long? paymentId, bool signatureValid);
}
