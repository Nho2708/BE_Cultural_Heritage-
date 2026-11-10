using System.Net;
using DSVHVN.Application.Common;
using Microsoft.Extensions.Configuration;
using static DSVHVN.Tests.Integration.GuestTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>Api chạy với ngưỡng thấp (3 lượt AI của giáo viên, 3 sự kiện lịch sử, 3 lượt đọc tóm tắt, 2 lượt AI dịch mỗi phút) để kiểm được các giới hạn theo người và theo thiết bị.</summary>
public sealed class LowLimitApiFactory : ApiFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
    [
        new("RateLimiting:TeacherAiRequestsPerMinute", "3"),
        new("RateLimiting:GuestHistoryEventsPerMinute", "3"),
        new("RateLimiting:GuestSummaryRequestsPerMinute", "3"),
        new("RateLimiting:AdminTranslationAiRequestsPerMinute", "2"),
    ];
}

/// <summary>
/// Giới hạn tần suất theo người và theo thiết bị qua Api thật: AI của giáo viên, AI dịch và tóm tắt của quản trị, đọc tóm tắt và ghi lịch sử của khách.
/// Vượt ngưỡng thì 429 theo khuôn phản hồi chung kèm Retry-After; người hoặc thiết bị khác không bị ảnh hưởng.
/// </summary>
public sealed class RateLimitApiTests(LowLimitApiFactory factory) : IClassFixture<LowLimitApiFactory>
{
    private readonly LessonTestKit _lessons = new(factory);
    private readonly GuestTestKit _guests = new(factory);
    private HttpClient Http => _guests.Http;

    private static void AssertTooMany(HttpResponseMessage response, Envelope<object> body)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.False(body.Success);
        Assert.Equal(MessageCodes.TooManyRequests, body.ErrorCode);
        var seconds = int.Parse(response.Headers.GetValues("Retry-After").Single());
        Assert.InRange(seconds, 1, 60);
        Assert.Equal($"Có quá nhiều yêu cầu gửi tới trong thời gian ngắn. Vui lòng thử lại sau {seconds} giây.", body.Message);
    }

    // ---- AI của giáo viên ----

    [Fact]
    public async Task A_teacher_may_call_the_ai_three_times_a_minute_across_lesson_and_question_ai_and_other_teachers_are_unaffected()
    {
        var (school, schoolClass) = await _lessons.SchoolWithClassAsync(2);
        var heritage = await _lessons.HeritageAsync("Hoàng thành", events: 2);
        var lesson = await _lessons.CreateLessonAsync(school.TeacherToken, schoolClass.Id, [heritage.Id]);
        var compose = $"{LessonTestKit.Teacher}/{lesson.Id}/ai/compose";
        var questions = $"{LessonTestKit.Teacher}/{lesson.Id}/ai/questions";

        Assert.Equal(HttpStatusCode.OK, (await Http.PostAsync<object>(compose, new { length = "SHORT" }, school.TeacherToken)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Http.PostAsync<object>(compose, new { length = "SHORT" }, school.TeacherToken)).Response.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Http.PostAsync<object>(questions, new { count = 1 }, school.TeacherToken)).Response.StatusCode);

        var (blocked, body) = await Http.PostAsync<object>(compose, new { length = "SHORT" }, school.TeacherToken);
        AssertTooMany(blocked, body);
        var (blockedQuestions, questionsBody) = await Http.PostAsync<object>(questions, new { count = 1 }, school.TeacherToken);
        AssertTooMany(blockedQuestions, questionsBody);

        // Xem hạn mức AI không tính; giáo viên khác của trường vẫn gọi được.
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync<object>($"{LessonTestKit.Teacher}/ai-quota", school.TeacherToken)).Response.StatusCode);
        var (_, otherToken) = await _lessons.Classroom.AnotherTeacherAsync(school);
        var (other, _) = await Http.PostAsync<object>(compose, new { length = "SHORT" }, otherToken);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, other.StatusCode);
    }

    [Fact]
    public async Task Rewriting_a_section_shares_the_teacher_ai_limit_with_compose()
    {
        var (school, schoolClass) = await _lessons.SchoolWithClassAsync(2);
        var heritage = await _lessons.HeritageAsync("Hoàng thành", events: 2);
        var lesson = await _lessons.CreateLessonAsync(school.TeacherToken, schoolClass.Id, [heritage.Id]);
        var compose = $"{LessonTestKit.Teacher}/{lesson.Id}/ai/compose";
        var rewrite = $"{LessonTestKit.Teacher}/{lesson.Id}/ai/rewrite-section";
        var section = new { sectionType = "READING", text = "Đoạn bài đọc cần viết lại." };

        Assert.Equal(HttpStatusCode.OK, (await Http.PostAsync<object>(compose, new { length = "SHORT" }, school.TeacherToken)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Http.PostAsync<object>(rewrite, section, school.TeacherToken)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Http.PostAsync<object>(rewrite, section, school.TeacherToken)).Response.StatusCode);

        var (blocked, body) = await Http.PostAsync<object>(rewrite, section, school.TeacherToken);
        AssertTooMany(blocked, body);
        var (blockedCompose, composeBody) = await Http.PostAsync<object>(compose, new { length = "SHORT" }, school.TeacherToken);
        AssertTooMany(blockedCompose, composeBody);
    }

    // ---- AI dịch và tóm tắt của quản trị hệ thống ----

    [Fact]
    public async Task The_admin_may_start_the_translation_ai_two_times_a_minute_here()
    {
        var admin = await _guests.AdminAsync();
        var url = "/api/v1/admin/heritages/987654/translations/generate";

        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsync<object>(url, null, admin)).Response.StatusCode);

        var (blocked, body) = await Http.PostAsync<object>(url, null, admin);
        AssertTooMany(blocked, body);

        // Xem bảng bản dịch không tính vào giới hạn.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Http.GetAsync<object>("/api/v1/admin/translations/counts", admin)).Response.StatusCode);
    }

    // ---- Khách: đọc tóm tắt ----

    [Fact]
    public async Task A_device_may_read_summaries_three_times_a_minute_and_another_device_is_unaffected()
    {
        var device = NewKey();
        var url = $"{PublicRoot}/heritages/khong-co-di-san-nay/summary";

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await _guests.GetAsync<object>(url, device)).Response.StatusCode);

        var (blocked, body) = await _guests.GetAsync<object>(url, device);
        AssertTooMany(blocked, body);

        Assert.Equal(HttpStatusCode.NotFound, (await _guests.GetAsync<object>(url, NewKey())).Response.StatusCode);
        // Chi tiết di sản của chính thiết bị đó không dùng chung bộ đếm đọc tóm tắt.
        Assert.Equal(HttpStatusCode.NotFound, (await _guests.GetAsync<object>($"{PublicRoot}/heritages/khong-co-di-san-nay", device)).Response.StatusCode);
    }

    [Fact]
    public async Task A_student_session_without_a_device_key_is_counted_per_account_not_per_address()
    {
        var (school, schoolClass) = await _lessons.SchoolWithClassAsync(2);
        var first = await _lessons.StudentTokenAsync(school.TeacherToken, schoolClass.Id, "Em thứ nhất");
        var second = await _lessons.StudentTokenAsync(school.TeacherToken, schoolClass.Id, "Em thứ hai");
        var url = $"{PublicRoot}/heritages/khong-co-di-san-nay/summary";

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await _guests.GetAsync<object>(url, null, first)).Response.StatusCode);
        var (blocked, body) = await _guests.GetAsync<object>(url, null, first);
        AssertTooMany(blocked, body);

        // Em khác cùng địa chỉ IP không bị chung bộ đếm.
        Assert.Equal(HttpStatusCode.NotFound, (await _guests.GetAsync<object>(url, null, second)).Response.StatusCode);
    }

    // ---- Khách: ghi lịch sử ----

    [Fact]
    public async Task A_device_may_record_three_history_events_a_minute_and_automatic_recording_beyond_that_is_dropped_silently()
    {
        var admin = await _guests.AdminAsync();
        var first = await _guests.HeritageAsync(admin, "Chùa thử một");
        var second = await _guests.HeritageAsync(admin, "Chùa thử hai");
        var device = NewKey();

        // Ba sự kiện ghi tay: lượt lặp trong 10 phút không ghi thêm nhưng vẫn tính vào bộ đếm.
        Assert.True((await _guests.RecordAsync(device, "VIEW", first.Slug)).Recorded);
        Assert.True((await _guests.RecordAsync(device, "READ_SUMMARY", first.Slug)).Recorded);
        Assert.False((await _guests.RecordAsync(device, "VIEW", first.Slug)).Recorded);

        // Sự kiện thứ tư: 429 kèm Retry-After.
        var (blocked, body) = await _guests.PostAsync<object>($"{Root}/activities", device, new { type = "VIEW_3D", slug = first.Slug });
        AssertTooMany(blocked, body);

        // Ghi tự động khi xem chi tiết cũng bị bỏ lặng lẽ: yêu cầu chính vẫn 200, lịch sử không có di sản thứ hai.
        var (detail, _) = await _guests.GetAsync<object>($"{PublicRoot}/heritages/{second.Slug}", device);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var history = await _guests.HistoryAsync(device);
        Assert.Equal(2, history.Items.Count);

        // Thiết bị khác không bị ảnh hưởng.
        Assert.True((await _guests.RecordAsync(NewKey(), "VIEW", first.Slug)).Recorded);
    }

    [Fact]
    public void The_shipped_configuration_holds_the_agreed_limits()
    {
        // Đọc đúng tệp appsettings.json của Api (khóa cấu hình để chỉnh được), không qua cấu hình ghi đè của kiểm thử.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "DSVHVN.Api", "appsettings.json"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(dir!.FullName, "src", "DSVHVN.Api", "appsettings.json")).Build();
        var limits = config.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>()!;
        Assert.Equal((300, 10, 120, 60, 300, 5),
            (limits.PublicRequestsPerMinute, limits.TeacherAiRequestsPerMinute, limits.GuestHistoryEventsPerMinute,
                limits.GuestSummaryRequestsPerMinute, limits.AdMobBadSignaturesPerMinute, limits.AdminTranslationAiRequestsPerMinute));
    }
}
