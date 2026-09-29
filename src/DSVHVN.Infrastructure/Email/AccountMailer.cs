using System.Globalization;
using System.Text;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Rules;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Email;

/// <summary>Gửi hai loại thư đặt mật khẩu: soạn bằng <see cref="AccountEmailComposer"/>, gửi qua <see cref="IEmailSender"/>.</summary>
public sealed class AccountMailer(IEmailSender sender, IOptions<AppLinkOptions> options) : IAccountMailer
{
    private readonly AccountEmailComposer _composer = new(options.Value);

    public Task SendFirstPasswordLinkAsync(string email, string fullName, string username, string? organizationName,
        string rawToken, DateTime expiresAt, CancellationToken cancellationToken = default) =>
        sender.SendAsync(_composer.FirstPasswordLink(email, fullName, username, organizationName, rawToken, expiresAt),
            cancellationToken);

    public Task SendResetPasswordLinkAsync(string email, string fullName, string rawToken, DateTime expiresAt,
        CancellationToken cancellationToken = default) =>
        sender.SendAsync(_composer.ResetPasswordLink(email, fullName, rawToken, expiresAt), cancellationToken);
}

/// <summary>
/// Soạn nội dung hai loại thư đặt mật khẩu, chỉ tiếng Việt: cùng một nội dung ở bản chữ thuần và bản HTML (có nút bấm).
/// Liên kết = <c>App:FrontendBaseUrl</c> + <c>App:SetPasswordPath</c> + <c>?token=…</c>; thời hạn lấy từ
/// <see cref="AccountRules"/>, giờ hết hạn hiện theo giờ Việt Nam.
/// </summary>
public sealed class AccountEmailComposer(AppLinkOptions links)
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    private const string Footer = "Thư được gửi tự động từ " + SmtpOptions.DefaultFromName + ". Vui lòng không trả lời thư này.";

    public EmailMessage FirstPasswordLink(string email, string fullName, string username, string? organizationName,
        string rawToken, DateTime expiresAt)
    {
        var where = string.IsNullOrWhiteSpace(organizationName) ? string.Empty : $" của {organizationName.Trim()}";
        return Compose(new Letter(
            To: email,
            FullName: fullName,
            Subject: "Đặt mật khẩu cho tài khoản DSVHVN của bạn",
            Intro:
            [
                new Line($"Tài khoản DSVHVN{where} đã được tạo cho bạn. Tên đăng nhập: ", username, "."),
                new Line("Mở liên kết dưới đây để đặt mật khẩu lần đầu:"),
            ],
            ButtonText: "Đặt mật khẩu",
            Link: Link(rawToken),
            Validity:
                $"Liên kết có hiệu lực {Lifetime(AccountRules.FirstPasswordLinkLifetime)} (đến {Local(expiresAt)}) và chỉ dùng được một lần. " +
                "Nếu liên kết hết hạn, hãy dùng chức năng \"Quên mật khẩu\" ở trang đăng nhập để nhận liên kết mới.",
            IgnoreNote: "Nếu bạn không yêu cầu, hãy bỏ qua thư này."));
    }

    public EmailMessage ResetPasswordLink(string email, string fullName, string rawToken, DateTime expiresAt) =>
        Compose(new Letter(
            To: email,
            FullName: fullName,
            Subject: "Đặt lại mật khẩu tài khoản DSVHVN",
            Intro:
            [
                new Line("Bạn vừa yêu cầu đặt lại mật khẩu tài khoản DSVHVN."),
                new Line("Mở liên kết dưới đây để đặt mật khẩu mới:"),
            ],
            ButtonText: "Đặt lại mật khẩu",
            Link: Link(rawToken),
            Validity:
                $"Liên kết có hiệu lực {Lifetime(AccountRules.ForgotPasswordLinkLifetime)} (đến {Local(expiresAt)}) và chỉ dùng được một lần.",
            IgnoreNote: "Nếu bạn không yêu cầu, hãy bỏ qua thư này; mật khẩu hiện tại của bạn vẫn giữ nguyên."));

    /// <summary>Liên kết tới màn hình Đặt mật khẩu của ứng dụng React.</summary>
    public string Link(string rawToken)
    {
        var path = "/" + links.SetPasswordPath.Trim().TrimStart('/');
        return $"{links.FrontendBaseUrl.Trim().TrimEnd('/')}{path}?token={Uri.EscapeDataString(rawToken)}";
    }

    /// <summary>"48 giờ", "30 phút".</summary>
    public static string Lifetime(TimeSpan lifetime) =>
        lifetime.TotalHours >= 1 && lifetime.Ticks % TimeSpan.TicksPerHour == 0
            ? $"{(long)lifetime.TotalHours} giờ"
            : $"{(long)Math.Ceiling(lifetime.TotalMinutes)} phút";

    private static string Local(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToOffset(VietnamTime.Offset)
            .ToString("HH:mm 'ngày' dd/MM/yyyy", Vi);

    private static EmailMessage Compose(Letter letter)
    {
        var greeting = string.IsNullOrWhiteSpace(letter.FullName) ? "bạn" : letter.FullName.Trim();
        return new EmailMessage(letter.To, string.IsNullOrWhiteSpace(letter.FullName) ? null : letter.FullName.Trim(),
            letter.Subject, Text(letter, greeting), Html(letter, greeting));
    }

    private static string Text(Letter letter, string greeting)
    {
        var sb = new StringBuilder();
        sb.Append("Xin chào ").Append(greeting).Append(",\n\n");
        foreach (var line in letter.Intro) sb.Append(line.Before).Append(line.Emphasis).Append(line.After).Append('\n');
        sb.Append(letter.Link).Append("\n\n");
        sb.Append(letter.Validity).Append("\n\n");
        sb.Append(letter.IgnoreNote).Append("\n\n");
        sb.Append("--\n").Append(Footer).Append('\n');
        return sb.ToString();
    }

    private static string Html(Letter letter, string greeting)
    {
        const string Accent = "#9b2c2c";
        var link = Encode(letter.Link);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"vi\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>").Append(Encode(letter.Subject)).Append("</title>\n</head>\n")
            .Append("<body style=\"margin:0;padding:0;background:#f4f1ea;font-family:Arial,Helvetica,sans-serif;color:#1f2933;\">\n")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#f4f1ea;padding:24px 12px;\">\n")
            .Append("<tr><td align=\"center\">\n")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" ")
            .Append("style=\"max-width:560px;background:#ffffff;border:1px solid #e5ded0;border-radius:8px;\">\n")
            .Append("<tr><td style=\"padding:20px 28px;border-bottom:3px solid ").Append(Accent)
            .Append(";font-size:18px;font-weight:bold;color:").Append(Accent).Append(";\">")
            .Append(Encode(SmtpOptions.DefaultFromName)).Append("</td></tr>\n")
            .Append("<tr><td style=\"padding:24px 28px;font-size:15px;line-height:1.6;\">\n")
            .Append("<p style=\"margin:0 0 16px;\">Xin chào <strong>").Append(Encode(greeting)).Append("</strong>,</p>\n");

        foreach (var line in letter.Intro)
        {
            sb.Append("<p style=\"margin:0 0 12px;\">").Append(Encode(line.Before));
            if (line.Emphasis.Length > 0) sb.Append("<strong>").Append(Encode(line.Emphasis)).Append("</strong>");
            sb.Append(Encode(line.After)).Append("</p>\n");
        }

        sb.Append("<p style=\"margin:24px 0;text-align:center;\"><a href=\"").Append(link)
            .Append("\" style=\"display:inline-block;padding:12px 28px;background:").Append(Accent)
            .Append(";color:#ffffff;text-decoration:none;border-radius:6px;font-weight:bold;\">")
            .Append(Encode(letter.ButtonText)).Append("</a></p>\n")
            .Append("<p style=\"margin:0 0 4px;font-size:13px;color:#52606d;\">Nếu nút không mở được, hãy sao chép đường dẫn sau vào trình duyệt:</p>\n")
            .Append("<p style=\"margin:0 0 16px;font-size:13px;word-break:break-all;\"><a href=\"").Append(link)
            .Append("\" style=\"color:").Append(Accent).Append(";\">").Append(link).Append("</a></p>\n")
            .Append("<p style=\"margin:0 0 12px;\">").Append(Encode(letter.Validity)).Append("</p>\n")
            .Append("<p style=\"margin:0;color:#52606d;\">").Append(Encode(letter.IgnoreNote)).Append("</p>\n")
            .Append("</td></tr>\n")
            .Append("<tr><td style=\"padding:16px 28px;border-top:1px solid #e5ded0;font-size:12px;color:#7b8794;\">")
            .Append(Encode(Footer)).Append("</td></tr>\n")
            .Append("</table>\n</td></tr>\n</table>\n</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Mã hóa HTML tối thiểu cho nội dung và thuộc tính trong nháy kép. Không đổi chữ tiếng Việt thành thực thể số
    /// (khác <c>WebUtility.HtmlEncode</c>) để mã nguồn thư dễ đọc.
    /// </summary>
    private static string Encode(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#39;", StringComparison.Ordinal);

    /// <summary>Một đoạn mở đầu; <see cref="Emphasis"/> in đậm ở bản HTML (vd tên đăng nhập).</summary>
    private sealed record Line(string Before, string Emphasis = "", string After = "");

    private sealed record Letter(
        string To,
        string FullName,
        string Subject,
        IReadOnlyList<Line> Intro,
        string ButtonText,
        string Link,
        string Validity,
        string IgnoreNote);
}
