using DSVHVN.Application.Common;
using DSVHVN.Application.SchoolInquiries;
using DSVHVN.Domain.Enums;
using DSVHVN.Infrastructure.Email;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Unit;

/// <summary>Liên hệ của trường: kiểm hợp lệ, ô bẫy, chọn người nhận, soạn thư và các lỗi gửi thư.</summary>
public sealed class SchoolInquiryServiceTests : IDisposable
{
    private readonly ServiceHarness _h = new();
    private readonly RecordingMailer _mailer = new();

    public void Dispose() => _h.Dispose();

    private static SchoolInquiryRequest Valid(Action<Dictionary<string, string?>>? change = null)
    {
        var f = new Dictionary<string, string?>
        {
            ["school"] = "Trường THCS Nguyễn Du",
            ["province"] = "Hà Nội",
            ["name"] = "Trần Thị Hạnh",
            ["position"] = "Phó hiệu trưởng",
            ["email"] = "Hanh@NguyenDu.edu.vn",
            ["phone"] = "0912 345 678",
            ["message"] = "Trường em muốn dùng thử.",
            ["website"] = null,
        };
        change?.Invoke(f);
        return new SchoolInquiryRequest(f["school"], f["province"], f["name"], f["position"], f["email"], f["phone"], f["message"], f["website"]);
    }

    private Task SubmitAsync(SchoolInquiryRequest request) =>
        _h.RunAsync(sp => new SchoolInquiryService(sp.GetRequiredService<IAppDbContext>(), sp.GetRequiredService<RequestValidator>(), _mailer,
            NullLogger<SchoolInquiryService>.Instance).SubmitAsync(request, default));

    [Fact]
    public async Task A_valid_request_sends_a_cleaned_inquiry_to_active_system_admins_only()
    {
        var org = await _h.AddOrganizationAsync();
        await _h.AddUserAsync("quantri.mot", RoleCode.ADMIN, null);
        await _h.AddUserAsync("quantri.hai", RoleCode.ADMIN, null);
        await _h.AddUserAsync("quantri.khoa", RoleCode.ADMIN, null, status: UserStatus.LOCKED);
        await _h.AddUserAsync("quantri.truong", RoleCode.ORG_ADMIN, org.Id);
        var gone = await _h.AddUserAsync("quantri.xoa", RoleCode.ADMIN, null);
        await _h.QueryAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == gone.Id)).DeletedAt = _h.Clock.GetUtcNow().UtcDateTime;
            return await db.SaveChangesAsync();
        });

        await SubmitAsync(Valid(f =>
        {
            f["school"] = "  Trường\r\n  Nguyễn   Du ";
            f["message"] = "  Dòng một\nDòng hai  ";
        }));

        var sent = Assert.Single(_mailer.Calls);
        Assert.Equal(["quantri.hai@example.vn", "quantri.mot@example.vn"], sent.Admins.Order().ToArray());
        Assert.Equal("Trường Nguyễn Du", sent.Inquiry.SchoolName);
        Assert.Equal("hanh@nguyendu.edu.vn", sent.Inquiry.Email);
        Assert.Equal("Dòng một\nDòng hai", sent.Inquiry.Message);
    }

    [Fact]
    public async Task Optional_fields_may_be_left_out()
    {
        await _h.AddUserAsync("quantri.mot", RoleCode.ADMIN, null);

        await SubmitAsync(Valid(f =>
        {
            f["province"] = null;
            f["position"] = " ";
            f["phone"] = "";
        }));

        var inquiry = Assert.Single(_mailer.Calls).Inquiry;
        Assert.Null(inquiry.Province);
        Assert.Null(inquiry.Position);
        Assert.Null(inquiry.Phone);
    }

    [Theory]
    [InlineData("school", "", "schoolName")]
    [InlineData("name", "   ", "contactName")]
    [InlineData("email", "", "email")]
    [InlineData("email", "khong-phai-email", "email")]
    [InlineData("message", null, "message")]
    [InlineData("phone", "abc", "phone")]
    public async Task Invalid_fields_return_400_with_the_field_error_and_send_nothing(string key, string? value, string field)
    {
        await _h.AddUserAsync("quantri.mot", RoleCode.ADMIN, null);

        var ex = await Assert.ThrowsAsync<AppException>(() => SubmitAsync(Valid(f => f[key] = value)));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains(field, ex.Errors!.Keys);
        Assert.Empty(_mailer.Calls);
    }

    [Fact]
    public async Task The_message_is_limited_to_2000_characters()
    {
        await _h.AddUserAsync("quantri.mot", RoleCode.ADMIN, null);

        await SubmitAsync(Valid(f => f["message"] = new string('a', 2000)));
        var ex = await Assert.ThrowsAsync<AppException>(() => SubmitAsync(Valid(f => f["message"] = new string('a', 2001))));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("Nội dung không hợp lệ: tối đa 2000 ký tự.", ex.Errors!["message"]);
        Assert.Single(_mailer.Calls);
    }

    [Fact]
    public async Task A_filled_trap_field_is_accepted_silently_without_sending_even_when_other_fields_are_wrong()
    {
        await _h.AddUserAsync("quantri.mot", RoleCode.ADMIN, null);

        await SubmitAsync(Valid(f =>
        {
            f["website"] = "http://thu-rac.example";
            f["email"] = "sai";
        }));

        Assert.Empty(_mailer.Calls);
    }

    [Fact]
    public async Task No_recipient_at_all_returns_503_with_a_clear_message()
    {
        _mailer.Delivery = new SchoolInquiryDelivery(0, 0);

        var ex = await Assert.ThrowsAsync<AppException>(() => SubmitAsync(Valid()));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("Chưa gửi được, vui lòng thử lại sau.", ex.Message);
        Assert.Equal(MessageCodes.SchoolInquiryUnavailable, ex.AppMessage.Code);
    }

    [Fact]
    public async Task A_send_that_reaches_nobody_or_throws_returns_503_and_a_partial_send_succeeds()
    {
        _mailer.Delivery = new SchoolInquiryDelivery(2, 0);
        Assert.Equal(503, (await Assert.ThrowsAsync<AppException>(() => SubmitAsync(Valid()))).StatusCode);

        _mailer.Failure = new InvalidOperationException("SMTP down");
        Assert.Equal(503, (await Assert.ThrowsAsync<AppException>(() => SubmitAsync(Valid()))).StatusCode);

        _mailer.Failure = null;
        _mailer.Delivery = new SchoolInquiryDelivery(2, 1);
        await SubmitAsync(Valid());
    }

    private sealed class RecordingMailer : ISchoolInquiryMailer
    {
        public List<(SchoolInquiry Inquiry, string[] Admins)> Calls { get; } = [];
        public SchoolInquiryDelivery Delivery { get; set; } = new(1, 1);
        public Exception? Failure { get; set; }

        public Task<SchoolInquiryDelivery> SendAsync(SchoolInquiry inquiry, IReadOnlyCollection<string> adminEmails, CancellationToken cancellationToken = default)
        {
            if (Failure is { } failure) throw failure;
            Calls.Add((inquiry, adminEmails.ToArray()));
            return Task.FromResult(Delivery);
        }
    }
}

public sealed class SchoolInquiryMailerTests
{
    private static readonly SchoolInquiry Inquiry = new("Trường <b>A</b> & \"B\"", "Hà Nội", "Trần <script>x</script>", null, "hanh@truong.edu.vn", null,
        "Dòng một\nDòng <img src=x onerror=alert(1)> hai");

    private static SchoolInquiryMailer MailerFor(IEmailSender sender, params string[] extra) =>
        new(sender, Options.Create(new SchoolInquiryOptions { Recipients = extra }), TimeProvider.System, NullLogger<SchoolInquiryMailer>.Instance);

    [Fact]
    public void Letter_has_the_subject_all_fields_and_the_sender_as_reply_address()
    {
        var mail = SchoolInquiryEmailComposer.Compose(Inquiry, "admin@vh.vn", new DateTime(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc));

        Assert.Equal("admin@vh.vn", mail.To);
        Assert.Equal("DiViet – Trường liên hệ: Trường <b>A</b> & \"B\"", mail.Subject);
        Assert.Equal("hanh@truong.edu.vn", mail.ReplyTo);
        Assert.Contains("nhận lúc 09:00 ngày 01/10/2026", mail.TextBody);
        Assert.Contains("Tên trường: Trường <b>A</b> & \"B\"", mail.TextBody);
        Assert.Contains("Chức vụ: (không nhập)", mail.TextBody);
        Assert.Contains("Dòng một\nDòng <img src=x onerror=alert(1)> hai", mail.TextBody);
    }

    [Fact]
    public void Everything_the_user_typed_is_html_encoded_in_the_html_body()
    {
        var mail = SchoolInquiryEmailComposer.Compose(Inquiry, "admin@vh.vn", DateTime.UtcNow);

        Assert.DoesNotContain("<script>", mail.HtmlBody);
        Assert.DoesNotContain("<img", mail.HtmlBody);
        Assert.DoesNotContain("<b>A</b>", mail.HtmlBody);
        Assert.Contains("Trường &lt;b&gt;A&lt;/b&gt; &amp; &quot;B&quot;", mail.HtmlBody);
        Assert.Contains("Trần &lt;script&gt;x&lt;/script&gt;", mail.HtmlBody);
        Assert.Contains("Dòng một<br>\nDòng &lt;img src=x onerror=alert(1)&gt; hai", mail.HtmlBody);
    }

    [Fact]
    public async Task Each_distinct_valid_recipient_gets_one_letter_and_nothing_goes_back_to_the_sender()
    {
        var sender = new CapturingEmailSender();

        var result = await MailerFor(sender, "Chung@VH.vn", "khong-hop-le", "admin@vh.vn")
            .SendAsync(Inquiry, ["admin@vh.vn", "Quan.Tri@vh.vn"]);

        Assert.Equal(new SchoolInquiryDelivery(3, 3), result);
        Assert.Equal(["admin@vh.vn", "chung@vh.vn", "quan.tri@vh.vn"], sender.Sent.Select(m => m.To).Order().ToArray());
        Assert.Equal(0, sender.CountTo("hanh@truong.edu.vn"));
    }

    [Fact]
    public async Task Local_domain_recipients_are_left_out()
    {
        var sender = new CapturingEmailSender();

        var result = await MailerFor(sender, "chung@vh.vn")
            .SendAsync(Inquiry, ["nho.admin@dsvhvn.local", "lam.admin@dsvhvn.local"]);

        Assert.Equal(new SchoolInquiryDelivery(1, 1), result);
        Assert.Equal(["chung@vh.vn"], sender.Sent.Select(m => m.To).ToArray());
    }

    [Fact]
    public async Task Only_local_domain_admins_means_no_letters()
    {
        var sender = new CapturingEmailSender();

        var result = await MailerFor(sender).SendAsync(Inquiry, ["nho.admin@dsvhvn.local"]);

        Assert.Equal(new SchoolInquiryDelivery(0, 0), result);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task A_failure_for_one_recipient_does_not_stop_the_others()
    {
        var sender = new FlakySender("loi@vh.vn");

        var result = await MailerFor(sender).SendAsync(Inquiry, ["loi@vh.vn", "tot@vh.vn"]);

        Assert.Equal(new SchoolInquiryDelivery(2, 1), result);
        Assert.Equal(["tot@vh.vn"], sender.Delivered);
    }

    [Fact]
    public async Task No_recipients_means_no_letters()
    {
        var sender = new CapturingEmailSender();

        var result = await MailerFor(sender).SendAsync(Inquiry, []);

        Assert.Equal(new SchoolInquiryDelivery(0, 0), result);
        Assert.Empty(sender.Sent);
    }

    private sealed class FlakySender(string failingAddress) : IEmailSender
    {
        public List<string> Delivered { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            if (message.To == failingAddress) throw new InvalidOperationException("máy chủ thư từ chối");
            Delivered.Add(message.To);
            return Task.CompletedTask;
        }
    }
}
