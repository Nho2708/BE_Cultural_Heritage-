using System.Net;

namespace DSVHVN.Tests.Integration;

/// <summary>Api có giới hạn rộng và một địa chỉ nhận thêm, để các test không chạm giới hạn 5 lần mỗi 10 phút.</summary>
public sealed class SchoolInquiryApiFactory : ApiFactory
{
    public const string ExtraRecipient = "nhom.vh@example.vn";

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
    [
        new("RateLimiting:SchoolInquiryPermits", "1000"),
        new("SchoolInquiry:Recipients:0", ExtraRecipient),
    ];
}

/// <summary>Liên hệ của trường qua HTTP: không cần đăng nhập, gửi tới quản trị hệ thống, kiểm hợp lệ, ô bẫy, lỗi thư.</summary>
public sealed class SchoolInquiryApiTests(SchoolInquiryApiFactory factory) : IClassFixture<SchoolInquiryApiFactory>
{
    private const string Url = "/api/v1/public/school-inquiries";
    private readonly HttpClient _http = factory.CreateClient();

    private static object Body(string school = "Trường THCS Nguyễn Du", string email = "hanh@nguyendu.edu.vn", string? website = null) => new
    {
        schoolName = school,
        province = "Hà Nội",
        contactName = "Trần Thị Hạnh",
        position = "Phó hiệu trưởng",
        email,
        phone = "0912 345 678",
        message = "Trường em muốn được tạo tài khoản.",
        website,
    };

    [Fact]
    public async Task Anonymous_submit_returns_202_and_mails_the_admin_and_the_configured_address_but_not_the_sender()
    {
        var school = "Trường Thử " + Guid.NewGuid().ToString("N")[..6];
        var sender = $"nguoi.gui.{Guid.NewGuid():N}@truong.edu.vn";

        var (res, body) = await _http.PostAsync<object>(Url, Body(school, sender));

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.True(body.Success);
        Assert.Equal("Đã gửi. Nhóm sẽ liên hệ lại qua email.", body.Message);

        var toAdmin = factory.Mail.Sent.Last(m => m.To == ApiFactory.AdminEmail && m.Subject.Contains(school));
        Assert.Equal($"DiViet – Trường liên hệ: {school}", toAdmin.Subject);
        Assert.Equal(sender, toAdmin.ReplyTo);
        Assert.Contains(sender, toAdmin.TextBody);
        Assert.Contains(factory.Mail.Sent, m => m.To == SchoolInquiryApiFactory.ExtraRecipient && m.Subject.Contains(school));
        Assert.Equal(0, factory.Mail.CountTo(sender));
    }

    [Fact]
    public async Task Invalid_input_returns_400_with_vietnamese_field_errors_and_sends_nothing()
    {
        var before = factory.Mail.Sent.Count;

        var (res, body) = await _http.PostAsync<object>(Url, new { schoolName = " ", contactName = "", email = "sai", message = "" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.False(body.Success);
        Assert.Equal("Vui lòng nhập tên trường.", body.Errors!["schoolName"]);
        Assert.Equal("Vui lòng nhập họ tên người liên hệ.", body.Errors["contactName"]);
        Assert.Equal("Email không hợp lệ: sai định dạng hoặc dài quá 255 ký tự.", body.Errors["email"]);
        Assert.Equal("Vui lòng nhập nội dung.", body.Errors["message"]);
        Assert.Equal(before, factory.Mail.Sent.Count);
    }

    [Fact]
    public async Task A_filled_trap_field_returns_202_without_sending()
    {
        var school = "Trường Rác " + Guid.NewGuid().ToString("N")[..6];

        var (res, _) = await _http.PostAsync<object>(Url, Body(school, website: "http://thu-rac.example"));

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.DoesNotContain(factory.Mail.Sent, m => m.Subject.Contains(school));
    }

    [Fact]
    public async Task A_mail_server_failure_returns_503_with_a_vietnamese_message()
    {
        factory.Mail.FailWith = new InvalidOperationException("kết nối SMTP bị từ chối: mat-khau-bi-mat");
        try
        {
            var (res, body) = await _http.PostAsync<object>(Url, Body());

            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            Assert.Equal("SCHOOL_INQUIRY_UNAVAILABLE", body.ErrorCode);
            Assert.Equal("Chưa gửi được, vui lòng thử lại sau.", body.Message);
            Assert.DoesNotContain("mat-khau-bi-mat", body.Message);
        }
        finally
        {
            factory.Mail.FailWith = null;
        }
    }
}

/// <summary>Giới hạn riêng của biểu mẫu: 5 lần mỗi 10 phút mỗi IP, lần thứ sáu bị 429 kèm Retry-After.</summary>
public sealed class SchoolInquiryRateLimitApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task The_sixth_submit_in_the_window_is_refused_with_429_and_retry_after()
    {
        var http = factory.CreateClient();
        var body = new { schoolName = "Trường A", contactName = "An", email = "an@truong.edu.vn", message = "Xin chào" };

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsync<object>("/api/v1/public/school-inquiries", body)).Response.StatusCode);

        var (res, refused) = await http.PostAsync<object>("/api/v1/public/school-inquiries", body);

        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.Equal("TOO_MANY_REQUESTS", refused.ErrorCode);
        Assert.Matches(@"^Có quá nhiều yêu cầu .* thử lại sau \d+ giây\.$", refused.Message);
        Assert.NotNull(res.Headers.RetryAfter);

        // Các API công khai khác không bị ảnh hưởng.
        var (home, _) = await http.GetAsync<object>("/api/v1/public/provinces");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
    }
}
