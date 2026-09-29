using DSVHVN.Application.Common;
using Microsoft.Extensions.Logging;

namespace DSVHVN.Infrastructure.Email;

/// <summary>
/// Chế độ <c>Email:Mode = Log</c> (mặc định): không gửi thư, chỉ ghi bản chữ thuần của thư ra log, kể cả liên kết có token,
/// để máy phát triển và smoke test lấy liên kết mà không cần máy chủ SMTP. Không dùng ở môi trường có người dùng thật.
/// </summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[Email chế độ Log, không gửi đi] Tới: {To} | Tiêu đề: {Subject}{NewLine}{Body}",
            message.To, message.Subject, Environment.NewLine, message.TextBody);
        return Task.CompletedTask;
    }
}
