using DSVHVN.Application.Billing;
using DSVHVN.Infrastructure.Email;
using DSVHVN.Infrastructure.Payments;
using DSVHVN.Tests.Support;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Unit;

/// <summary>Dựng địa chỉ thanh toán VNPay và kiểm chữ ký HMAC-SHA512 (không gọi mạng).</summary>
public sealed class VnPayGatewayTests
{
    private static VnPayGateway Gateway(string tmn = VnPayTestKit.TmnCode, string secret = VnPayTestKit.HashSecret, string frontend = "http://localhost:5173") =>
        new(Options.Create(new VnPayOptions { TmnCode = tmn, HashSecret = secret }),
            Options.Create(new AppLinkOptions { FrontendBaseUrl = frontend }));

    private static VnPayPaymentRequest Request(long id = 42, decimal amount = 1_990_000m) => new(
        id, amount, "thanh toan hoa don hd20261008 000001",
        new DateTime(2026, 10, 8, 3, 30, 0, DateTimeKind.Utc), new DateTime(2026, 10, 8, 3, 45, 0, DateTimeKind.Utc), "203.0.113.10");

    [Fact]
    public void Payment_url_matches_a_signature_computed_independently_of_the_code()
    {
        // Kết quả tính sẵn bằng HMAC-SHA512 của Python trên chuỗi đã sắp theo tên khóa, giá trị mã hóa URL, khóa là VnPayTestKit.HashSecret.
        const string expectedSignedData =
            "vnp_Amount=199000000&vnp_Command=pay&vnp_CreateDate=20261008103000&vnp_CurrCode=VND&vnp_ExpireDate=20261008104500&vnp_IpAddr=203.0.113.10" +
            "&vnp_Locale=vn&vnp_OrderInfo=thanh+toan+hoa+don+hd20261008+000001&vnp_OrderType=other" +
            "&vnp_ReturnUrl=http%3A%2F%2Flocalhost%3A5080%2Fapi%2Fv1%2Fpayments%2Fvnpay%2Freturn&vnp_TmnCode=TESTTMN1&vnp_TxnRef=42&vnp_Version=2.1.0";
        const string expectedSignature =
            "9c597f5889cc0148a18e24a7d76f121a50a299166b51a72fda8bb8e264a43a1cd6fd86dc7f9719e351acf3af3b42c1ac70533a49c516e80c6b2bb54fc70ad899";

        var url = Gateway().CreatePaymentUrl(Request());

        Assert.Equal($"{VnPayOptions.SandboxPaymentUrl}?{expectedSignedData}&vnp_SecureHash={expectedSignature}", url);
    }

    [Fact]
    public void Payment_url_carries_amount_times_100_vietnam_time_and_the_payment_id_as_reference()
    {
        var p = VnPayTestKit.ParseUrl(Gateway().CreatePaymentUrl(Request(id: 7, amount: 500_000m)));

        Assert.Equal(("500000" + "00", "7", "VND", "pay", "2.1.0"), (p["vnp_Amount"], p["vnp_TxnRef"], p["vnp_CurrCode"], p["vnp_Command"], p["vnp_Version"]));
        Assert.Equal(("20261008103000", "20261008104500"), (p["vnp_CreateDate"], p["vnp_ExpireDate"]));   // 03:30 UTC = 10:30 giờ Việt Nam; hạn 15 phút
        Assert.Equal(VnPayTestKit.TmnCode, p["vnp_TmnCode"]);
    }

    [Fact]
    public void Parse_accepts_a_correct_signature_and_reads_the_fields()
    {
        var callback = Gateway().Parse(VnPayTestKit.Callback(paymentId: 42, priceVnd: 1_990_000m, transactionNo: "14012345"));

        Assert.True(callback.SignatureValid);
        Assert.Equal((42L, 199_000_000L, "00", "00", "14012345"),
            (callback.PaymentId, callback.Amount, callback.ResponseCode, callback.TransactionStatus, callback.TransactionNo));
        Assert.True(callback.IsSuccess);
    }

    [Theory]
    [InlineData("vnp_Amount", "100")]
    [InlineData("vnp_TxnRef", "43")]
    [InlineData("vnp_ResponseCode", "24")]
    [InlineData("vnp_OrderInfo", "x")]
    public void Parse_rejects_any_tampered_field(string field, string value)
    {
        var parameters = VnPayTestKit.Callback(42, 1_990_000m);
        parameters[field] = value;

        Assert.False(Gateway().Parse(parameters).SignatureValid);
    }

    [Fact]
    public void Parse_rejects_a_missing_empty_or_foreign_signature()
    {
        Assert.False(Gateway().Parse(VnPayTestKit.Callback(42, 1_990_000m, sign: false)).SignatureValid);

        var empty = VnPayTestKit.Callback(42, 1_990_000m);
        empty["vnp_SecureHash"] = "";
        Assert.False(Gateway().Parse(empty).SignatureValid);

        Assert.False(Gateway().Parse(VnPayTestKit.Callback(42, 1_990_000m, secret: "another-secret")).SignatureValid);
    }

    [Fact]
    public void Parse_ignores_parameters_outside_the_vnp_family_and_the_hash_type_when_checking_the_signature()
    {
        var parameters = VnPayTestKit.Callback(42, 1_990_000m);
        parameters["utm_source"] = "mail";
        parameters["vnp_SecureHashType"] = "SHA256";   // không nằm trong chuỗi ký

        Assert.True(Gateway().Parse(parameters).SignatureValid);
    }

    [Fact]
    public void Signature_comparison_ignores_the_case_of_the_hex_digits()
    {
        var parameters = VnPayTestKit.Callback(42, 1_990_000m);
        parameters["vnp_SecureHash"] = parameters["vnp_SecureHash"].ToUpperInvariant();

        Assert.True(Gateway().Parse(parameters).SignatureValid);
    }

    [Fact]
    public void Without_keys_nothing_is_signed_and_no_signature_is_accepted()
    {
        var gateway = Gateway(tmn: "", secret: "");

        Assert.False(gateway.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => gateway.CreatePaymentUrl(Request()));
        Assert.False(gateway.Parse(VnPayTestKit.Callback(42, 1_990_000m)).SignatureValid);
        Assert.False(Gateway(tmn: "", secret: VnPayTestKit.HashSecret).IsConfigured);
    }

    [Fact]
    public void Result_page_url_has_the_payment_id_only_for_a_valid_signature()
    {
        var gateway = Gateway(frontend: "https://quantri.example.vn/");

        Assert.Equal("https://quantri.example.vn/truong/ket-qua-thanh-toan?payment=42", gateway.BuildResultPageUrl(42, signatureValid: true));
        Assert.Equal("https://quantri.example.vn/truong/ket-qua-thanh-toan?error=invalid", gateway.BuildResultPageUrl(42, signatureValid: false));
        Assert.Equal("https://quantri.example.vn/truong/ket-qua-thanh-toan?error=invalid", gateway.BuildResultPageUrl(null, signatureValid: true));
    }
}
