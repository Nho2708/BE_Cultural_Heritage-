using Microsoft.Extensions.Options;
using MimeKit;

namespace DSVHVN.Infrastructure.Email;

/// <summary>Cấu hình mục <c>App</c>: địa chỉ ứng dụng React để dựng liên kết trong thư.</summary>
public sealed class AppLinkOptions
{
    public const string Section = "App";

    public string FrontendBaseUrl { get; set; } = "http://localhost:5173";

    /// <summary>Route màn hình Đặt mật khẩu, dùng chung cho đặt mật khẩu lần đầu và đặt lại.</summary>
    public string SetPasswordPath { get; set; } = "/dat-mat-khau";
}

/// <summary>Cách xử lý thư đi.</summary>
public enum EmailMode
{
    /// <summary>Không gửi, chỉ ghi nguyên thư (kể cả liên kết có token) ra log. Chỉ dùng ở máy phát triển.</summary>
    Log,

    /// <summary>Gửi thật qua máy chủ SMTP khai ở <c>Email:Smtp</c>.</summary>
    Smtp,
}

/// <summary>Cấu hình mục <c>Email</c>. Mặc định <see cref="EmailMode.Log"/> để máy phát triển không cần SMTP.</summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    public EmailMode Mode { get; set; } = EmailMode.Log;

    public SmtpOptions Smtp { get; set; } = new();
}

/// <summary>
/// Cấu hình mục <c>Email:Smtp</c>. <see cref="Password"/> không ghi vào appsettings: đặt bằng User Secrets
/// (<c>dotnet user-secrets set "Email:Smtp:Password" …</c>) hoặc biến môi trường <c>Email__Smtp__Password</c>.
/// </summary>
public sealed class SmtpOptions
{
    public const string DefaultFromName = "DSVHVN — Nền tảng học di sản";

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    /// <summary>
    /// true: bắt buộc nâng kết nối lên TLS bằng STARTTLS (cổng 587). false: tự chọn theo cổng — 465 mã hóa ngay khi kết nối,
    /// cổng khác dùng STARTTLS nếu máy chủ hỗ trợ (máy chủ thư thử nghiệm trên máy phát triển).
    /// </summary>
    public bool UseStartTls { get; set; } = true;

    /// <summary>Để trống nếu máy chủ không yêu cầu đăng nhập.</summary>
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = DefaultFromName;

    /// <summary>Thời gian chờ tối đa cho mỗi bước kết nối, đăng nhập, gửi.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Kiểm cấu hình khi <c>Email:Mode</c> là <c>Smtp</c>; thiếu mục bắt buộc thì dừng khởi động và liệt kê đủ các mục cần đặt.
/// </summary>
public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        if (options.Mode != EmailMode.Smtp) return ValidateOptionsResult.Success;

        var smtp = options.Smtp;
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(smtp.Host)) problems.Add("thiếu Email:Smtp:Host (vd smtp.gmail.com)");
        if (smtp.Port is < 1 or > 65535) problems.Add("Email:Smtp:Port phải từ 1 đến 65535");
        if (string.IsNullOrWhiteSpace(smtp.FromAddress))
            problems.Add("thiếu Email:Smtp:FromAddress (địa chỉ người gửi)");
        else if (!MailboxAddress.TryParse(smtp.FromAddress, out _) || !smtp.FromAddress.Contains('@'))
            problems.Add($"Email:Smtp:FromAddress \"{smtp.FromAddress}\" không phải địa chỉ email hợp lệ");

        var hasUsername = !string.IsNullOrWhiteSpace(smtp.Username);
        var hasPassword = !string.IsNullOrEmpty(smtp.Password);
        if (hasUsername && !hasPassword)
            problems.Add("có Email:Smtp:Username nhưng thiếu Email:Smtp:Password " +
                "(đặt bằng User Secrets hoặc biến môi trường Email__Smtp__Password, không ghi vào appsettings)");
        if (!hasUsername && hasPassword) problems.Add("có Email:Smtp:Password nhưng thiếu Email:Smtp:Username");
        if (smtp.TimeoutSeconds < 1) problems.Add("Email:Smtp:TimeoutSeconds phải lớn hơn 0");

        return problems.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Cấu hình gửi thư qua SMTP chưa đủ (Email:Mode = Smtp): " + string.Join("; ", problems) + ".");
    }
}
