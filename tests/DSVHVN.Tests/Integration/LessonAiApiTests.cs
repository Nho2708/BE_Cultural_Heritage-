using System.Net;
using System.Net.Http.Json;
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
/// AI soạn nội dung bài học theo cấp qua dịch vụ AI giả lập: nội dung đúng khung của cấp, ghi nhật ký <c>ai_generations</c>, tính hạn mức lượt AI
/// của gói, thử lại khi dịch vụ AI lỗi, lượt thất bại không bị tính, bản nháp không tự lưu.
/// </summary>
public sealed class LessonAiApiTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
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

    private async Task<(ClassroomClient.School School, long LessonId, long HeritageId)> LessonAsync(int level, int events = 6)
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(level);
        var h = await _k.HeritageAsync("Hoàng thành", events);
        var lesson = await _k.CreateLessonAsync(school.TeacherToken, schoolClass.Id, [h.Id]);
        return (school, lesson.Id, h.Id);
    }

    private static int Words(string? text) => LessonText.CountWords(text);

    private Task<List<AiGeneration>> RowsAsync(long lessonId) =>
        factory.WithDbAsync(db => db.AiGenerations.AsNoTracking().Where(g => g.LessonId == lessonId).OrderBy(g => g.Id).ToListAsync());

    // ---- Nội dung đúng cấp ----

    [Theory]
    [InlineData(LessonLength.SHORT)]
    [InlineData(LessonLength.MEDIUM)]
    [InlineData(LessonLength.DETAILED)]
    public async Task Level_one_draft_is_a_short_story_in_steps_with_one_picture_per_step(LessonLength length)
    {
        var (school, lessonId, heritageId) = await LessonAsync(1);
        var token = school.TeacherToken;
        var materials = await DiscoveryTestKit.Expect(Http.GetAsync<LessonMaterialsDto>($"{T}/{lessonId}/materials", token));

        var draft = await _k.ComposeAsync(token, lessonId, length);

        Assert.Equal((1, length), (draft.Level, draft.Length));
        Assert.InRange(Words(draft.Summary), 150, 250);
        Assert.Equal(Words(draft.Summary), draft.WordCount);
        var steps = draft.Sections.Where(s => s.Type == LessonSectionType.STORY).ToList();
        Assert.InRange(steps.Count, LessonRules.Level1MinSteps, LessonRules.Level1MaxSteps);
        Assert.All(steps, s =>
        {
            Assert.InRange(Words(s.Body), 1, LessonRules.Level1StepMaxWords);
            Assert.Contains(s.ImageUrl, materials.Images.Select(i => i.Url));
            Assert.NotNull(s.Image?.Credit);
        });
        Assert.Equal(draft.Summary, string.Join(' ', steps.Select(s => s.Body)));
        Assert.All(draft.Sections, s => Assert.Contains(s.Type, new[] { LessonSectionType.STORY, LessonSectionType.IMAGE }));
        Assert.InRange(draft.Sections.Count(s => s.Type == LessonSectionType.IMAGE), 1, 3);
        // Câu đơn giản: không câu nào quá 15 từ.
        Assert.All(draft.Summary.Split('.', StringSplitOptions.RemoveEmptyEntries), sentence => Assert.InRange(Words(sentence), 1, 15));
        Assert.InRange(draft.KeyPoints.Count, 3, 7);
        Assert.All(draft.KeyPoints, k => Assert.InRange(k.Length, 1, 300));
        Assert.DoesNotContain(draft.Warnings, w => w.Contains("thường dài"));
        Assert.Equal(heritageId, factory.Ai.LastRequest!.Heritages.Single().HeritageId);

        // Chấp nhận bản nháp: bài đủ điều kiện xuất bản.
        var saved = await _k.SaveDraftAsync(token, lessonId, draft);
        Assert.True(saved.Readiness.IsReady);
        Assert.Equal(draft.WordCount, saved.WordCount);
    }

    [Fact]
    public async Task The_three_lengths_grow_within_each_level_range_and_each_level_gets_its_own_frame()
    {
        var wordsByLevel = new Dictionary<int, List<int>>();
        foreach (var level in new[] { 1, 2, 3 })
        {
            var (school, lessonId, _) = await LessonAsync(level);
            var frame = LessonFrames.For(level);
            var counts = new List<int>();
            foreach (var length in new[] { LessonLength.SHORT, LessonLength.MEDIUM, LessonLength.DETAILED })
            {
                var draft = await _k.ComposeAsync(school.TeacherToken, lessonId, length);
                Assert.InRange(draft.WordCount, frame.MinWords, frame.MaxWords);
                Assert.DoesNotContain(draft.Warnings, w => w.Contains("thường dài"));
                counts.Add(draft.WordCount);
            }
            Assert.True(counts[0] < counts[1] && counts[1] < counts[2], $"Cấp {level}: {string.Join(", ", counts)}");
            wordsByLevel[level] = counts;
        }
        // Cấp càng cao bài càng dài: bài ngắn của cấp sau dài hơn bài chi tiết của cấp trước.
        Assert.True(wordsByLevel[1][2] < wordsByLevel[2][0] && wordsByLevel[2][2] < wordsByLevel[3][0]);
    }

    [Fact]
    public async Task Level_two_draft_has_a_reading_with_captioned_pictures_and_milestones_taken_from_the_timeline()
    {
        var (school, lessonId, _) = await LessonAsync(2, events: 12);
        var token = school.TeacherToken;
        var materials = await DiscoveryTestKit.Expect(Http.GetAsync<LessonMaterialsDto>($"{T}/{lessonId}/materials", token));

        var draft = await _k.ComposeAsync(token, lessonId, LessonLength.DETAILED);

        Assert.InRange(draft.WordCount, 300, 500);
        Assert.All(draft.Sections, s => Assert.Contains(s.Type, new[] { LessonSectionType.IMAGE, LessonSectionType.MILESTONE }));
        var milestones = draft.Sections.Where(s => s.Type == LessonSectionType.MILESTONE).ToList();
        Assert.InRange(milestones.Count, LessonRules.Level2MinMilestones, LessonRules.Level2MaxMilestones);
        Assert.Equal(milestones.Select(m => m.MilestoneYear).Order(), milestones.Select(m => m.MilestoneYear));
        Assert.All(milestones, m =>
        {
            Assert.Contains(m.EventId, materials.Events.Select(e => (long?)e.EventId));
            Assert.False(string.IsNullOrWhiteSpace(m.Body));
        });
        var images = draft.Sections.Where(s => s.Type == LessonSectionType.IMAGE).ToList();
        Assert.InRange(images.Count, 1, 2);
        Assert.All(images, i => Assert.False(string.IsNullOrWhiteSpace(i.Body)));
        Assert.DoesNotContain(draft.Warnings, w => w.Contains("mốc thời gian"));

        var saved = await _k.SaveDraftAsync(token, lessonId, draft);
        Assert.True(saved.Readiness.IsReady);
    }

    [Fact]
    public async Task Level_three_draft_is_an_analysis_with_verbatim_source_excerpts_and_reflection_questions()
    {
        var (school, lessonId, heritageId) = await LessonAsync(3);
        var token = school.TeacherToken;
        var heritage = await _k.HeritageOnlyAsync(heritageId);

        var draft = await _k.ComposeAsync(token, lessonId, LessonLength.MEDIUM);

        Assert.InRange(draft.WordCount, 600, 900);
        var excerpts = draft.Sections.Where(s => s.Type == LessonSectionType.SOURCE_EXCERPT).ToList();
        Assert.InRange(excerpts.Count, 1, LessonRules.Level3MaxExcerpts);
        Assert.All(excerpts, e =>
        {
            Assert.Contains(e.Body!, heritage.Content!);                     // nguyên văn từ nội dung di sản
            Assert.InRange(Words(e.Body), 1, LessonRules.Level3ExcerptMaxWords);
            Assert.False(string.IsNullOrWhiteSpace(e.SourceCitation));        // bắt buộc có nguồn
        });
        Assert.InRange(draft.Sections.Count(s => s.Type == LessonSectionType.REFLECTION), LessonRules.Level3MinReflections, LessonRules.Level3MaxReflections);
        Assert.All(draft.Sections, s => Assert.Contains(s.Type, new[]
            { LessonSectionType.SOURCE_EXCERPT, LessonSectionType.MILESTONE, LessonSectionType.REFLECTION }));
        Assert.Contains(excerpts, e => e.SourceCitation!.Contains("vanban.chinhphu.vn"));

        var saved = await _k.SaveDraftAsync(token, lessonId, draft);
        Assert.True(saved.Readiness.IsReady);
    }

    // ---- Nhật ký và hạn mức ----

    [Fact]
    public async Task Each_call_writes_a_log_row_without_personal_data_counts_one_turn_and_does_not_change_the_lesson()
    {
        var (school, lessonId, heritageId) = await LessonAsync(2);
        var token = school.TeacherToken;
        await _k.Classroom.AddStudentAsync(token, (await _k.GetAsync(token, lessonId)).ClassId, "Trần Thị Bích Ngọc");

        var first = await _k.ComposeAsync(token, lessonId);
        Assert.Equal((19, 1), (first.AiQuota.Remaining, first.AiQuota.Used));
        Assert.Equal($"Đã tạo bản nháp nội dung bài học. Hãy kiểm tra độ chính xác trước khi dùng. Còn 19 lượt AI trong kỳ.", first.Message);

        // Bản nháp chưa lưu vào bài.
        var detail = await _k.GetAsync(token, lessonId);
        Assert.Null(detail.Summary);
        Assert.Empty(detail.Sections);

        var rows = await RowsAsync(lessonId);
        var row = Assert.Single(rows);
        Assert.Equal((AiTaskType.LESSON_SUMMARY, AiTaskStatus.SUCCEEDED, school.Teacher.Id, (long?)null), (row.TaskType, row.Status, row.RequestedBy, row.HeritageId));
        Assert.Equal(SimulatedLessonAiClient.Name, row.Model);
        Assert.NotNull(row.CompletedAt);
        Assert.False(string.IsNullOrWhiteSpace(row.RawOutput));
        Assert.Equal(first.GenerationId, row.Id);
        Assert.Contains("\"gradeLevel\":2", row.Params);
        Assert.Contains("MEDIUM", row.Params);

        // Dữ liệu gửi AI chỉ có nội dung di sản và cấp học: không họ tên, email, tên lớp, mã học sinh.
        var heritage = await _k.HeritageOnlyAsync(heritageId);
        Assert.Contains(heritage.Name!, row.InputSnapshot);
        Assert.Contains("\"gradeLevel\":2", row.InputSnapshot);
        foreach (var personal in new[] { school.Teacher.FullName, school.Teacher.Email, "Lớp thử", "Trần Thị Bích Ngọc", "HS" })
            Assert.DoesNotContain(personal!, row.InputSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain(school.Teacher.FullName!, row.RawOutput!);

        // Tạo lại: một lượt nữa, một dòng nữa.
        var second = await _k.ComposeAsync(token, lessonId, LessonLength.SHORT);
        Assert.Equal((18, 2), (second.AiQuota.Remaining, second.AiQuota.Used));
        Assert.NotEqual(first.GenerationId, second.GenerationId);
        Assert.Equal(2, (await RowsAsync(lessonId)).Count);

        var quota = await DiscoveryTestKit.Expect(Http.GetAsync<AiQuotaDto>($"{T}/ai-quota", token));
        Assert.Equal((true, 2, 20, 18, "Miễn phí"), (quota.HasEffectivePlan, quota.Used, quota.Max, quota.Remaining, quota.PlanName));
    }

    [Fact]
    public async Task The_quota_is_the_whole_schools_in_the_plan_period_and_stops_the_call_when_used_up()
    {
        var (school, lessonId, _) = await LessonAsync(2);
        var (_, otherToken) = await _k.Classroom.AnotherTeacherAsync(school);
        var otherClass = await _k.Classroom.CreateClassAsync(otherToken, "Lớp bạn", 8);
        var h = await _k.HeritageAsync("Văn Miếu", events: 3);
        var otherLesson = await _k.CreateLessonAsync(otherToken, otherClass.Id, [h.Id]);
        await LimitAiAsync(school.OrgId, 2);

        await _k.ComposeAsync(school.TeacherToken, lessonId);
        await _k.ComposeAsync(otherToken, otherLesson.Id);          // giáo viên khác cùng trường: cùng hạn mức
        var calls = factory.Ai.Calls;

        var (res, body) = await Http.PostAsync<object>($"{T}/{lessonId}/ai/compose", new { }, school.TeacherToken);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "AI_QUOTA_REACHED"), (res.StatusCode, body.ErrorCode));
        Assert.Matches(@"^Trường đã dùng hết 2 lượt AI của kỳ gói hiện tại \(đến hết ngày \d{2}/\d{2}/\d{4}\)\. Vui lòng liên hệ quản trị trường để nâng cấp gói\.$", body.Message);
        Assert.Equal(calls, factory.Ai.Calls);                       // không gọi dịch vụ AI
        Assert.Single(await RowsAsync(lessonId));                    // không thêm dòng nhật ký

        // Trường khác không bị ảnh hưởng.
        var (school2, lesson2, _) = await LessonAsync(2);
        Assert.Equal(19, (await _k.ComposeAsync(school2.TeacherToken, lesson2)).AiQuota.Remaining);
    }

    [Fact]
    public async Task Admin_translation_rows_and_other_period_rows_do_not_count_against_the_school()
    {
        var (school, lessonId, heritageId) = await LessonAsync(2);
        var adminId = await factory.WithDbAsync(db => db.Users.Where(u => u.Username == ApiFactory.AdminUsername).Select(u => u.Id).SingleAsync());
        await factory.WithDbAsync(async db =>
        {
            // Dịch di sản do quản trị hệ thống gọi: gắn với di sản, không tính hạn mức trường nào.
            db.AiGenerations.Add(new AiGeneration
            {
                TaskType = AiTaskType.HERITAGE_TRANSLATION, HeritageId = heritageId, RequestedBy = adminId, Status = AiTaskStatus.SUCCEEDED,
            });
            // Lượt cũ trước kỳ của gói: không tính.
            db.AiGenerations.Add(new AiGeneration
            {
                TaskType = AiTaskType.LESSON_SUMMARY, LessonId = lessonId, RequestedBy = school.Teacher.Id, Status = AiTaskStatus.SUCCEEDED,
                CreatedAt = DateTime.UtcNow.AddDays(-90),
            });
            return await db.SaveChangesAsync();
        });

        var quota = await DiscoveryTestKit.Expect(Http.GetAsync<AiQuotaDto>($"{T}/ai-quota", school.TeacherToken));
        Assert.Equal((0, 20), (quota.Used, quota.Remaining));
    }

    [Fact]
    public async Task A_school_without_an_effective_plan_cannot_call_ai_and_no_row_is_written()
    {
        var (school, lessonId, _) = await LessonAsync(2);
        await factory.WithDbAsync(async db =>
        {
            foreach (var s in await db.Subscriptions.Where(x => x.OrganizationId == school.OrgId).ToListAsync()) s.Status = SubscriptionStatus.CANCELLED;
            return await db.SaveChangesAsync();
        });

        var (res, body) = await Http.PostAsync<object>($"{T}/{lessonId}/ai/compose", new { }, school.TeacherToken);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "PLAN_REQUIRED"), (res.StatusCode, body.ErrorCode));
        Assert.StartsWith("Không thể soạn nội dung bằng AI.", body.Message);
        Assert.Empty(await RowsAsync(lessonId));
        Assert.Equal(0, factory.Ai.Calls);
        var quota = await DiscoveryTestKit.Expect(Http.GetAsync<AiQuotaDto>($"{T}/ai-quota", school.TeacherToken));
        Assert.False(quota.HasEffectivePlan);
        Assert.Null(quota.Remaining);
    }

    [Fact]
    public async Task Archived_lessons_and_invalid_lengths_are_refused_and_published_lessons_can_be_recomposed()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(2);
        var token = school.TeacherToken;
        var h = await _k.HeritageAsync("Hoàng thành", events: 4);
        var lesson = await _k.PublishedLessonAsync(token, schoolClass.Id, [h.Id]);

        var (badLength, badBody) = await Http.PostAsync<object>($"{T}/{lesson.Id}/ai/compose", new { length = "HUGE" }, token);
        Assert.Equal(HttpStatusCode.BadRequest, badLength.StatusCode);
        Assert.NotNull(badBody.ErrorCode);

        // Bài đã xuất bản gọi AI được, kết quả vẫn chỉ là bản nháp (bài giữ nguyên).
        var draft = await _k.ComposeAsync(token, lesson.Id, LessonLength.SHORT);
        Assert.Equal(lesson.Summary, (await _k.GetAsync(token, lesson.Id)).Summary);
        Assert.NotEqual(lesson.Summary, draft.Summary);

        await DiscoveryTestKit.Expect(Http.PostAsync<LessonDetailDto>($"{T}/{lesson.Id}/archive", null, token));
        var before = factory.Ai.Calls;
        var (res, body) = await Http.PostAsync<object>($"{T}/{lesson.Id}/ai/compose", new { }, token);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "BLOCKED"), (res.StatusCode, body.ErrorCode));
        Assert.Contains("Bài học đã lưu trữ", body.Message);
        Assert.Equal(before, factory.Ai.Calls);
    }

    // ---- Dịch vụ AI lỗi ----

    [Fact]
    public async Task A_failing_ai_service_is_retried_twice_inside_the_same_log_row()
    {
        var (school, lessonId, _) = await LessonAsync(2);
        factory.Ai.Behavior = (call, request, ct) => call < 3
            ? throw new AiServiceException("Dịch vụ AI trả mã 503.", retryable: true)
            : new SimulatedLessonAiClient().ComposeLessonAsync(request, "x", ct);

        var draft = await _k.ComposeAsync(school.TeacherToken, lessonId);

        Assert.Equal(3, factory.Ai.Calls);
        Assert.Equal(1, draft.AiQuota.Used);
        var row = Assert.Single(await RowsAsync(lessonId));
        Assert.Equal(AiTaskStatus.SUCCEEDED, row.Status);
        Assert.Equal(draft.GenerationId, row.Id);
    }

    [Fact]
    public async Task When_every_attempt_fails_the_row_is_failed_the_turn_is_not_counted_and_the_lesson_is_untouched()
    {
        var (school, lessonId, _) = await LessonAsync(2);
        var token = school.TeacherToken;
        await DiscoveryTestKit.Expect(Http.PutAsync<LessonDetailDto>($"{T}/{lessonId}", new { summary = "Nội dung giáo viên tự viết." }, token));
        factory.Ai.Behavior = (_, _, _) => throw new AiServiceException("Không kết nối được dịch vụ AI.", retryable: true);

        var (res, body) = await Http.PostAsync<object>($"{T}/{lessonId}/ai/compose", new { }, token);

        Assert.Equal((HttpStatusCode.ServiceUnavailable, "AI_UNAVAILABLE"), (res.StatusCode, body.ErrorCode));
        Assert.Equal("Dịch vụ AI đang bận. Lượt này không bị tính. Bạn có thể thử lại sau hoặc tự nhập nội dung; nội dung bài học được giữ nguyên.", body.Message);
        Assert.Equal(3, factory.Ai.Calls);                           // 1 lần đầu + tối đa 2 lần thử lại
        var row = Assert.Single(await RowsAsync(lessonId));
        Assert.Equal(AiTaskStatus.FAILED, row.Status);
        Assert.Equal("Không kết nối được dịch vụ AI.", row.ErrorMessage);
        Assert.NotNull(row.CompletedAt);
        var detail = await _k.GetAsync(token, lessonId);
        Assert.Equal(("Nội dung giáo viên tự viết.", 0, 20), (detail.Summary, detail.AiQuota.Used, detail.AiQuota.Remaining));
    }

    [Fact]
    public async Task A_client_error_from_the_ai_service_is_not_retried()
    {
        var (school, lessonId, _) = await LessonAsync(2);
        factory.Ai.Behavior = (_, _, _) => throw new AiServiceException("Dịch vụ AI trả mã 422.", retryable: false);

        var (res, body) = await Http.PostAsync<object>($"{T}/{lessonId}/ai/compose", new { }, school.TeacherToken);

        Assert.Equal((HttpStatusCode.ServiceUnavailable, "AI_UNAVAILABLE"), (res.StatusCode, body.ErrorCode));
        Assert.Equal(1, factory.Ai.Calls);
        Assert.Equal(AiTaskStatus.FAILED, Assert.Single(await RowsAsync(lessonId)).Status);
    }

    [Fact]
    public async Task A_call_that_exceeds_the_wait_time_is_cancelled_and_retried()
    {
        var (school, lessonId, _) = await LessonAsync(2);
        factory.Ai.Behavior = async (call, request, ct) =>
        {
            if (call == 1) await Task.Delay(Timeout.Infinite, ct); // chờ tới khi bị hủy (Ai:AttemptTimeoutSeconds = 1 trong kiểm thử)
            return await new SimulatedLessonAiClient().ComposeLessonAsync(request, "x", ct);
        };

        var started = DateTime.UtcNow;
        var draft = await _k.ComposeAsync(school.TeacherToken, lessonId);

        Assert.InRange((DateTime.UtcNow - started).TotalSeconds, 0.9, 10);
        Assert.Equal(2, factory.Ai.Calls);
        Assert.Equal(1, draft.AiQuota.Used);
        Assert.Equal(AiTaskStatus.SUCCEEDED, Assert.Single(await RowsAsync(lessonId)).Status);
    }

    [Fact]
    public async Task Output_that_breaks_the_contract_is_cleaned_or_retried()
    {
        var (school, lessonId, heritageId) = await LessonAsync(3);
        var token = school.TeacherToken;

        // Nội dung rỗng là lỗi đầu ra: thử lại như lỗi mạng.
        factory.Ai.Behavior = async (call, request, ct) =>
        {
            var good = await new SimulatedLessonAiClient().ComposeLessonAsync(request, "x", ct);
            return call == 1 ? good with { Content = "   " } : good;
        };
        await _k.ComposeAsync(token, lessonId);
        Assert.Equal(2, factory.Ai.Calls);

        // Đoạn trích không nguyên văn hoặc không nguồn bị bỏ; ý chính cắt về 7 ý, mỗi ý 300 ký tự; câu hỏi rỗng bị bỏ.
        factory.Ai.Reset();
        factory.Ai.Behavior = async (_, request, ct) =>
        {
            var good = await new SimulatedLessonAiClient().ComposeLessonAsync(request, "x", ct);
            return good with
            {
                SourceQuotes = [.. good.SourceQuotes, new AiSourceQuote(heritageId, "Câu này do AI bịa ra, không có trong nguồn.", "Nguồn bịa"),
                    new AiSourceQuote(heritageId, good.SourceQuotes[0].Quote, "  ")],
                KeyPoints = Enumerable.Range(1, 10).Select(i => new string('ý', 400) + i).ToList(),
                Steps = [.. good.Steps, new AiComposedStep(LessonSectionType.REFLECTION, " ", null, null, null, null),
                    new AiComposedStep(LessonSectionType.STORY, "Bước của cấp 1 không dùng ở cấp 3", null, null, null, null)],
            };
        };
        var draft = await _k.ComposeAsync(token, lessonId);
        Assert.Equal(1, factory.Ai.Calls);
        var heritage = await _k.HeritageOnlyAsync(heritageId);
        Assert.All(draft.Sections.Where(s => s.Type == LessonSectionType.SOURCE_EXCERPT), s =>
        {
            Assert.Contains(s.Body!, heritage.Content!);
            Assert.False(string.IsNullOrWhiteSpace(s.SourceCitation));
        });
        Assert.DoesNotContain(draft.Sections, s => s.Type == LessonSectionType.STORY);
        Assert.DoesNotContain(draft.Sections, s => string.IsNullOrWhiteSpace(s.Body));
        Assert.Equal(7, draft.KeyPoints.Count);
        Assert.All(draft.KeyPoints, k => Assert.Equal(300, k.Length));
    }

    [Fact]
    public async Task The_correlation_id_of_the_request_reaches_the_ai_service()
    {
        var (school, lessonId, _) = await LessonAsync(2);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{T}/{lessonId}/ai/compose") { Content = JsonContent.Create(new { }) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", school.TeacherToken);
        request.Headers.Add("X-Correlation-Id", "kiem-thu-ai-0001");

        var response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("kiem-thu-ai-0001", factory.Ai.LastCorrelationId);
    }

    private async Task LimitAiAsync(long orgId, int max) =>
        await factory.WithDbAsync(async db =>
        {
            var plan = new Plan { Name = "Gói thử " + DiscoveryTestKit.Unique(), Price = 0m, DurationDays = 30, MaxTeachers = 5, MaxAiRequests = max };
            db.Plans.Add(plan);
            await db.SaveChangesAsync();
            foreach (var s in await db.Subscriptions.Where(x => x.OrganizationId == orgId).ToListAsync()) s.PlanId = plan.Id;
            return await db.SaveChangesAsync();
        });
}
