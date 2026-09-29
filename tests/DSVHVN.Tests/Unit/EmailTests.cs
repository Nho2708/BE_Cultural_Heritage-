using System.Net;
using System.Net.Sockets;
using System.Text;
using DSVHVN.Application.Accounts;
using DSVHVN.Application.Auth;
using DSVHVN.Application.Common;
using DSVHVN.Application.Organizations;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure;
using DSVHVN.Infrastructure.Email;
using DSVHVN.Tests.Support;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DSVHVN.Tests.Unit;

/// <summary>Soạn thư đặt mật khẩu (chữ thuần + HTML), chọn bản gửi theo <c>Email:Mode</c>, kiểm cấu hình SMTP, dựng thư MIME.</summary>
public sealed class EmailContentTests
{
    private const string Token = "Abc_123-xyz";

    // 02:00 UTC ngày 01/10/2026 = 09:00 giờ Việt Nam.
    private static readonly DateTime ExpiresAt = new(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc);

    private static readonly AccountEmailComposer Composer = new(new AppLinkOptions
    {
        FrontendBaseUrl = "https://quantri.dsvhvn.test",
        SetPasswordPath = "/dat-mat-khau",
    });

    [Fact]
    public void First_password_letter_greets_by_name_and_carries_link_lifetime_and_ignore_note()
    {
        var mail = Composer.FirstPasswordLink("hanh@truong.edu.vn", "Trần Thị Hạnh", "hanh.tran",
            "Trường Tiểu học Lê Lợi", Token, ExpiresAt);

        const string link = "https://quantri.dsvhvn.test/dat-mat-khau?token=Abc_123-xyz";
        Assert.Equal("hanh@truong.edu.vn", mail.To);
        Assert.Equal("Trần Thị Hạnh", mail.ToName);
        Assert.Equal("Đặt mật khẩu cho tài khoản DSVHVN của bạn", mail.Subject);

        Assert.StartsWith("Xin chào Trần Thị Hạnh,", mail.TextBody);
        Assert.Contains("Tài khoản DSVHVN của Trường Tiểu học Lê Lợi đã được tạo cho bạn. Tên đăng nhập: hanh.tran.", mail.TextBody);
        Assert.Contains(link, mail.TextBody);
        Assert.Contains("Liên kết có hiệu lực 48 giờ (đến 09:00 ngày 01/10/2026) và chỉ dùng được một lần.", mail.TextBody);
        Assert.Contains("Nếu bạn không yêu cầu, hãy bỏ qua thư này.", mail.TextBody);

        Assert.Contains("<html lang=\"vi\">", mail.HtmlBody);
        Assert.Contains("<meta charset=\"utf-8\">", mail.HtmlBody);
        Assert.Contains("Xin chào <strong>Trần Thị Hạnh</strong>,", mail.HtmlBody);
        Assert.Contains("Tên đăng nhập: <strong>hanh.tran</strong>.", mail.HtmlBody);
        Assert.Contains($"<a href=\"{link}\"", mail.HtmlBody);
        Assert.Contains(">Đặt mật khẩu</a>", mail.HtmlBody);
        Assert.Contains("48 giờ (đến 09:00 ngày 01/10/2026)", mail.HtmlBody);
        Assert.Contains("Nếu bạn không yêu cầu, hãy bỏ qua thư này.", mail.HtmlBody);
    }

    [Fact]
    public void Reset_letter_uses_30_minute_lifetime_and_ignore_note()
    {
        var mail = Composer.ResetPasswordLink("an@truong.edu.vn", "Nguyễn Văn An", Token, ExpiresAt);

        Assert.Equal("Đặt lại mật khẩu tài khoản DSVHVN", mail.Subject);
        Assert.StartsWith("Xin chào Nguyễn Văn An,", mail.TextBody);
        Assert.Contains("Bạn vừa yêu cầu đặt lại mật khẩu tài khoản DSVHVN.", mail.TextBody);
        Assert.Contains("https://quantri.dsvhvn.test/dat-mat-khau?token=Abc_123-xyz", mail.TextBody);
        Assert.Contains("Liên kết có hiệu lực 30 phút (đến 09:00 ngày 01/10/2026)", mail.TextBody);
        Assert.Contains("Nếu bạn không yêu cầu, hãy bỏ qua thư này", mail.TextBody);
        Assert.Contains(">Đặt lại mật khẩu</a>", mail.HtmlBody);
        Assert.Contains("Nếu bạn không yêu cầu, hãy bỏ qua thư này", mail.HtmlBody);
    }

    [Fact]
    public void Link_joins_frontend_address_and_path_and_escapes_token()
    {
        var composer = new AccountEmailComposer(new AppLinkOptions
        {
            FrontendBaseUrl = "https://hoc.dsvhvn.test/", SetPasswordPath = "dat-mat-khau",
        });

        Assert.Equal("https://hoc.dsvhvn.test/dat-mat-khau?token=a%2Bb%2Fc%3D", composer.Link("a+b/c="));
    }

    [Fact]
    public void Organization_name_is_left_out_when_absent()
    {
        var mail = Composer.FirstPasswordLink("x@truong.edu.vn", "Lê Minh", "le.minh", null, Token, ExpiresAt);

        Assert.Contains("Tài khoản DSVHVN đã được tạo cho bạn.", mail.TextBody);
    }

    [Fact]
    public void Html_escapes_values_typed_by_people_but_text_keeps_them()
    {
        var mail = Composer.ResetPasswordLink("x@truong.edu.vn", "<b>An & \"Bình\"</b>", Token, ExpiresAt);

        Assert.Contains("Xin chào <strong>&lt;b&gt;An &amp; &quot;Bình&quot;&lt;/b&gt;</strong>,", mail.HtmlBody);
        Assert.DoesNotContain("<b>An", mail.HtmlBody);
        Assert.StartsWith("Xin chào <b>An & \"Bình\"</b>,", mail.TextBody);
    }

    [Fact]
    public void Missing_full_name_falls_back_to_a_neutral_greeting()
    {
        var mail = Composer.ResetPasswordLink("x@truong.edu.vn", "  ", Token, ExpiresAt);

        Assert.Null(mail.ToName);
        Assert.StartsWith("Xin chào bạn,", mail.TextBody);
    }

    [Theory]
    [InlineData(48 * 60, "48 giờ")]
    [InlineData(60, "1 giờ")]
    [InlineData(30, "30 phút")]
    [InlineData(90, "90 phút")]
    public void Lifetime_reads_in_hours_or_minutes(int minutes, string expected) =>
        Assert.Equal(expected, AccountEmailComposer.Lifetime(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Lifetimes_in_letters_follow_account_rules()
    {
        Assert.Equal("48 giờ", AccountEmailComposer.Lifetime(AccountRules.FirstPasswordLinkLifetime));
        Assert.Equal("30 phút", AccountEmailComposer.Lifetime(AccountRules.ForgotPasswordLinkLifetime));
    }

    [Fact]
    public void Mime_message_has_sender_name_recipient_and_both_text_and_html_parts()
    {
        var mail = Composer.FirstPasswordLink("hanh@truong.edu.vn", "Trần Thị Hạnh", "hanh.tran", null, Token, ExpiresAt);
        var smtp = new SmtpOptions { FromAddress = "dsvhvn.thu@gmail.com" };

        var mime = SmtpEmailSender.CreateMimeMessage(mail, smtp);

        var from = Assert.Single(mime.From.Mailboxes);
        Assert.Equal(("DSVHVN — Nền tảng học di sản", "dsvhvn.thu@gmail.com"), (from.Name, from.Address));
        var to = Assert.Single(mime.To.Mailboxes);
        Assert.Equal(("Trần Thị Hạnh", "hanh@truong.edu.vn"), (to.Name, to.Address));
        Assert.Equal(mail.Subject, mime.Subject);
        Assert.IsType<MultipartAlternative>(mime.Body);
        // MimeKit đổi xuống dòng thành CRLF theo chuẩn thư.
        Assert.Equal(mail.TextBody, mime.TextBody!.ReplaceLineEndings("\n"));
        Assert.Equal(mail.HtmlBody, mime.HtmlBody!.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData(true, 587, SecureSocketOptions.StartTls)]
    [InlineData(true, 25, SecureSocketOptions.StartTls)]
    [InlineData(false, 465, SecureSocketOptions.SslOnConnect)]
    [InlineData(false, 25, SecureSocketOptions.StartTlsWhenAvailable)]
    public void Socket_security_follows_start_tls_setting_and_port(bool useStartTls, int port, SecureSocketOptions expected) =>
        Assert.Equal(expected, SmtpEmailSender.SocketOptionsFor(new SmtpOptions { UseStartTls = useStartTls, Port = port }));

    [Theory]
    [InlineData("quangnho278@gmail.com", "qu***@gmail.com")]
    [InlineData("ab@truong.edu.vn", "a***@truong.edu.vn")]
    [InlineData("a@truong.edu.vn", "***@truong.edu.vn")]
    [InlineData("khong-phai-email", "***")]
    public void Recipient_is_masked_in_logs(string address, string expected) =>
        Assert.Equal(expected, SmtpEmailSender.MaskAddress(address));

    [Fact]
    public async Task Log_mode_writes_the_link_so_developers_need_no_smtp()
    {
        var logger = new ListLogger<LogEmailSender>();
        var mail = Composer.ResetPasswordLink("an@truong.edu.vn", "Nguyễn Văn An", Token, ExpiresAt);

        await new LogEmailSender(logger).SendAsync(mail);

        var entry = Assert.Single(logger.Messages);
        Assert.Contains("Tới: an@truong.edu.vn | Tiêu đề: Đặt lại mật khẩu tài khoản DSVHVN", entry);
        Assert.Contains("token=" + Token, entry);
    }
}

/// <summary>Chọn bản gửi theo <c>Email:Mode</c> và dừng khởi động khi chế độ SMTP thiếu cấu hình bắt buộc.</summary>
public sealed class EmailConfigurationTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> Gmail(string? password = "abcdefghijklmnop") => new()
    {
        ["Email:Mode"] = "Smtp",
        ["Email:Smtp:Host"] = "smtp.gmail.com",
        ["Email:Smtp:Username"] = "dsvhvn.thu@gmail.com",
        ["Email:Smtp:Password"] = password,
        ["Email:Smtp:FromAddress"] = "dsvhvn.thu@gmail.com",
    };

    [Fact]
    public void Without_email_section_the_log_sender_is_used()
    {
        using var sp = Build([]);

        Assert.IsType<LogEmailSender>(sp.GetRequiredService<IEmailSender>());
        Assert.Equal(EmailMode.Log, sp.GetRequiredService<IOptions<EmailOptions>>().Value.Mode);
    }

    [Fact]
    public void Log_mode_ignores_empty_smtp_settings()
    {
        using var sp = Build(new() { ["Email:Mode"] = "Log", ["Email:Smtp:Host"] = "" });

        Assert.IsType<LogEmailSender>(sp.GetRequiredService<IEmailSender>());
    }

    [Theory]
    [InlineData("Smtp")]
    [InlineData("smtp")]
    public void Smtp_mode_with_full_settings_uses_the_smtp_sender(string mode)
    {
        var settings = Gmail();
        settings["Email:Mode"] = mode;
        using var sp = Build(settings);

        Assert.IsType<SmtpEmailSender>(sp.GetRequiredService<IEmailSender>());
        var smtp = sp.GetRequiredService<IOptions<EmailOptions>>().Value.Smtp;
        Assert.Equal((587, true, "DSVHVN — Nền tảng học di sản", 30), (smtp.Port, smtp.UseStartTls, smtp.FromName, smtp.TimeoutSeconds));
    }

    [Fact]
    public void Smtp_mode_without_password_stops_with_a_clear_message()
    {
        using var sp = Build(Gmail(password: null));

        var ex = Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IEmailSender>());
        Assert.Contains("Email:Smtp:Password", ex.Message);
        Assert.Contains("Email__Smtp__Password", ex.Message);
    }

    [Fact]
    public void Smtp_mode_lists_every_missing_setting()
    {
        using var sp = Build(new() { ["Email:Mode"] = "Smtp" });

        var ex = Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IEmailSender>());
        Assert.Contains("Email:Smtp:Host", ex.Message);
        Assert.Contains("Email:Smtp:FromAddress", ex.Message);
    }

    [Fact]
    public void Smtp_mode_rejects_invalid_sender_address_and_port()
    {
        var settings = Gmail();
        settings["Email:Smtp:FromAddress"] = "khong-phai-email";
        settings["Email:Smtp:Port"] = "70000";
        using var sp = Build(settings);

        var ex = Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IEmailSender>());
        Assert.Contains("không phải địa chỉ email hợp lệ", ex.Message);
        Assert.Contains("Email:Smtp:Port", ex.Message);
    }

    [Fact]
    public void Smtp_mode_without_login_is_allowed_for_local_test_servers()
    {
        using var sp = Build(new()
        {
            ["Email:Mode"] = "Smtp", ["Email:Smtp:Host"] = "localhost", ["Email:Smtp:Port"] = "2525",
            ["Email:Smtp:UseStartTls"] = "false", ["Email:Smtp:FromAddress"] = "khong-tra-loi@dsvhvn.test",
        });

        Assert.IsType<SmtpEmailSender>(sp.GetRequiredService<IEmailSender>());
    }
}

/// <summary>
/// Bản gửi SMTP chạy với một máy chủ SMTP giả trên cổng loopback của máy (không ra mạng): thư đi đủ hai phần,
/// log không chứa token, máy chủ từ chối thì ném lỗi gửi thư có mô tả rõ.
/// </summary>
public sealed class SmtpEmailSenderTests
{
    private static readonly AccountEmailComposer Composer = new(new AppLinkOptions());

    private static SmtpEmailSender Sender(int port, ListLogger<SmtpEmailSender> logger) =>
        new(Options.Create(new EmailOptions
        {
            Mode = EmailMode.Smtp,
            Smtp = new SmtpOptions
            {
                Host = "127.0.0.1", Port = port, UseStartTls = false, FromAddress = "khong-tra-loi@dsvhvn.test", TimeoutSeconds = 5,
            },
        }), logger);

    [Fact]
    public async Task Sends_both_parts_and_never_logs_the_token()
    {
        await using var server = new FakeSmtpServer();
        var logger = new ListLogger<SmtpEmailSender>();
        const string token = "BiMat_Token-0123456789";
        var mail = Composer.FirstPasswordLink("hanh@truong.edu.vn", "Trần Thị Hạnh", "hanh.tran", "Trường Tiểu học Lê Lợi",
            token, new DateTime(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc));

        await Sender(server.Port, logger).SendAsync(mail).WaitAsync(TimeSpan.FromSeconds(15));

        var received = await server.ReceivedAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("Đặt mật khẩu cho tài khoản DSVHVN của bạn", received.Subject);
        Assert.Equal("hanh@truong.edu.vn", Assert.Single(received.To.Mailboxes).Address);
        Assert.Contains("token=" + token, received.TextBody);
        Assert.Contains("Xin chào <strong>Trần Thị Hạnh</strong>", received.HtmlBody);

        Assert.Contains(logger.Messages, m => m.Contains("Đã gửi thư qua SMTP tới ha***@truong.edu.vn", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, m => m.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rejected_recipient_raises_a_delivery_error_without_the_token()
    {
        await using var server = new FakeSmtpServer(rejectRecipients: true);
        var logger = new ListLogger<SmtpEmailSender>();
        const string token = "BiMat_Token-0123456789";
        var mail = Composer.ResetPasswordLink("khongco@truong.edu.vn", "Nguyễn Văn An", token, DateTime.UtcNow);

        var ex = await Assert.ThrowsAsync<EmailDeliveryException>(
            () => Sender(server.Port, logger).SendAsync(mail).WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Contains("127.0.0.1", ex.Message);
        Assert.Contains("kh***@truong.edu.vn", ex.Message);
        Assert.Contains("mã 550", ex.Message);
        Assert.DoesNotContain(token, ex.Message);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>Máy chủ SMTP tối thiểu cho một kết nối: không TLS, không đăng nhập, giữ lại phần DATA.</summary>
    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<MimeMessage> _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _rejectRecipients;
        private readonly Task _loop;

        public FakeSmtpServer(bool rejectRecipients = false)
        {
            _rejectRecipients = rejectRecipients;
            _listener.Start();
            _loop = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Task<MimeMessage> ReceivedAsync() => _received.Task;

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.Latin1);
                await using var writer = new StreamWriter(stream, Encoding.Latin1) { NewLine = "\r\n", AutoFlush = true };

                await writer.WriteLineAsync("220 localhost SMTP thu nghiem");
                while (await reader.ReadLineAsync() is { } line)
                {
                    var command = line.Split(' ', 2)[0].ToUpperInvariant();
                    switch (command)
                    {
                        case "EHLO":
                            await writer.WriteLineAsync("250 localhost");
                            break;
                        case "RCPT" when _rejectRecipients:
                            await writer.WriteLineAsync("550 5.1.1 Mailbox unavailable");
                            break;
                        case "DATA":
                            await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                            var data = new StringBuilder();
                            while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                                data.Append(dataLine.StartsWith("..", StringComparison.Ordinal) ? dataLine[1..] : dataLine).Append("\r\n");
                            await writer.WriteLineAsync("250 OK");
                            using (var buffer = new MemoryStream(Encoding.Latin1.GetBytes(data.ToString())))
                                _received.TrySetResult(await MimeMessage.LoadAsync(buffer));
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 Bye");
                            return;
                        default:
                            await writer.WriteLineAsync("250 OK");
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                // Bên gửi ngắt kết nối sau khi bị từ chối.
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _loop;
        }
    }
}

/// <summary>Lỗi gửi thư không làm hỏng thao tác đã lưu; người dùng lấy liên kết mới ở nhánh quên mật khẩu.</summary>
public sealed class EmailFailureTests : IDisposable
{
    private readonly ServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Organization_and_org_admin_are_kept_when_the_first_password_letter_fails()
    {
        var admin = await _h.AddUserAsync("admin.test", RoleCode.ADMIN, null);
        var actor = new Actor(admin.Id, RoleCode.ADMIN, null, "203.0.113.5");
        _h.Mail.FailWith = new EmailDeliveryException("Không gửi được thư qua SMTP", new IOException("mất mạng"));

        var created = await _h.Service<OrganizationService, Created<OrganizationDetailDto>>(s => s.CreateAsync(
            new CreateOrganizationRequest("Trường THCS Nguyễn Du", null, null, null,
                new NewAccountRequest("Phạm Thu", "pham.thu", "thu@nguyendu.edu.vn", null)), actor, default));

        Assert.Equal("thu@nguyendu.edu.vn", created.NotifiedEmail);
        Assert.True(await _h.QueryAsync(db => db.Users.AnyAsync(u => u.Username == "pham.thu")));
        Assert.Equal(0, _h.Mail.CountTo("thu@nguyendu.edu.vn"));

        _h.Mail.FailWith = null;
        await _h.Service<AuthService>(s => s.ForgotPasswordAsync(new ForgotPasswordRequest("thu@nguyendu.edu.vn"), default));
        Assert.NotNull(_h.Mail.LastTokenFor("thu@nguyendu.edu.vn"));
    }

    [Fact]
    public async Task Forgot_password_answers_the_same_when_the_letter_fails()
    {
        var org = await _h.AddOrganizationAsync();
        await _h.AddUserAsync("giaovien.a", RoleCode.TEACHER, org.Id);
        _h.Mail.FailWith = new EmailDeliveryException("Không gửi được thư qua SMTP", new IOException("mất mạng"));

        await _h.Service<AuthService>(s => s.ForgotPasswordAsync(new ForgotPasswordRequest("giaovien.a@example.vn"), default));

        Assert.Equal(0, _h.Mail.CountTo("giaovien.a@example.vn"));
    }
}

/// <summary>Logger giữ lại các dòng log đã định dạng để kiểm nội dung.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Messages) Messages.Add(formatter(state, exception));
    }
}
