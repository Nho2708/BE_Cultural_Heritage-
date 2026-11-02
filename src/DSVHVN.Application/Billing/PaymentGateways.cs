using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Billing;

/// <summary>Thông tin một lần thanh toán gửi sang cổng: mã tham chiếu là mã dòng <c>payments</c>, số tiền là đồng Việt Nam (chưa nhân gì).</summary>
public sealed record PaymentStartContext(
    long PaymentId,
    decimal Amount,
    string InvoiceNumber,
    string OrderInfo,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    string IpAddress);

/// <summary>Cổng gọi tới nhà cung cấp thanh toán gặp lỗi khi tạo giao dịch (mạng, cổng từ chối, thiếu khóa). Tầng dịch vụ đổi thành 503.</summary>
public sealed class PaymentGatewayException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Một cổng thanh toán (VNPay, MoMo, PayOS) ở phía tạo giao dịch: nhận <see cref="PaymentStartContext"/> và trả địa chỉ để chuyển trình duyệt sang.
/// Phần xác thực dữ liệu cổng gửi về (IPN, webhook, trang trả về) riêng cho từng cổng và cùng đi vào <see cref="PaymentSettlementService"/>.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>Mã trong <c>payment_providers.code</c>.</summary>
    string Code { get; }

    /// <summary>Tên hiển thị ở hộp chọn phương thức và hộp xác nhận.</summary>
    string DisplayName { get; }

    /// <summary>Đã có đủ khóa cấu hình. Chưa có thì không tạo được giao dịch và mọi IPN bị từ chối.</summary>
    bool IsConfigured { get; }

    /// <summary>Tạo giao dịch ở cổng và trả địa chỉ thanh toán. Lỗi gọi cổng thì ném <see cref="PaymentGatewayException"/>.</summary>
    Task<string> CreatePaymentUrlAsync(PaymentStartContext context, CancellationToken ct);
}

/// <summary>Danh sách cổng đã đăng ký, tra theo mã.</summary>
public sealed class PaymentGatewayRegistry(IEnumerable<IPaymentGateway> gateways)
{
    private readonly IReadOnlyList<IPaymentGateway> _all = gateways.ToList();

    public IReadOnlyList<IPaymentGateway> All => _all;

    public IPaymentGateway? Find(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : _all.FirstOrDefault(g => string.Equals(g.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>VNPay ở dạng <see cref="IPaymentGateway"/>: bọc <see cref="IVnPayGateway"/> (không gọi mạng, dựng địa chỉ có chữ ký).</summary>
public sealed class VnPayProvider(IVnPayGateway inner) : IPaymentGateway
{
    public string Code => BillingRules.VnPayProviderCode;

    public string DisplayName => "VNPay";

    public bool IsConfigured => inner.IsConfigured;

    public Task<string> CreatePaymentUrlAsync(PaymentStartContext context, CancellationToken ct) =>
        Task.FromResult(inner.CreatePaymentUrl(new VnPayPaymentRequest(
            context.PaymentId, context.Amount, context.OrderInfo, context.CreatedAtUtc, context.ExpiresAtUtc, context.IpAddress)));
}
