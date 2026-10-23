using System.Globalization;
using System.Text;
using DSVHVN.Application.Common;
using DSVHVN.Application.SchoolInquiries;
using DSVHVN.Domain.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Email;

/// <summary>Cấu hình mục <c>SchoolInquiry</c>: hộp thư nhận thư liên hệ của trường (vd hộp thư chung của nhóm).</summary>
public sealed class SchoolInquiryOptions
{
    public const string Section = "SchoolInquiry";

    /// <summary>Địa chỉ email nhận thư liên hệ của trường, thay cho email của quản trị hệ thống; để trống thì thư tới quản trị hệ thống.</summary>
    public string[] Recipients { get; set; } = [];
}

/// <summary>
/// Gửi thư liên hệ của trường: có địa chỉ dùng được ở <c>SchoolInquiry:Recipients</c> thì chỉ gửi tới các địa chỉ đó, không có thì gửi tới
/// quản trị hệ thống. Mỗi người nhận một thư riêng (không lộ địa chỉ cho nhau), qua <see cref="IEmailSender"/>.
/// Lỗi của từng người nhận được ghi log (chỉ ghi địa chỉ đã che bớt) và không chặn những người còn lại.
/// </summary>
public sealed class SchoolInquiryMailer(
    IEmailSender sender,
    IOptions<SchoolInquiryOptions> options,
    TimeProvider clock,
    ILogger<SchoolInquiryMailer> logger) : ISchoolInquiryMailer
{
    public async Task<SchoolInquiryDelivery> SendAsync(SchoolInquiry inquiry, IReadOnlyCollection<string> adminEmails,
        CancellationToken cancellationToken = default)
    {
        var configured = Usable(options.Value.Recipients ?? []);
        var recipients = configured.Count > 0 ? configured : Usable(adminEmails);

        var delivered = 0;
        foreach (var recipient in recipients)
        {
            try
            {
                await sender.SendAsync(SchoolInquiryEmailComposer.Compose(inquiry, recipient, clock.GetUtcNow().UtcDateTime), cancellationToken);
                delivered++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Gửi thư liên hệ của trường thất bại tới {Recipient}", SmtpEmailSender.MaskAddress(recipient));
            }
        }

        return new SchoolInquiryDelivery(recipients.Count, delivered);
    }

    /// <summary>Chuẩn hóa, bỏ địa chỉ sai định dạng, bỏ tên miền <c>.local</c> không nhận thư, bỏ trùng.</summary>
    private static List<string> Usable(IEnumerable<string> addresses) => addresses
        .Select(EmailAddress.Normalize)
        .Where(ValidationRules.IsEmail)
        .Where(EmailAddress.CanReceiveMail)
        .Distinct(StringComparer.Ordinal)
        .ToList();
}

/// <summary>
/// Soạn thư liên hệ của trường gửi quản trị, chỉ tiếng Việt: bản chữ thuần và bản HTML cùng nội dung. Mọi chữ do người dùng nhập được mã hóa HTML ở bản HTML.
/// Địa chỉ người nhập đặt làm địa chỉ trả lời để quản trị trả lời thẳng.
/// </summary>
public static class SchoolInquiryEmailComposer
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    private const string ProductName = "DiViet";

    public static EmailMessage Compose(SchoolInquiry inquiry, string recipient, DateTime receivedAtUtc)
    {
        var subject = $"{ProductName} – Trường liên hệ: {inquiry.SchoolName}";
        var received = new DateTimeOffset(DateTime.SpecifyKind(receivedAtUtc, DateTimeKind.Utc)).ToOffset(VietnamTime.Offset)
            .ToString("HH:mm 'ngày' dd/MM/yyyy", Vi);

        var fields = new List<(string Label, string Value)>
        {
            ("Tên trường", inquiry.SchoolName),
            ("Tỉnh/thành", inquiry.Province ?? "(không nhập)"),
            ("Người liên hệ", inquiry.ContactName),
            ("Chức vụ", inquiry.Position ?? "(không nhập)"),
            ("Email", inquiry.Email),
            ("Số điện thoại", inquiry.Phone ?? "(không nhập)"),
        };

        return new EmailMessage(recipient, null, subject, Text(fields, inquiry.Message, received), Html(subject, fields, inquiry.Message, received),
            ReplyTo: inquiry.Email);
    }

    private static string Text(IReadOnlyList<(string Label, string Value)> fields, string message, string received)
    {
        var sb = new StringBuilder();
        sb.Append("Một trường vừa gửi thông tin liên hệ để được tạo tài khoản trên ").Append(ProductName).Append(" (nhận lúc ").Append(received).Append(").\n\n");
        foreach (var (label, value) in fields) sb.Append(label).Append(": ").Append(value).Append('\n');
        sb.Append("\nNội dung:\n").Append(message).Append("\n\n");
        sb.Append("Trả lời thư này để liên hệ thẳng với người gửi.\n");
        sb.Append("--\nThư được gửi tự động từ ").Append(ProductName).Append(".\n");
        return sb.ToString();
    }

    private static string Html(string subject, IReadOnlyList<(string Label, string Value)> fields, string message, string received)
    {
        const string Accent = "#9b2c2c";
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"vi\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>").Append(Encode(subject)).Append("</title>\n</head>\n")
            .Append("<body style=\"margin:0;padding:0;background:#f4f1ea;font-family:Arial,Helvetica,sans-serif;color:#1f2933;\">\n")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#f4f1ea;padding:24px 12px;\">\n")
            .Append("<tr><td align=\"center\">\n")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" ")
            .Append("style=\"max-width:600px;background:#ffffff;border:1px solid #e5ded0;border-radius:8px;\">\n")
            .Append("<tr><td style=\"padding:20px 28px;border-bottom:3px solid ").Append(Accent)
            .Append(";font-size:18px;font-weight:bold;color:").Append(Accent).Append(";\">").Append(Encode(ProductName)).Append("</td></tr>\n")
            .Append("<tr><td style=\"padding:24px 28px;font-size:15px;line-height:1.6;\">\n")
            .Append("<p style=\"margin:0 0 16px;\">Một trường vừa gửi thông tin liên hệ để được tạo tài khoản (nhận lúc ")
            .Append(Encode(received)).Append(").</p>\n")
            .Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"width:100%;margin:0 0 16px;\">\n");

        foreach (var (label, value) in fields)
            sb.Append("<tr><td style=\"padding:4px 12px 4px 0;width:140px;vertical-align:top;color:#52606d;\">").Append(Encode(label))
                .Append("</td><td style=\"padding:4px 0;vertical-align:top;\">").Append(Encode(value)).Append("</td></tr>\n");

        sb.Append("</table>\n")
            .Append("<p style=\"margin:0 0 4px;color:#52606d;\">Nội dung</p>\n")
            .Append("<p style=\"margin:0 0 16px;padding:12px 14px;background:#f4f1ea;border-radius:6px;\">")
            .Append(Encode(message).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "<br>\n", StringComparison.Ordinal)).Append("</p>\n")
            .Append("<p style=\"margin:0;font-size:13px;color:#52606d;\">Trả lời thư này để liên hệ thẳng với người gửi.</p>\n")
            .Append("</td></tr>\n")
            .Append("<tr><td style=\"padding:16px 28px;border-top:1px solid #e5ded0;font-size:12px;color:#7b8794;\">Thư được gửi tự động từ ")
            .Append(Encode(ProductName)).Append(".</td></tr>\n")
            .Append("</table>\n</td></tr>\n</table>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static string Encode(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#39;", StringComparison.Ordinal);
}
