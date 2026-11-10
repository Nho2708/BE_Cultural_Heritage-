using System.Net;
using System.Text.Json;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Góp ý thêm cho AI khi soạn bài (<c>instructions</c> thành <c>teacherNote</c>) và viết lại riêng một phần bằng AI (<c>ai/rewrite-section</c>): kiểm tham số, loại phần theo cấp,
/// đoạn trích tư liệu gốc không viết lại, ghi nhật ký <c>LESSON_SECTION_REWRITE</c>, tính lượt AI, thử lại, lỗi không tính lượt, cảnh báo giới hạn, không lưu vào bài.
/// </summary>
public sealed class LessonRewriteApiTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly LessonTestKit _k = new(factory);
    private HttpClient Http => _k.Http;
    private const string T = LessonTestKit.Teacher;

    public Task InitializeAsync()
    {
        factory.Ai.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        factory.Ai.Reset();
        return Task.CompletedTask;
    }

    private async Task<(ClassroomClient.School School, long LessonId)> LessonAsync(int level)
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(level);
        var h = await _k.HeritageAsync("Hoàng thành", 6);
        var lesson = await _k.CreateLessonAsync(school.TeacherToken, schoolClass.Id, [h.Id]);
        return (school, lesson.Id);
    }

    private Task<List<AiGeneration>> RowsAsync(long lessonId) =>
        factory.WithDbAsync(db => db.AiGenerations.AsNoTracking().Where(g => g.LessonId == lessonId).OrderBy(g => g.Id).ToListAsync());

    private Task<(HttpResponseMessage Response, Envelope<LessonSectionRewriteDto> Body)> RewriteRawAsync(string token, long lessonId, object body) =>
        Http.PostAsync<LessonSectionRewriteDto>($"{T}/{lessonId}/ai/rewrite-section", body, token);

    private async Task<LessonSectionRewriteDto> RewriteAsync(string token, long lessonId, object body) =>
        await DiscoveryTestKit.Expect(RewriteRawAsync(token, lessonId, body));

    private static void AssertFail(HttpResponseMessage res, Envelope<object> body, HttpStatusCode status, string code)
    {
        Assert.Equal((status, code), (res.StatusCode, body.ErrorCode));
        Assert.False(body.Success);
    }

    private static string Words(int n) => string.Join(' ', Enumerable.Range(1, n).Select(i => $"từ{i}"));

    // ---- Yêu cầu thêm khi soạn bài ----

    [Fact]
    public async Task The_instructions_are_trimmed_sent_to_the_ai_as_teacherNote_and_logged_in_params_and_the_input_snapshot()
    {
        var (school, lessonId) = await LessonAsync(2);

        var (res, body) = await Http.PostAsync<LessonDraftDto>($"{T}/{lessonId}/ai/compose",
            new { length = "SHORT", instructions = "  Nhấn mạnh kiến trúc; liên hệ với địa phương  " }, school.TeacherToken);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Nhấn mạnh kiến trúc; liên hệ với địa phương", factory.Ai.LastRequest!.TeacherNote);
        var row = Assert.Single(await RowsAsync(lessonId));
        Assert.Equal(AiTaskType.LESSON_SUMMARY, row.TaskType);
        using var snapshot = JsonDocument.Parse(row.InputSnapshot!);
        using var parameters = JsonDocument.Parse(row.Params!);
        Assert.Equal("Nhấn mạnh kiến trúc; liên hệ với địa phương", snapshot.RootElement.GetProperty("instructions").GetString());
        Assert.Equal("Nhấn mạnh kiến trúc; liên hệ với địa phương", parameters.RootElement.GetProperty("instructions").GetString());
        // Bản giả lập nhận và bỏ qua: vẫn tất định và đúng khung.
        Assert.InRange(body.Data!.WordCount, 300, 500);
    }

    [Fact]
    public async Task Without_instructions_the_teacher_note_is_empty_and_the_logs_hold_null()
    {
        var (school, lessonId) = await LessonAsync(2);

        await _k.ComposeAsync(school.TeacherToken, lessonId);
        var blank = await Http.PostAsync<LessonDraftDto>($"{T}/{lessonId}/ai/compose", new { instructions = "   " }, school.TeacherToken);

        Assert.Equal(HttpStatusCode.OK, blank.Response.StatusCode);
        Assert.Equal(string.Empty, factory.Ai.LastRequest!.TeacherNote);
        var rows = await RowsAsync(lessonId);
        Assert.All(rows, r =>
        {
            using var parameters = JsonDocument.Parse(r.Params!);
            Assert.True(!parameters.RootElement.TryGetProperty("instructions", out var v) || v.ValueKind == JsonValueKind.Null);
        });
    }

    [Fact]
    public async Task Instructions_over_500_characters_after_trimming_are_refused_by_field_and_500_are_accepted()
    {
        var (school, lessonId) = await LessonAsync(2);

        var (res, body) = await Http.PostAsync<object>($"{T}/{lessonId}/ai/compose", new { instructions = new string('a', 501) }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("INVALID", body.ErrorCode);
        Assert.True(body.Errors!.ContainsKey("instructions"));
        Assert.Empty(await RowsAsync(lessonId));
        Assert.Equal(0, factory.Ai.Calls);

        var (ok, _) = await Http.PostAsync<LessonDraftDto>($"{T}/{lessonId}/ai/compose", new { instructions = "  " + new string('b', 500) + "  " }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(500, factory.Ai.LastRequest!.TeacherNote.Length);
    }

    // ---- Viết lại một phần: thành công ----

    [Fact]
    public async Task A_rewrite_returns_the_new_text_logs_one_succeeded_row_counts_one_turn_and_does_not_touch_the_lesson()
    {
        var (school, lessonId) = await LessonAsync(2);
        var token = school.TeacherToken;
        await _k.SaveDraftAsync(token, lessonId, await _k.ComposeAsync(token, lessonId));
        var lessonBefore = await _k.GetAsync(token, lessonId);
        var usedBefore = lessonBefore.AiQuota.Used;
        var rowsBefore = (await RowsAsync(lessonId)).Count;
        factory.Ai.Reset();

        var result = await RewriteAsync(token, lessonId, new
        {
            sectionType = "MILESTONE", text = "  Vua  Lý dời đô\nvề Thăng Long.  ", title = " 1010 ", year = 1010, instructions = "  Ngắn hơn  ",
        });

        Assert.Equal((LessonSectionType.MILESTONE, "Vua Lý dời đô về Thăng Long.", null, SimulatedLessonAiClient.Name),
            (result.SectionType, result.Text, result.Title, result.Model));
        Assert.Empty(result.Warnings);
        Assert.Equal(usedBefore + 1, result.AiQuota.Used);
        Assert.StartsWith("Đã tạo bản nháp đoạn viết lại.", result.Message);

        // Yêu cầu gửi sang Dịch vụ AI.
        var sent = factory.Ai.LastRewriteRequest!;
        Assert.Equal((result.GenerationId, lessonId, 2, LessonSectionType.MILESTONE), (sent.GenerationId, sent.LessonId, sent.GradeLevel, sent.Kind));
        Assert.Equal(("Vua  Lý dời đô\nvề Thăng Long.", "1010", 1010, "Ngắn hơn"), (sent.CurrentText, sent.Title, sent.Year, sent.TeacherNote));
        Assert.Equal(lessonBefore.Summary, sent.LessonContent);
        Assert.Single(sent.Heritages);
        Assert.Equal(sent.GenerationId, (await RowsAsync(lessonId)).Last().Id);

        // Một dòng nhật ký mới, gắn bài học, đã thành công; bài không đổi.
        var rows = await RowsAsync(lessonId);
        Assert.Equal(rowsBefore + 1, rows.Count);
        var row = rows.Last();
        Assert.Equal((AiTaskType.LESSON_SECTION_REWRITE, AiTaskStatus.SUCCEEDED, school.Teacher.Id, SimulatedLessonAiClient.Name),
            (row.TaskType, row.Status, row.RequestedBy, row.Model));
        Assert.Null(row.HeritageId);
        Assert.NotNull(row.CompletedAt);
        using var snapshot = JsonDocument.Parse(row.InputSnapshot!);
        Assert.Equal("Ngắn hơn", snapshot.RootElement.GetProperty("instructions").GetString());
        Assert.Equal("MILESTONE", snapshot.RootElement.GetProperty("kind").GetString());
        var lessonAfter = await _k.GetAsync(token, lessonId);
        Assert.Equal(lessonBefore.UpdatedAt, lessonAfter.UpdatedAt);
        Assert.Equal(lessonBefore.Summary, lessonAfter.Summary);
        Assert.Equal(lessonBefore.Sections.Select(s => s.Body), lessonAfter.Sections.Select(s => s.Body));
        Assert.Equal(usedBefore + 1, lessonAfter.AiQuota.Used);   // lượt viết lại tính vào hạn mức như soạn bài
    }

    [Theory]
    [InlineData(1, "STORY")]
    [InlineData(1, "IMAGE")]
    [InlineData(2, "READING")]
    [InlineData(2, "IMAGE")]
    [InlineData(3, "READING")]
    [InlineData(3, "MILESTONE")]
    [InlineData(3, "REFLECTION")]
    public async Task Every_section_type_of_the_frame_except_source_excerpts_can_be_rewritten(int level, string type)
    {
        var (school, lessonId) = await LessonAsync(level);
        var text = type == "REFLECTION" ? "Vì sao di sản cần được gìn giữ?" : "Cổng làng buổi sáng sớm. Mái ngói rêu phong.";

        var result = await RewriteAsync(school.TeacherToken, lessonId, new { sectionType = type, text });

        Assert.Equal(Enum.Parse<LessonSectionType>(type), result.SectionType);
        Assert.Equal(text, result.Text);
        Assert.Single(await RowsAsync(lessonId));
    }

    [Fact]
    public async Task A_rewrite_works_for_a_published_lesson_too()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(1);
        var h = await _k.HeritageAsync("Hoàng thành", 3);
        var lesson = await _k.PublishedLessonAsync(school.TeacherToken, schoolClass.Id, [h.Id]);

        var result = await RewriteAsync(school.TeacherToken, lesson.Id, new { sectionType = "STORY", text = "Cậu bé lớn nhanh. Cậu bé vươn vai." });

        Assert.Equal("Cậu bé lớn nhanh. Cậu bé vươn vai.", result.Text);
        Assert.Equal(lesson.Summary, (await _k.GetAsync(school.TeacherToken, lesson.Id)).Summary);
    }

    [Fact]
    public async Task At_level_one_the_simulated_rewrite_cuts_a_text_over_50_words_and_the_result_is_checked_by_the_backend()
    {
        var (school, lessonId) = await LessonAsync(1);
        string Sentence(int n) => string.Join(' ', Enumerable.Range(1, 12).Select(i => $"chữ{n}{i}")) + ".";
        var text = string.Join(' ', Enumerable.Range(1, 6).Select(Sentence));

        var result = await RewriteAsync(school.TeacherToken, lessonId, new { sectionType = "STORY", text });

        Assert.InRange(LessonText.CountWords(result.Text), 1, 50);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("tối đa 50 từ"));
    }

    [Fact]
    public async Task A_result_beyond_the_limits_of_the_section_type_comes_with_warnings_but_is_not_blocked()
    {
        var (school, lessonId) = await LessonAsync(2);
        factory.Ai.RewriteBehavior = (_, request, _) => Task.FromResult(new AiRewriteSectionResult(
            "**Rất dài.** " + Words(70) + ". Câu hai. Câu ba.", "  Tiêu đề mới  ", "mo-hinh-thu", new AiUsage(10, 20)));

        var result = await RewriteAsync(school.TeacherToken, lessonId, new { sectionType = "MILESTONE", text = "Vua dời đô về Thăng Long.", year = 1010 });

        Assert.Contains(result.Warnings, w => w.Contains("tối đa 60 từ"));
        Assert.Contains(result.Warnings, w => w.Contains("3 câu"));
        Assert.Contains(result.Warnings, w => w.Contains("Markdown"));
        Assert.Equal(("Tiêu đề mới", "mo-hinh-thu"), (result.Title, result.Model));
        Assert.Equal(AiTaskStatus.SUCCEEDED, (await RowsAsync(lessonId)).Single().Status);
    }

    // ---- Kiểm tham số ----

    [Fact]
    public async Task Source_excerpts_and_section_types_outside_the_level_are_refused_without_calling_the_ai()
    {
        var (school3, lesson3) = await LessonAsync(3);
        var (school1, lesson1) = await LessonAsync(1);

        var (res, body) = await Http.PostAsync<object>($"{T}/{lesson3}/ai/rewrite-section", new { sectionType = "SOURCE_EXCERPT", text = "Trích nguyên văn." }, school3.TeacherToken);
        AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");
        Assert.Contains("nguyên văn", body.Errors!["sectionType"]);

        foreach (var (token, id, type) in new[] { (school1.TeacherToken, lesson1, "READING"), (school1.TeacherToken, lesson1, "MILESTONE"), (school3.TeacherToken, lesson3, "STORY"), (school3.TeacherToken, lesson3, "IMAGE") })
        {
            (res, body) = await Http.PostAsync<object>($"{T}/{id}/ai/rewrite-section", new { sectionType = type, text = "Chữ." }, token);
            AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");
            Assert.Contains("không dùng được ở bài của lớp cấp", body.Errors!["sectionType"]);
        }
        Assert.Equal(0, factory.Ai.RewriteCalls);
        Assert.Empty(await RowsAsync(lesson3));
        Assert.Empty(await RowsAsync(lesson1));
    }

    [Fact]
    public async Task Text_title_year_instructions_and_type_are_validated_by_field()
    {
        var (school, lessonId) = await LessonAsync(2);
        var token = school.TeacherToken;

        async Task<Dictionary<string, string>> Errors(object body)
        {
            var (res, envelope) = await Http.PostAsync<object>($"{T}/{lessonId}/ai/rewrite-section", body, token);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            return envelope.Errors!;
        }

        Assert.True((await Errors(new { text = "Chữ." })).ContainsKey("sectionType"));
        Assert.True((await Errors(new { sectionType = "READING" })).ContainsKey("text"));
        Assert.True((await Errors(new { sectionType = "READING", text = "   " })).ContainsKey("text"));
        Assert.True((await Errors(new { sectionType = "READING", text = new string('a', 4001) })).ContainsKey("text"));
        Assert.True((await Errors(new { sectionType = "READING", text = "Chữ.", title = new string('t', 256) })).ContainsKey("title"));
        Assert.True((await Errors(new { sectionType = "READING", text = "Chữ.", instructions = new string('i', 501) })).ContainsKey("instructions"));
        // Năm chỉ với mốc thời gian và trong khoảng hợp lệ.
        Assert.True((await Errors(new { sectionType = "READING", text = "Chữ.", year = 1010 })).ContainsKey("year"));
        Assert.True((await Errors(new { sectionType = "MILESTONE", text = "Chữ.", year = 99999 })).ContainsKey("year"));
        Assert.Equal(0, factory.Ai.RewriteCalls);
        Assert.Empty(await RowsAsync(lessonId));

        // Đúng ngưỡng thì được.
        Assert.Equal(HttpStatusCode.OK, (await RewriteRawAsync(token, lessonId, new { sectionType = "READING", text = new string('a', 4000), title = new string('t', 255), instructions = new string('i', 500) })).Response.StatusCode);
    }

    // ---- Điều kiện, quyền, hạn mức ----

    [Fact]
    public async Task Another_teacher_the_org_admin_and_students_cannot_rewrite_and_archived_lessons_are_blocked()
    {
        var (school, lessonId) = await LessonAsync(2);
        var (_, otherToken) = await _k.Classroom.AnotherTeacherAsync(school);
        var body = new { sectionType = "READING", text = "Chữ của đoạn." };

        var (res, envelope) = await Http.PostAsync<object>($"{T}/{lessonId}/ai/rewrite-section", body, otherToken);
        AssertFail(res, envelope, HttpStatusCode.NotFound, "BLOCKED");
        (res, _) = await Http.PostAsync<object>($"{T}/{lessonId}/ai/rewrite-section", body, school.OrgAdminToken);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        (res, _) = await Http.PostAsync<object>($"{T}/{lessonId}/ai/rewrite-section", body);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);

        var (school2, schoolClass2) = await _k.SchoolWithClassAsync(2);
        var h = await _k.HeritageAsync("Hoàng thành", 3);
        var lesson = await _k.PublishedLessonAsync(school2.TeacherToken, schoolClass2.Id, [h.Id]);
        await DiscoveryTestKit.Expect(Http.PostAsync<LessonDetailDto>($"{T}/{lesson.Id}/archive", null, school2.TeacherToken));
        (res, envelope) = await Http.PostAsync<object>($"{T}/{lesson.Id}/ai/rewrite-section", body, school2.TeacherToken);
        AssertFail(res, envelope, HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("Bài học đã lưu trữ", envelope.Message);

        // Không còn di sản đang xuất bản nào: không có gì để giữ mạch.
        var (school3, lesson3) = await LessonAsync(2);
        var heritageId = await factory.WithDbAsync(db => db.LessonHeritages.Where(x => x.LessonId == lesson3).Select(x => x.HeritageId).SingleAsync());
        await _k.ArchiveHeritageAsync(heritageId);
        (res, envelope) = await Http.PostAsync<object>($"{T}/{lesson3}/ai/rewrite-section", body, school3.TeacherToken);
        AssertFail(res, envelope, HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Equal(0, factory.Ai.RewriteCalls);
    }

    [Fact]
    public async Task Without_a_plan_or_with_no_turns_left_the_rewrite_is_refused_and_no_row_is_written()
    {
        var (school, lessonId) = await LessonAsync(2);
        var body = new { sectionType = "READING", text = "Chữ của đoạn." };

        // Hết lượt: gói thử cho 20 lượt, dùng hết bằng các dòng nhật ký giả.
        var quota = (await _k.GetAsync(school.TeacherToken, lessonId)).AiQuota;
        await factory.WithDbAsync(async db =>
        {
            for (var i = 0; i < quota.Max; i++)
                db.AiGenerations.Add(new AiGeneration
                {
                    TaskType = AiTaskType.LESSON_SECTION_REWRITE, LessonId = lessonId, RequestedBy = school.Teacher.Id, Status = AiTaskStatus.SUCCEEDED,
                    CreatedAt = DateTime.UtcNow,
                });
            return await db.SaveChangesAsync();
        });
        var rowsBefore = (await RowsAsync(lessonId)).Count;

        var (res, envelope) = await Http.PostAsync<object>($"{T}/{lessonId}/ai/rewrite-section", body, school.TeacherToken);
        AssertFail(res, envelope, HttpStatusCode.UnprocessableEntity, "AI_QUOTA_REACHED");
        Assert.Equal(rowsBefore, (await RowsAsync(lessonId)).Count);

        // Không có gói hiệu lực.
        await factory.WithDbAsync(async db =>
        {
            foreach (var s in await db.Subscriptions.Where(x => x.OrganizationId == school.OrgId).ToListAsync()) s.Status = SubscriptionStatus.CANCELLED;
            return await db.SaveChangesAsync();
        });
        (res, envelope) = await Http.PostAsync<object>($"{T}/{lessonId}/ai/rewrite-section", body, school.TeacherToken);
        AssertFail(res, envelope, HttpStatusCode.UnprocessableEntity, "PLAN_REQUIRED");
        Assert.StartsWith("Không thể viết lại phần bài học bằng AI.", envelope.Message);
        Assert.Equal(0, factory.Ai.RewriteCalls);
    }

    // ---- Dịch vụ AI lỗi ----

    [Fact]
    public async Task A_failing_ai_service_is_retried_twice_inside_the_same_row()
    {
        var (school, lessonId) = await LessonAsync(2);
        factory.Ai.RewriteBehavior = (call, request, ct) => call < 3
            ? throw new AiServiceException("Dịch vụ AI trả mã 503.", retryable: true)
            : new SimulatedLessonAiClient().RewriteSectionAsync(request, "x", ct);

        var result = await RewriteAsync(school.TeacherToken, lessonId, new { sectionType = "READING", text = "Đoạn bài đọc cần viết lại." });

        Assert.Equal(3, factory.Ai.RewriteCalls);
        Assert.Equal(1, result.AiQuota.Used);
        var row = Assert.Single(await RowsAsync(lessonId));
        Assert.Equal((AiTaskStatus.SUCCEEDED, result.GenerationId), (row.Status, row.Id));
    }

    [Fact]
    public async Task When_every_attempt_fails_or_returns_empty_text_the_row_is_failed_the_turn_is_not_counted_and_the_status_is_503()
    {
        var (school, lessonId) = await LessonAsync(2);
        var body = new { sectionType = "READING", text = "Đoạn bài đọc cần viết lại." };

        factory.Ai.RewriteBehavior = (_, _, _) => throw new AiServiceException("Dịch vụ AI trả mã 500.", retryable: true);
        var (res, envelope) = await Http.PostAsync<object>($"{T}/{lessonId}/ai/rewrite-section", body, school.TeacherToken);
        AssertFail(res, envelope, HttpStatusCode.ServiceUnavailable, "AI_UNAVAILABLE");
        Assert.Contains("Lượt này không bị tính", envelope.Message);
        Assert.Equal(3, factory.Ai.RewriteCalls);

        factory.Ai.Reset();
        factory.Ai.RewriteBehavior = (_, _, _) => Task.FromResult(new AiRewriteSectionResult("   ", null, "x", new AiUsage(0, 0)));
        (res, envelope) = await Http.PostAsync<object>($"{T}/{lessonId}/ai/rewrite-section", body, school.TeacherToken);
        AssertFail(res, envelope, HttpStatusCode.ServiceUnavailable, "AI_UNAVAILABLE");
        Assert.Equal(3, factory.Ai.RewriteCalls);

        factory.Ai.Reset();
        factory.Ai.RewriteBehavior = (_, _, _) => throw new AiServiceException("Dịch vụ AI trả mã 422.", retryable: false);
        (res, envelope) = await Http.PostAsync<object>($"{T}/{lessonId}/ai/rewrite-section", body, school.TeacherToken);
        AssertFail(res, envelope, HttpStatusCode.ServiceUnavailable, "AI_UNAVAILABLE");
        Assert.Equal(1, factory.Ai.RewriteCalls);   // lỗi 4xx không thử lại

        var rows = await RowsAsync(lessonId);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal((AiTaskType.LESSON_SECTION_REWRITE, AiTaskStatus.FAILED), (r.TaskType, r.Status)));
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.ErrorMessage)));
        Assert.Equal(0, (await _k.GetAsync(school.TeacherToken, lessonId)).AiQuota.Used);
    }

    [Fact]
    public async Task Rewrite_rows_count_in_the_school_quota_even_for_another_teacher_of_the_school()
    {
        var (school, lessonId) = await LessonAsync(2);
        var (_, otherToken) = await _k.Classroom.AnotherTeacherAsync(school);

        await RewriteAsync(school.TeacherToken, lessonId, new { sectionType = "READING", text = "Đoạn bài đọc cần viết lại." });
        await _k.ComposeAsync(school.TeacherToken, lessonId);

        var quota = await DiscoveryTestKit.Expect(Http.GetAsync<AiQuotaDto>($"{T}/ai-quota", otherToken));
        Assert.Equal(2, quota.Used);
    }

    [Fact]
    public async Task The_swagger_like_json_of_the_result_uses_camel_case_names()
    {
        var (school, lessonId) = await LessonAsync(2);

        var result = await RewriteAsync(school.TeacherToken, lessonId, new { sectionType = "READING", text = "Đoạn bài đọc cần viết lại." });
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });

        foreach (var field in new[] { "generationId", "sectionType", "text", "title", "warnings", "model", "aiQuota", "message" })
            Assert.Contains($"\"{field}\"", json);
    }
}
