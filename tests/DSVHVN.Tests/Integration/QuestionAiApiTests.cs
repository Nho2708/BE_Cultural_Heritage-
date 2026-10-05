using System.Net;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// AI sinh câu hỏi theo dạng và cấp qua dịch vụ AI giả lập: dạng đúng cấp của lớp, câu vào ngân hàng ở trạng thái chờ duyệt, ghi <c>ai_generations</c>,
/// tính hạn mức lượt AI của gói, kiểm kết quả (câu sai bị bỏ), thử lại khi lỗi và lượt thất bại không bị tính; duyệt, loại, sửa câu chờ duyệt.
/// </summary>
public sealed class QuestionAiApiTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly QuestionTestKit _k = new(factory);
    private HttpClient Http => _k.Http;
    private const string T = QuestionTestKit.Teacher;
    private const string L = QuestionTestKit.Lessons;

    public Task InitializeAsync()
    {
        factory.QuestionAi.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        factory.QuestionAi.Reset();
        return Task.CompletedTask;
    }

    private Task<List<AiGeneration>> RowsAsync(long lessonId) =>
        factory.WithDbAsync(db => db.AiGenerations.AsNoTracking().Where(g => g.LessonId == lessonId).OrderBy(g => g.Id).ToListAsync());

    private Task<AiQuotaDto> QuotaAsync(string token) =>
        DiscoveryTestKit.Expect(Http.GetAsync<AiQuotaDto>($"{L}/ai-quota", token));

    // ---- Dạng đúng cấp của lớp ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task By_default_every_type_of_the_class_level_is_generated_and_nothing_else(int level)
    {
        var setup = await _k.LessonAsync(level);

        var batch = await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 12 });

        Assert.Equal((level, 12, 12, 0), (batch.Level, batch.Requested, batch.Received, batch.Dropped));
        Assert.Equal(SchoolLevels.NameOf(level), batch.LevelName);
        Assert.Equal(QuestionTypes.ForLevel(level).Order(), batch.Questions.Select(q => q.QuestionType).Distinct().Order());
        Assert.All(batch.Questions, q =>
        {
            Assert.Equal((QuestionSource.AI, ReviewStatus.PENDING, batch.GenerationId), (q.Source, q.ReviewStatus, q.AiGenerationId));
            Assert.Equal((setup.School.Teacher.Id, setup.Heritage.Id, DifficultyLevel.MEDIUM), (q.CreatedBy, q.HeritageId, q.DifficultyLevel));
            Assert.Equal([level], q.Levels);                                                    // cấp phù hợp là cấp của lớp
            Assert.Null(q.ReviewedBy);
            Assert.True(q.PartsTotal >= 1);
            Assert.False(string.IsNullOrWhiteSpace(q.Explanation));
        });
        Assert.StartsWith("Đã tạo bản nháp câu hỏi. Hãy kiểm tra độ chính xác trước khi dùng. Còn 19 lượt AI trong kỳ.", batch.Message);
    }

    [Fact]
    public async Task The_chosen_types_difficulty_and_count_are_honoured()
    {
        var setup = await _k.LessonAsync(2);

        var batch = await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 4, difficultyLevel = "HARD", types = new[] { "FILL_BLANK", "ORDERING" } });

        Assert.Equal(4, batch.Received);
        Assert.All(batch.Questions, q =>
        {
            Assert.Contains(q.QuestionType, new[] { QuestionType.FILL_BLANK, QuestionType.ORDERING });
            Assert.Equal(DifficultyLevel.HARD, q.DifficultyLevel);
        });
        Assert.Equal(["FILL_BLANK", "ORDERING"], factory.QuestionAi.LastRequest!.Types.Select(t => t.ToString()).Order());
        Assert.Equal((4, DifficultyLevel.HARD, 2), (factory.QuestionAi.LastRequest.Count, factory.QuestionAi.LastRequest.Difficulty, factory.QuestionAi.LastRequest.GradeLevel));
    }

    [Fact]
    public async Task Level_one_picture_types_only_use_pictures_from_the_heritage_library()
    {
        var setup = await _k.LessonAsync(1);

        var batch = await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 8 });

        var used = batch.Questions.SelectMany(q => q.Images.Select(i => i.Url)).ToList();
        Assert.NotEmpty(used);
        Assert.All(used, url => Assert.Contains(url, setup.Images));
        Assert.Contains(batch.Questions, q => q.QuestionType == QuestionType.IMAGE_CHOICE);
        Assert.All(batch.Questions.Where(q => q.QuestionType == QuestionType.TRUE_FALSE), q => Assert.NotNull(q.ImageUrl));    // Đúng/Sai cấp 1 luôn có hình
    }

    [Theory]
    [InlineData(1, "SINGLE_CHOICE")]
    [InlineData(1, "FILL_BLANK")]
    [InlineData(2, "IMAGE_CHOICE")]
    [InlineData(2, "MULTI_TRUE_FALSE")]
    [InlineData(3, "TRUE_FALSE")]
    [InlineData(3, "ORDERING")]
    [InlineData(3, "MATCHING")]
    public async Task A_type_that_the_class_level_cannot_use_is_refused_before_any_ai_turn_is_spent(int level, string type)
    {
        var setup = await _k.LessonAsync(level);

        var (res, body) = await Http.PostAsync<object>($"{L}/{setup.LessonId}/ai/questions", new { types = new[] { type } }, setup.Token);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("types", body.Errors!.Keys);
        Assert.Contains($"lớp cấp {level}", body.Errors["types"]);
        Assert.Empty(await RowsAsync(setup.LessonId));
        Assert.Equal(0, factory.QuestionAi.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(-3)]
    public async Task The_count_must_be_between_one_and_twenty(int count)
    {
        var setup = await _k.LessonAsync(2);
        var (res, body) = await Http.PostAsync<object>($"{L}/{setup.LessonId}/ai/questions", new { count }, setup.Token);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("count", body.Errors!.Keys);
        Assert.Empty(await RowsAsync(setup.LessonId));
    }

    [Fact]
    public async Task Unknown_values_are_a_400()
    {
        var setup = await _k.LessonAsync(2);
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.PostAsync<object>($"{L}/{setup.LessonId}/ai/questions", new { difficultyLevel = "IMPOSSIBLE" }, setup.Token)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.PostAsync<object>($"{L}/{setup.LessonId}/ai/questions", new { types = new[] { "ESSAY" } }, setup.Token)).Response.StatusCode);
        Assert.Empty(await RowsAsync(setup.LessonId));
    }

    // ---- Di sản của bài ----

    [Fact]
    public async Task A_single_heritage_of_the_lesson_can_be_chosen_and_only_it_is_sent()
    {
        var (school, schoolClass) = await _k.Lesson.SchoolWithClassAsync(2);
        var (first, _) = await _k.HeritageAsync("Hoàng thành");
        var (second, _) = await _k.HeritageAsync("Văn Miếu");
        var lesson = await _k.Lesson.CreateLessonAsync(school.TeacherToken, schoolClass.Id, [first.Id, second.Id]);

        var all = await _k.GenerateAsync(school.TeacherToken, lesson.Id, new { count = 8, types = new[] { "FILL_BLANK", "SINGLE_CHOICE" } });
        Assert.Equal([first.Id, second.Id], all.Questions.Select(q => q.HeritageId).Distinct().Order());
        Assert.Equal(2, factory.QuestionAi.LastRequest!.Heritages.Count);

        var only = await _k.GenerateAsync(school.TeacherToken, lesson.Id, new { count = 4, heritageId = second.Id });
        Assert.All(only.Questions, q => Assert.Equal(second.Id, q.HeritageId));
        Assert.Equal([second.Id], factory.QuestionAi.LastRequest.Heritages.Select(h => h.HeritageId));

        var (res, body) = await Http.PostAsync<object>($"{L}/{lesson.Id}/ai/questions", new { heritageId = 99_999_999 }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("heritageId", body.Errors!.Keys);
    }

    [Fact]
    public async Task A_lesson_without_a_published_heritage_or_an_archived_lesson_cannot_ask_for_questions()
    {
        var setup = await _k.LessonAsync(2);
        var published = await _k.Lesson.PublishedLessonAsync(setup.Token, setup.ClassId, [setup.Heritage.Id]);
        // Bài đã xuất bản gọi được; bài lưu trữ thì không.
        await _k.GenerateAsync(setup.Token, published.Id, new { count = 1 });
        await DiscoveryTestKit.Expect(Http.PostAsync<LessonDetailDto>($"{L}/{published.Id}/archive", null, setup.Token));
        var (archived, archivedBody) = await Http.PostAsync<object>($"{L}/{published.Id}/ai/questions", new { }, setup.Token);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "BLOCKED"), (archived.StatusCode, archivedBody.ErrorCode));
        Assert.StartsWith("Không thể sinh câu hỏi bằng AI. Bài học đã lưu trữ.", archivedBody.Message);

        // Di sản bị ẩn: bài không còn di sản đã xuất bản.
        await _k.Lesson.ArchiveHeritageAsync(setup.Heritage.Id);
        var calls = factory.QuestionAi.Calls;
        var (noHeritage, noBody) = await Http.PostAsync<object>($"{L}/{setup.LessonId}/ai/questions", new { }, setup.Token);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "BLOCKED"), (noHeritage.StatusCode, noBody.ErrorCode));
        Assert.Contains("ít nhất một di sản đã xuất bản", noBody.Message);
        Assert.Equal(calls, factory.QuestionAi.Calls);
    }

    [Fact]
    public async Task Lessons_of_other_teachers_are_not_found_and_other_roles_are_forbidden()
    {
        var setup = await _k.LessonAsync(2);
        var (_, colleague) = await _k.Lesson.Classroom.AnotherTeacherAsync(setup.School);
        var student = await _k.Lesson.StudentTokenAsync(setup.Token, setup.ClassId);
        var url = $"{L}/{setup.LessonId}/ai/questions";

        Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsync<object>(url, new { }, colleague)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.PostAsync<object>(url, new { }, setup.School.OrgAdminToken)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.PostAsync<object>(url, new { }, student)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.PostAsync<object>(url, new { }, await _k.Lesson.Discovery.Api.AdminTokenAsync())).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.PostAsync<object>(url, new { }, null)).Response.StatusCode);
        Assert.Equal(0, factory.QuestionAi.Calls);
    }

    // ---- Nhật ký và hạn mức ----

    [Fact]
    public async Task Each_call_writes_one_generation_row_and_costs_one_turn_whatever_the_number_of_questions()
    {
        var setup = await _k.LessonAsync(2);

        var one = await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 1 });
        var many = await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 10 });

        var rows = await RowsAsync(setup.LessonId);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal((AiTaskType.QUIZ_GENERATION, AiTaskStatus.SUCCEEDED, SimulatedQuestionAiClient.Name, setup.School.Teacher.Id),
            (r.TaskType, r.Status, r.Model, r.RequestedBy)));
        Assert.All(rows, r => Assert.NotNull(r.CompletedAt));
        Assert.Equal([one.GenerationId, many.GenerationId], rows.Select(r => r.Id));
        Assert.Equal((19, 18), (one.AiQuota.Remaining!.Value, many.AiQuota.Remaining!.Value));
        Assert.Equal((2, 18), ((await QuotaAsync(setup.Token)).Used, (await QuotaAsync(setup.Token)).Remaining!.Value));
        Assert.Equal((18, 2), ((await DiscoveryTestKit.Expect(Http.GetAsync<AiQuotaDto>($"{L}/{setup.LessonId}/ai/questions/quota", setup.Token))).Remaining!.Value,
            (await QuotaAsync(setup.Token)).Used));

        // Câu sinh ra truy được về lần gọi: input_snapshot chứa dữ liệu di sản, raw_output chứa câu AI trả.
        Assert.Contains("Hoàng thành", rows[1].InputSnapshot);
        Assert.Contains("questions", rows[1].RawOutput);
        Assert.Equal(many.Received, (await _k.RowsAsync(many.GenerationId)).Count);
    }

    [Fact]
    public async Task Regenerating_is_a_new_turn_and_adds_more_pending_questions()
    {
        var setup = await _k.LessonAsync(3);
        var first = await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 3 });
        var second = await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 3 });

        Assert.NotEqual(first.GenerationId, second.GenerationId);
        Assert.Equal(first.Received + second.Received, (await _k.ListAsync(setup.Token, "scope=PENDING")).TotalItems);
        Assert.Equal(18, (await QuotaAsync(setup.Token)).Remaining!.Value);
    }

    [Fact]
    public async Task Nothing_personal_goes_to_the_ai_service()
    {
        var setup = await _k.LessonAsync(2);
        await _k.Lesson.StudentTokenAsync(setup.Token, setup.ClassId, "Trần Thị Bình");

        await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 2 });

        var sent = System.Text.Json.JsonSerializer.Serialize(factory.QuestionAi.LastRequest, HttpTestExtensions.Json);
        var snapshot = (await RowsAsync(setup.LessonId)).Single().InputSnapshot!;
        foreach (var secret in new[] { "Trần Thị Bình", setup.School.Teacher.FullName!, setup.School.Teacher.Email!, "Lớp thử" })
        {
            Assert.DoesNotContain(secret, sent);
            Assert.DoesNotContain(secret, snapshot);
        }
    }

    [Fact]
    public async Task The_quota_is_the_whole_schools_and_stops_the_call_when_used_up()
    {
        var setup = await _k.LessonAsync(2);
        await LimitAiAsync(setup.School.OrgId, 2);
        var otherLesson = await _k.Lesson.CreateLessonAsync(setup.Token, setup.ClassId, [setup.Heritage.Id], "Bài khác");

        await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 2 });
        await DiscoveryTestKit.Expect(Http.PostAsync<LessonDraftDto>($"{L}/{otherLesson.Id}/ai/compose", new { }, setup.Token));      // soạn bài cũng tốn lượt chung
        var calls = factory.QuestionAi.Calls;

        var (res, body) = await Http.PostAsync<object>($"{L}/{setup.LessonId}/ai/questions", new { }, setup.Token);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "AI_QUOTA_REACHED"), (res.StatusCode, body.ErrorCode));
        Assert.Matches(@"^Trường đã dùng hết 2 lượt AI của kỳ gói hiện tại \(đến hết ngày \d{2}/\d{2}/\d{4}\)\.", body.Message);
        Assert.Equal(calls, factory.QuestionAi.Calls);
        Assert.Single(await RowsAsync(setup.LessonId));
    }

    [Fact]
    public async Task A_school_without_an_effective_plan_cannot_generate_questions_and_no_row_is_written()
    {
        var setup = await _k.LessonAsync(2);
        await factory.WithDbAsync(async db =>
        {
            foreach (var s in await db.Subscriptions.Where(x => x.OrganizationId == setup.School.OrgId).ToListAsync()) s.Status = SubscriptionStatus.CANCELLED;
            return await db.SaveChangesAsync();
        });

        var (res, body) = await Http.PostAsync<object>($"{L}/{setup.LessonId}/ai/questions", new { }, setup.Token);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "PLAN_REQUIRED"), (res.StatusCode, body.ErrorCode));
        Assert.StartsWith("Không thể sinh câu hỏi bằng AI.", body.Message);
        Assert.Empty(await RowsAsync(setup.LessonId));
        Assert.Equal(0, factory.QuestionAi.Calls);
        // Câu đã có trong ngân hàng vẫn xem và tạo thủ công được khi hết gói.
        var samples = QuestionTestKit.Samples(setup.Heritage.Id, setup.Images);
        Assert.NotNull(await _k.CreateAsync(setup.Token, samples[QuestionType.SINGLE_CHOICE]));
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

    // ---- Lỗi dịch vụ AI ----

    [Fact]
    public async Task A_retryable_failure_is_retried_twice_then_fails_without_cost_or_questions()
    {
        var setup = await _k.LessonAsync(2);
        factory.QuestionAi.Behavior = (_, _, _) => throw new AiServiceException("Dịch vụ AI trả mã 503.", retryable: true);

        var (res, body) = await Http.PostAsync<object>($"{L}/{setup.LessonId}/ai/questions", new { count = 3 }, setup.Token);

        Assert.Equal((HttpStatusCode.ServiceUnavailable, "AI_UNAVAILABLE"), (res.StatusCode, body.ErrorCode));
        Assert.Equal("Dịch vụ AI đang bận. Lượt này không bị tính. Bạn có thể thử lại sau hoặc tự nhập nội dung; nội dung bài học được giữ nguyên.", body.Message);
        Assert.Equal(3, factory.QuestionAi.Calls);                                              // 1 lần đầu + 2 lần thử lại
        var row = Assert.Single(await RowsAsync(setup.LessonId));
        Assert.Equal((AiTaskStatus.FAILED, "Dịch vụ AI trả mã 503."), (row.Status, row.ErrorMessage));
        Assert.NotNull(row.CompletedAt);
        Assert.Equal((0, 20), ((await QuotaAsync(setup.Token)).Used, (await QuotaAsync(setup.Token)).Remaining!.Value));       // không tính lượt
        Assert.Equal(0, (await _k.ListAsync(setup.Token, "scope=PENDING")).TotalItems);
    }

    [Fact]
    public async Task A_non_retryable_failure_stops_at_once_and_a_later_try_works()
    {
        var setup = await _k.LessonAsync(2);
        factory.QuestionAi.Behavior = (_, _, _) => throw new AiServiceException("Sai khóa nội bộ.", retryable: false);

        var (res, body) = await Http.PostAsync<object>($"{L}/{setup.LessonId}/ai/questions", new { }, setup.Token);
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "AI_UNAVAILABLE"), (res.StatusCode, body.ErrorCode));
        Assert.Equal(1, factory.QuestionAi.Calls);

        factory.QuestionAi.Reset();
        var batch = await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 2 });
        Assert.Equal(2, batch.Received);
        var rows = await RowsAsync(setup.LessonId);
        Assert.Equal([AiTaskStatus.FAILED, AiTaskStatus.SUCCEEDED], rows.Select(r => r.Status!.Value));
        Assert.Equal(1, (await QuotaAsync(setup.Token)).Used);
    }

    [Fact]
    public async Task A_second_attempt_that_works_after_a_timeout_gives_the_questions()
    {
        var setup = await _k.LessonAsync(2);
        factory.QuestionAi.Behavior = async (call, request, ct) =>
        {
            if (call == 1) await Task.Delay(TimeSpan.FromSeconds(30), ct);                      // quá thời gian chờ 1 giây của kiểm thử
            return await new SimulatedQuestionAiClient().GenerateQuestionsAsync(request, "x", ct);
        };

        var batch = await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 2 });

        Assert.Equal(2, batch.Received);
        Assert.Equal(2, factory.QuestionAi.Calls);
        var row = Assert.Single(await RowsAsync(setup.LessonId));
        Assert.Equal(AiTaskStatus.SUCCEEDED, row.Status);                                       // một dòng nhật ký cho cả hai lần thử
    }

    // ---- Kiểm kết quả AI trả ----

    [Fact]
    public async Task Questions_that_break_the_rules_are_dropped_and_the_response_says_how_many_were_received()
    {
        var setup = await _k.LessonAsync(2);
        var h = setup.Heritage.Id;
        factory.QuestionAi.Behavior = (_, request, _) => Task.FromResult(new AiQuestionResult(
        [
            Good(h, "Hoàng thành Thăng Long thuộc loại di sản nào sau đây?"),
            Good(h, "Câu có hai đáp án đúng cho di sản Hoàng thành?") with { Options = [new("A", null, true), new("B", null, true), new("C", null, false), new("D", null, false)] },
            Good(h, "Câu thiếu giải thích về di sản Hoàng thành này?") with { Explanation = null },
            Good(999_999, "Câu gắn di sản không có trong bài học đã gửi?"),
            new AiGeneratedQuestion(h, QuestionType.IMAGE_CHOICE, "Em hãy chọn hình có tên: Cổng chính.", "Hình thứ nhất.", Options: [new("Một", "https://nguoi-la.example/1.jpg", true), new("Hai", "https://nguoi-la.example/2.jpg", false), new("Ba", "https://nguoi-la.example/3.jpg", false)]),
            Good(h, "Hoàng thành Thăng Long được ghi danh vào thời gian nào?"),
        ], "mo-hinh-thu", new AiUsage(10, 20)));

        var batch = await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 6, types = new[] { "SINGLE_CHOICE", "FILL_BLANK" } });

        Assert.Equal((6, 2, 4), (batch.Requested, batch.Received, batch.Dropped));
        Assert.Equal(["Hoàng thành Thăng Long thuộc loại di sản nào sau đây?", "Hoàng thành Thăng Long được ghi danh vào thời gian nào?"], batch.Questions.Select(q => q.Content));
        Assert.Equal("mo-hinh-thu", batch.Model);
        Assert.Equal("mo-hinh-thu", (await RowsAsync(setup.LessonId)).Single().Model);
        Assert.Equal(2, (await _k.ListAsync(setup.Token, "scope=PENDING")).TotalItems);
    }

    [Fact]
    public async Task Extra_questions_beyond_the_requested_count_and_duplicates_and_unrequested_types_are_not_kept()
    {
        var setup = await _k.LessonAsync(2);
        var h = setup.Heritage.Id;
        factory.QuestionAi.Behavior = (_, _, _) => Task.FromResult(new AiQuestionResult(
        [
            Good(h, "Câu thứ nhất về Hoàng thành Thăng Long là gì?"),
            Good(h, "Câu thứ nhất về Hoàng thành Thăng Long là gì?"),
            new AiGeneratedQuestion(h, QuestionType.TRUE_FALSE, "Câu Đúng/Sai không được yêu cầu về Hoàng thành.", "Giải thích.", AnswerIsTrue: true),
            Good(h, "Câu thứ hai về Hoàng thành Thăng Long là gì?"),
            Good(h, "Câu thứ ba về Hoàng thành Thăng Long là gì?"),
        ], "mo-hinh-thu", new AiUsage(1, 1)));

        var batch = await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 2, types = new[] { "SINGLE_CHOICE" } });

        Assert.Equal(["Câu thứ nhất về Hoàng thành Thăng Long là gì?", "Câu thứ hai về Hoàng thành Thăng Long là gì?"], batch.Questions.Select(q => q.Content));
        Assert.Equal((2, 2, 3), (batch.Requested, batch.Received, batch.Dropped));
    }

    [Fact]
    public async Task A_result_with_no_usable_question_is_retried_like_a_network_error_and_then_fails_without_cost()
    {
        var setup = await _k.LessonAsync(2);
        var h = setup.Heritage.Id;
        factory.QuestionAi.Behavior = (_, _, _) => Task.FromResult(new AiQuestionResult(
            [Good(h, "Câu hỏi thiếu giải thích về Hoàng thành?") with { Explanation = "" }], "mo-hinh-thu", new AiUsage(1, 1)));

        var (res, body) = await Http.PostAsync<object>($"{L}/{setup.LessonId}/ai/questions", new { count = 1, types = new[] { "SINGLE_CHOICE" } }, setup.Token);

        Assert.Equal((HttpStatusCode.ServiceUnavailable, "AI_UNAVAILABLE"), (res.StatusCode, body.ErrorCode));
        Assert.Equal(3, factory.QuestionAi.Calls);
        Assert.Equal(AiTaskStatus.FAILED, (await RowsAsync(setup.LessonId)).Single().Status);
        Assert.Equal(0, (await QuotaAsync(setup.Token)).Used);
        Assert.Equal(0, (await _k.ListAsync(setup.Token, "scope=PENDING")).TotalItems);
    }

    private static AiGeneratedQuestion Good(long heritageId, string content) => new(
        heritageId, QuestionType.SINGLE_CHOICE, content, "Đáp án đúng nằm trong nội dung di sản.",
        Options: [new("Di sản văn hóa", null, true), new("Di sản thiên nhiên", null, false), new("Di sản hỗn hợp", null, false), new("Di sản tư liệu", null, false)]);

    // ---- Duyệt, loại, sửa câu chờ duyệt ----

    [Fact]
    public async Task Approving_a_pending_question_makes_it_shared_and_records_who_and_when()
    {
        var setup = await _k.LessonAsync(2);
        var colleague = (await _k.Lesson.Classroom.AnotherTeacherAsync(setup.School)).Token;
        var batch = await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 3 });
        var question = batch.Questions[0];
        Assert.True(question.Access is { CanApprove: true, CanReject: true, CanEdit: true, CanDuplicate: false });
        Assert.DoesNotContain((await _k.ListAsync(colleague, "pageSize=100")).Items, i => i.Id == question.Id);

        var approved = await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{T}/{question.Id}/approve", null, setup.Token));

        Assert.Equal((ReviewStatus.APPROVED, setup.School.Teacher.Id, QuestionSource.AI), (approved.ReviewStatus, approved.ReviewedBy, approved.Source));
        Assert.NotNull(approved.ReviewedAt);
        Assert.Equal(batch.GenerationId, approved.AiGenerationId);                                // vẫn truy được về lần gọi AI
        Assert.Contains((await _k.ListAsync(colleague, "pageSize=100")).Items, i => i.Id == question.Id);
        Assert.Equal(batch.Received - 1, (await _k.ListAsync(setup.Token, "scope=PENDING")).TotalItems);
        // Duyệt lại câu đã duyệt không đổi gì.
        var again = await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{T}/{question.Id}/approve", null, setup.Token));
        Assert.Equal(approved.ReviewedAt, again.ReviewedAt);
    }

    [Fact]
    public async Task Several_questions_can_be_approved_at_once_and_the_ones_that_cannot_are_skipped_with_a_reason()
    {
        var setup = await _k.LessonAsync(2);
        var batch = await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 4 });
        var ids = batch.Questions.Select(q => q.Id).ToList();
        await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{T}/{ids[3]}/reject", null, setup.Token));

        var result = await DiscoveryTestKit.Expect(Http.PostAsync<ApproveQuestionsResultDto>($"{T}/approve", new { questionIds = new[] { ids[0], ids[1], ids[2], 99_999_999L, ids[0] } }, setup.Token));

        Assert.Equal(ids.Take(3), result.Approved);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(99_999_999L, skipped.QuestionId);
        Assert.Contains("Không tìm thấy câu hỏi", skipped.Reason);
        Assert.Equal("Đã duyệt 3 câu, bỏ qua 1 câu.", result.Message);
        Assert.Equal(3, (await _k.ListAsync(setup.Token, "scope=MINE&status=APPROVED")).TotalItems);

        // Câu đã loại duyệt lại được trong lần sau.
        var rest = await DiscoveryTestKit.Expect(Http.PostAsync<ApproveQuestionsResultDto>($"{T}/approve", new { questionIds = new[] { ids[3] } }, setup.Token));
        Assert.Equal((1, 0, "Duyệt 1 câu hỏi thành công."), (rest.Approved.Count, rest.Skipped.Count, rest.Message));

        foreach (var bad in new object[] { new { questionIds = Array.Empty<long>() }, new { }, new { questionIds = Enumerable.Range(1, 101).Select(i => (long)i).ToArray() } })
            Assert.Equal(HttpStatusCode.BadRequest, (await Http.PostAsync<object>($"{T}/approve", bad, setup.Token)).Response.StatusCode);
    }

    [Fact]
    public async Task Other_teachers_questions_in_a_batch_are_skipped_not_approved()
    {
        var setup = await _k.LessonAsync(2);
        var colleagueSetup = await _k.LessonAsync(2, "Văn Miếu");
        var mine = (await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 1 })).Questions[0];
        var theirs = (await _k.GenerateAsync(colleagueSetup.Token, colleagueSetup.LessonId, new { count = 1 })).Questions[0];

        var result = await DiscoveryTestKit.Expect(Http.PostAsync<ApproveQuestionsResultDto>($"{T}/approve", new { questionIds = new[] { mine.Id, theirs.Id } }, setup.Token));

        Assert.Equal([mine.Id], result.Approved);
        Assert.Equal(theirs.Id, Assert.Single(result.Skipped).QuestionId);
        Assert.Equal(ReviewStatus.PENDING, (await _k.GetAsync(colleagueSetup.Token, theirs.Id)).ReviewStatus);
    }

    [Fact]
    public async Task A_rejected_question_can_be_fixed_and_approved_again_but_only_pending_ones_can_be_rejected()
    {
        var setup = await _k.LessonAsync(2);
        var question = (await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 1, types = new[] { "SINGLE_CHOICE" } })).Questions[0];

        var rejected = await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{T}/{question.Id}/reject", null, setup.Token));
        Assert.Equal((ReviewStatus.REJECTED, setup.School.Teacher.Id), (rejected.ReviewStatus, rejected.ReviewedBy));
        Assert.True(rejected.Access is { CanEdit: true, CanApprove: true, CanReject: false });
        var (twice, twiceBody) = await Http.PostAsync<object>($"{T}/{question.Id}/reject", null, setup.Token);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "BLOCKED"), (twice.StatusCode, twiceBody.ErrorCode));

        var edited = await DiscoveryTestKit.Expect(Http.PutAsync<QuestionDetailDto>($"{T}/{question.Id}",
            QuestionTestKit.Samples(setup.Heritage.Id, setup.Images)[QuestionType.SINGLE_CHOICE] with { Explanation = "Giáo viên đã sửa lại giải thích." }, setup.Token));
        Assert.Equal((ReviewStatus.REJECTED, "Giáo viên đã sửa lại giải thích."), (edited.ReviewStatus, edited.Explanation));       // sửa không tự duyệt

        var approved = await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{T}/{question.Id}/approve", null, setup.Token));
        Assert.Equal(ReviewStatus.APPROVED, approved.ReviewStatus);
        var (approvedReject, _) = await Http.PostAsync<object>($"{T}/{question.Id}/reject", null, setup.Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, approvedReject.StatusCode);
    }

    [Fact]
    public async Task A_pending_question_can_be_edited_by_type_structure_but_not_given_another_type()
    {
        var setup = await _k.LessonAsync(2);
        var question = (await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 1, types = new[] { "FILL_BLANK" } })).Questions[0];
        var samples = QuestionTestKit.Samples(setup.Heritage.Id, setup.Images);

        var edited = await DiscoveryTestKit.Expect(Http.PutAsync<QuestionDetailDto>($"{T}/{question.Id}", samples[QuestionType.FILL_BLANK], setup.Token));
        Assert.Equal((ReviewStatus.PENDING, QuestionSource.AI, samples[QuestionType.FILL_BLANK].Content), (edited.ReviewStatus, edited.Source, edited.Content));
        Assert.Equal(question.AiGenerationId, edited.AiGenerationId);

        var (res, body) = await Http.PutAsync<object>($"{T}/{question.Id}", samples[QuestionType.SINGLE_CHOICE], setup.Token);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "BLOCKED"), (res.StatusCode, body.ErrorCode));
        Assert.Contains("Không đổi được dạng của câu do AI sinh", body.Message);
        Assert.Equal(QuestionType.FILL_BLANK, (await _k.GetAsync(setup.Token, question.Id)).QuestionType);
    }

    [Fact]
    public async Task Approval_checks_the_question_again_against_the_current_rules()
    {
        var setup = await _k.LessonAsync(2);
        var batch = await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 2, types = new[] { "SINGLE_CHOICE" } });
        await _k.Lesson.ArchiveHeritageAsync(setup.Heritage.Id);                                  // di sản không còn xuất bản

        var (res, body) = await Http.PostAsync<object>($"{T}/{batch.Questions[0].Id}/approve", null, setup.Token);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "BLOCKED"), (res.StatusCode, body.ErrorCode));
        Assert.StartsWith("Không thể duyệt câu hỏi. Câu hỏi chưa đạt:", body.Message);
        Assert.Contains("heritageId", body.Errors!.Keys);
        Assert.Equal(ReviewStatus.PENDING, (await _k.GetAsync(setup.Token, batch.Questions[0].Id)).ReviewStatus);
        var result = await DiscoveryTestKit.Expect(Http.PostAsync<ApproveQuestionsResultDto>($"{T}/approve", new { questionIds = batch.Questions.Select(q => q.Id).ToArray() }, setup.Token));
        Assert.Empty(result.Approved);
        Assert.Equal(2, result.Skipped.Count);
    }

    [Fact]
    public async Task A_pending_question_used_in_no_quiz_can_be_deleted_by_its_creator()
    {
        var setup = await _k.LessonAsync(2);
        var question = (await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 1 })).Questions[0];

        var (del, _) = await Http.DeleteAsync<object>($"{T}/{question.Id}", setup.Token);

        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(0, (await _k.ListAsync(setup.Token, "scope=PENDING")).TotalItems);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync<object>($"{T}/{question.Id}", setup.Token)).Response.StatusCode);
    }
}
