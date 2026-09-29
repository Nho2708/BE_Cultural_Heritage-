using System.Globalization;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Email;

/// <summary>Cấu hình mục <c>App</c>: địa chỉ ứng dụng React để dựng liên kết trong thư.</summary>
public sealed class AppLinkOptions
{
    public const string Section = "App";

    public string FrontendBaseUrl { get; set; } = "http://localhost:5173";

    /// <summary>Route màn hình Đặt mật khẩu, dùng chung cho đặt mật khẩu lần đầu và đặt lại.</summary>
    public string SetPasswordPath { get; set; } = "/dat-mat-khau";
}

/// <summary>
/// Bản GIẢ của dịch vụ email: không gửi thư thật, chỉ ghi nội dung thư ra log.
/// Thư có chứa token đặt mật khẩu nên chỉ dùng ở máy phát triển. Gửi qua SMTP thật là việc của luồng sau.
/// </summary>
public sealed class ConsoleEmailSender(ILogger<ConsoleEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[Email giả] Tới: {To} | Tiêu đề: {Subject}{NewLine}{Body}", to, subject, Environment.NewLine, body);
        return Task.CompletedTask;
    }
}

/// <summary>Soạn hai loại thư đặt mật khẩu, nội dung chỉ tiếng Việt; giờ hết hạn hiện theo giờ Việt Nam.</summary>
public sealed class AccountMailer(IEmailSender sender, IOptions<AppLinkOptions> options) : IAccountMailer
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public Task SendFirstPasswordLinkAsync(string email, string fullName, string username, string? organizationName,
        string rawToken, DateTime expiresAt, CancellationToken cancellationToken = default)
    {
        var nl = Environment.NewLine;
        var where = organizationName is null ? string.Empty : $" của {organizationName}";
        var body =
            $"Xin chào {fullName},{nl}{nl}" +
            $"Tài khoản DSVHVN{where} đã được tạo cho bạn. Tên đăng nhập: {username}.{nl}" +
            $"Mở liên kết dưới đây để đặt mật khẩu lần đầu:{nl}{Link(rawToken)}{nl}{nl}" +
            $"Liên kết có hiệu lực 48 giờ (đến {Local(expiresAt)}) và chỉ dùng được một lần. " +
            $"Nếu liên kết hết hạn, hãy dùng chức năng \"Quên mật khẩu\" để nhận liên kết mới.";
        return sender.SendAsync(email, "Đặt mật khẩu tài khoản DSVHVN", body, cancellationToken);
    }

    public Task SendResetPasswordLinkAsync(string email, string fullName, string rawToken, DateTime expiresAt,
        CancellationToken cancellationToken = default)
    {
        var nl = Environment.NewLine;
        var body =
            $"Xin chào {fullName},{nl}{nl}" +
            $"Bạn vừa yêu cầu đặt lại mật khẩu tài khoản DSVHVN. Mở liên kết dưới đây để đặt mật khẩu mới:{nl}" +
            $"{Link(rawToken)}{nl}{nl}" +
            $"Liên kết có hiệu lực 30 phút (đến {Local(expiresAt)}) và chỉ dùng được một lần. " +
            $"Nếu bạn không yêu cầu, hãy bỏ qua thư này.";
        return sender.SendAsync(email, "Đặt lại mật khẩu DSVHVN", body, cancellationToken);
    }

    private string Link(string rawToken)
    {
        var opt = options.Value;
        return $"{opt.FrontendBaseUrl.TrimEnd('/')}{opt.SetPasswordPath}?token={Uri.EscapeDataString(rawToken)}";
    }

    private static string Local(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToOffset(VietnamTime.Offset)
            .ToString("HH:mm 'ngày' dd/MM/yyyy", Vi);
}
