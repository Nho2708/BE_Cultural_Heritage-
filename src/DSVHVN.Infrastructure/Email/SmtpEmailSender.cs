using DSVHVN.Application.Common;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DSVHVN.Infrastructure.Email;

/// <summary>
/// Chế độ <c>Email:Mode = Smtp</c>: gửi thật qua MailKit, mỗi thư một kết nối (kết nối, đăng nhập, gửi, ngắt).
/// Log chỉ ghi địa chỉ người nhận đã che bớt và tiêu đề, không bao giờ ghi nội dung thư (có token đặt mật khẩu).
/// Gửi không được thì ném <see cref="EmailDeliveryException"/>; nơi gọi ghi log lỗi và không làm hỏng thao tác đã lưu.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _smtp;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _smtp = options.Value.Smtp;
        _logger = logger;
        _logger.LogInformation("Thư đặt mật khẩu gửi thật qua SMTP {Host}:{Port}, người gửi {From}",
            _smtp.Host, _smtp.Port, _smtp.FromAddress);
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var mime = CreateMimeMessage(message, _smtp);
        var recipient = MaskAddress(message.To);
        try
        {
            using var client = new SmtpClient { Timeout = (int)TimeSpan.FromSeconds(_smtp.TimeoutSeconds).TotalMilliseconds };
            await client.ConnectAsync(_smtp.Host.Trim(), _smtp.Port, SocketOptionsFor(_smtp), cancellationToken);
            if (!string.IsNullOrWhiteSpace(_smtp.Username))
                await client.AuthenticateAsync(_smtp.Username.Trim(), _smtp.Password, cancellationToken);
            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Hủy do chính yêu cầu bị hủy thì để nguyên; hủy do hết thời gian chờ của MailKit thì coi là gửi không được.
            throw new EmailDeliveryException(Describe(ex, recipient), ex);
        }

        _logger.LogInformation("Đã gửi thư qua SMTP tới {Recipient} | Tiêu đề: {Subject}", recipient, message.Subject);
    }

    /// <summary>Thư MIME nhiều phần (chữ thuần + HTML), UTF-8; họ tên có dấu được mã hóa đúng chuẩn trong tiêu đề thư.</summary>
    public static MimeMessage CreateMimeMessage(EmailMessage message, SmtpOptions smtp)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(smtp.FromName?.Trim() ?? string.Empty, smtp.FromAddress.Trim()));
        mime.To.Add(new MailboxAddress(message.ToName ?? string.Empty, message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();
        return mime;
    }

    /// <summary>
    /// <c>UseStartTls = true</c>: bắt buộc STARTTLS (máy chủ không hỗ trợ thì báo lỗi, không gửi trần).
    /// false: cổng 465 mã hóa ngay khi kết nối, cổng khác dùng STARTTLS nếu máy chủ hỗ trợ.
    /// </summary>
    public static SecureSocketOptions SocketOptionsFor(SmtpOptions smtp) =>
        smtp.UseStartTls ? SecureSocketOptions.StartTls
        : smtp.Port == 465 ? SecureSocketOptions.SslOnConnect
        : SecureSocketOptions.StartTlsWhenAvailable;

    /// <summary>Che bớt phần trước <c>@</c> khi ghi log: <c>quangnho278@gmail.com</c> → <c>qu***@gmail.com</c>.</summary>
    public static string MaskAddress(string address)
    {
        var at = address.LastIndexOf('@');
        if (at <= 0) return "***";
        var keep = Math.Min(2, at - 1);
        return address[..keep] + "***" + address[at..];
    }

    private string Describe(Exception ex, string recipient)
    {
        var reason = ex switch
        {
            AuthenticationException => "máy chủ từ chối tên đăng nhập hoặc mật khẩu SMTP (Gmail cần mật khẩu ứng dụng 16 ký tự)",
            SmtpCommandException cmd => $"máy chủ từ chối thư (mã {(int)cmd.StatusCode})",
            SslHandshakeException => "không thiết lập được kết nối mã hóa TLS",
            ServiceNotConnectedException or System.Net.Sockets.SocketException or IOException => "không kết nối được tới máy chủ",
            TimeoutException or OperationCanceledException => $"quá {_smtp.TimeoutSeconds} giây không có phản hồi",
            _ => "lỗi không xác định",
        };
        return $"Không gửi được thư qua SMTP {_smtp.Host}:{_smtp.Port} tới {recipient}: {reason}.";
    }
}

/// <summary>Gửi thư qua SMTP không thành công; thông điệp không chứa nội dung thư.</summary>
public sealed class EmailDeliveryException(string message, Exception innerException) : Exception(message, innerException);
