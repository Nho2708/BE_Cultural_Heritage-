using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.StudentAuth;
using DSVHVN.Application.Students;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Học sinh bấm "Quên mật khẩu" qua Api thật: mã có thật và mã không có nhận cùng một câu trả lời (200, cùng thông điệp, cùng dữ liệu trung tính), thư báo thầy cô gửi ở nền
/// (yêu cầu không chờ SMTP), mã trống thì 400. Tối đa 3 yêu cầu mỗi mã mỗi giờ và 10 yêu cầu mỗi IP mỗi 15 phút, vượt thì 429 kèm Retry-After. Ba lớp kiểm thử riêng vì bộ đếm theo IP
/// dùng chung cho mọi test cùng một Api.
/// </summary>
internal static class ForgotPasswordKit
{
    public const string Url = "/api/v1/student/auth/forgot-password";

    public static Task<(HttpResponseMessage Response, Envelope<StudentForgotPasswordResultDto> Body)> AskAsync(HttpClient http, string code) =>
        http.PostAsync<StudentForgotPasswordResultDto>(Url, new { studentCode = code });

    public static async Task<(ClassroomClient.School School, LoginSheetEntryDto Entry)> StudentAsync(ClassroomClient client)
    {
        var school = await client.SchoolAsync();
        var schoolClass = await client.CreateClassAsync(school.TeacherToken, "8A1", 8);
        var created = await client.AddStudentAsync(school.TeacherToken, schoolClass.Id, "Trần Thị Bình");
        return (school, created.Sheet.Entries.Single());
    }
}

public sealed class StudentForgotPasswordApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly ClassroomClient _c = new(factory);

    [Fact]
    public async Task A_known_and_an_unknown_code_get_the_same_answer_and_only_the_known_one_mails_the_teacher_in_the_background()
    {
        var (school, entry) = await ForgotPasswordKit.StudentAsync(_c);
        var gate = new TaskCompletionSource();
        factory.Mail.Gate = gate.Task;   // SMTP chậm: thư chưa đi được cho tới khi mở cổng

        try
        {
            var (unknownRes, unknown) = await ForgotPasswordKit.AskAsync(_c.Http, "HSKHONGCO");
            var knownCall = ForgotPasswordKit.AskAsync(_c.Http, entry.StudentCode!.ToLowerInvariant());

            // Yêu cầu của mã có thật không chờ việc gửi thư.
            Assert.Same(knownCall, await Task.WhenAny(knownCall, Task.Delay(TimeSpan.FromSeconds(20))));
            var (knownRes, known) = await knownCall;

            Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (unknownRes.StatusCode, knownRes.StatusCode));
            Assert.Equal("Nếu mã học sinh đúng, thầy cô chủ nhiệm của em sẽ nhận được thông báo để đặt lại mật khẩu.", known.Message);
            Assert.Equal(known.Message, unknown.Message);
            Assert.True(known.Success);
            Assert.Equal(unknown.Data, known.Data);
            Assert.Equal(("Em", "thầy cô chủ nhiệm"), (known.Data!.StudentName, known.Data.TeacherName));
            Assert.DoesNotContain("Trần", known.Message);
        }
        finally
        {
            gate.SetResult();
        }

        // Thư tới giáo viên chủ nhiệm sau khi cổng mở; mã không có thì không có thư nào.
        var notices = await WaitForNoticesAsync(school.Teacher.Email!, entry.StudentCode!);
        Assert.Single(notices);
        Assert.Contains("Trần Thị Bình", notices[0].TextBody);
        Assert.DoesNotContain(factory.Mail.Sent, m => m.Subject.Contains("HSKHONGCO"));
        factory.Mail.Gate = null;
    }

    [Fact]
    public async Task An_empty_code_is_a_bad_request()
    {
        var (res, body) = await _c.Http.PostAsync<object>(ForgotPasswordKit.Url, new { studentCode = " " });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.True(body.Errors!.ContainsKey("studentCode"));
    }

    /// <summary>Thư báo học sinh xin đặt lại mật khẩu gửi tới giáo viên (nhận ra qua mã học sinh ở tiêu đề), chờ tối đa 10 giây vì thư gửi ở nền.</summary>
    private async Task<List<EmailMessage>> WaitForNoticesAsync(string email, string studentCode)
    {
        List<EmailMessage> Find() => factory.Mail.Sent
            .Where(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase) && m.Subject.Contains(studentCode)).ToList();
        for (var i = 0; i < 100 && Find().Count == 0; i++) await Task.Delay(100);
        return Find();
    }
}

public sealed class StudentForgotPasswordCodeLimitApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task The_fourth_request_for_the_same_code_in_an_hour_is_429_with_the_wait_in_minutes_even_for_a_code_that_does_not_exist()
    {
        var http = new ClassroomClient(factory).Http;
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await ForgotPasswordKit.AskAsync(http, "HS-KHONG CO")).Response.StatusCode);

        var (res, body) = await ForgotPasswordKit.AskAsync(http, "hskhongco");
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.Equal(MessageCodes.TooManyRequests, body.ErrorCode);
        Assert.Equal("Em đã xin đặt lại mật khẩu quá nhiều lần. Em hãy thử lại sau 60 phút.", body.Message);
        Assert.False(body.Success);
        var seconds = int.Parse(res.Headers.GetValues("Retry-After").Single());
        Assert.InRange(seconds, 3590, 3600);

        // Mã khác chưa bị ảnh hưởng.
        Assert.Equal(HttpStatusCode.OK, (await ForgotPasswordKit.AskAsync(http, "HSMACKHAC")).Response.StatusCode);
    }
}

public sealed class StudentForgotPasswordIpLimitApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task The_eleventh_request_from_one_address_in_fifteen_minutes_is_429_whatever_the_codes()
    {
        var http = new ClassroomClient(factory).Http;
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await ForgotPasswordKit.AskAsync(http, $"HSMA{i}")).Response.StatusCode);

        var (res, body) = await ForgotPasswordKit.AskAsync(http, "HSMAMOI");
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.Equal("Em đã xin đặt lại mật khẩu quá nhiều lần. Em hãy thử lại sau 15 phút.", body.Message);
        var seconds = int.Parse(res.Headers.GetValues("Retry-After").Single());
        Assert.InRange(seconds, 890, 900);
    }
}
