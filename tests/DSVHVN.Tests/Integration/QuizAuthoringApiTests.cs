using System.Net;
using DSVHVN.Application.Quizzes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Giáo viên tạo, sửa, xóa bài kiểm tra nháp và soạn danh sách câu qua Api thật: bài kiểm tra gắn bài học của mình, lớp và cấp là của bài học,
/// thời gian làm theo cấp, chỉ nhận câu đã duyệt hợp cấp, điểm bước 0,25, tổng điểm tự tính, phạm vi theo giáo viên, hộp chọn câu.
/// </summary>
public sealed class QuizAuthoringApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly QuizTestKit _k = new(factory);
    private HttpClient Http => _k.Http;
    private const string Q = QuizTestKit.Quizzes;

    private static void AssertFail(HttpResponseMessage res, Envelope<object> body, HttpStatusCode status, string code)
    {
        Assert.Equal((status, code), (res.StatusCode, body.ErrorCode));
        Assert.False(body.Success);
    }

    // ---- Tạo ----

    [Theory]
    [InlineData(1, "Cấp 1", TimeLimitRule.NOT_ALLOWED)]
    [InlineData(2, "Cấp 2", TimeLimitRule.OPTIONAL)]
    [InlineData(3, "Cấp 3", TimeLimitRule.REQUIRED)]
    public async Task Creating_a_quiz_makes_a_draft_for_the_lesson_and_the_class_and_level_come_from_the_lesson(int level, string levelName, TimeLimitRule rule)
    {
        var s = await _k.SetupAsync(level);

        var quiz = await _k.CreateAsync(s, "  Kiểm tra giữa kỳ  ", new { description = "  Em đọc kỹ đề.  ", difficultyLevel = DifficultyLevel.HARD, shuffleQuestions = true });

        Assert.Equal(("Kiểm tra giữa kỳ", "Em đọc kỹ đề.", QuizStatus.DRAFT, "Nháp"), (quiz.Name, quiz.Description, quiz.Status, quiz.StatusLabel));
        Assert.Equal((s.LessonId, s.ClassId, level, levelName), (quiz.LessonId, quiz.ClassId, quiz.SchoolLevel, quiz.SchoolLevelName));
        Assert.Equal((s.AccessCode, rule), (quiz.LessonAccessCode, quiz.TimeLimitRule));
        Assert.Equal((DifficultyLevel.HARD, true, false, 0m, 0), (quiz.DifficultyLevel, quiz.ShuffleQuestions, quiz.ShowAnswersAfterSubmit, quiz.TotalPoint, quiz.QuestionCount));
        Assert.Equal((QuizOpenState.NONE, null, null), (quiz.OpenState, quiz.OpenAt, quiz.PublishedAt));
        Assert.True(quiz.Access is { CanEditSettings: true, CanEditQuestions: true, CanDelete: true, CanClose: false, CanPublish: false, CanDuplicate: true });
        Assert.False(quiz.Readiness.IsReady);
        Assert.Contains("ít nhất một câu hỏi", quiz.Readiness.Missing);
        Assert.Contains("giờ mở", quiz.Readiness.Missing);
        Assert.Contains("giờ đóng", quiz.Readiness.Missing);
        Assert.Equal(level == 3, quiz.Readiness.Missing.Any(m => m.Contains("phải đặt thời gian làm bài")));
    }

    [Fact]
    public async Task The_time_limit_follows_the_level_of_the_class_and_times_are_stored_in_utc()
    {
        var level1 = await _k.SetupAsync(1);
        var (res, body) = await Http.PostAsync<object>(Q, new { lessonId = level1.LessonId, name = "Cấp 1", timeLimitMinutes = 15 }, level1.Token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");
        Assert.Contains("không giới hạn thời gian", body.Errors!["timeLimitMinutes"]);

        var level2 = await _k.SetupAsync(2);
        Assert.Null((await _k.CreateAsync(level2, "Không giới hạn")).TimeLimitMinutes);
        Assert.Equal(45, (await _k.CreateAsync(level2, "Có giới hạn", new { timeLimitMinutes = 45 })).TimeLimitMinutes);

        var level3 = await _k.SetupAsync(3);
        Assert.Null((await _k.CreateAsync(level3, "Chưa đặt giờ làm")).TimeLimitMinutes);   // nháp được để trống, phát hành mới bắt buộc
        foreach (var bad in new[] { 0, -5, 181 })
        {
            (res, body) = await Http.PostAsync<object>(Q, new { lessonId = level3.LessonId, name = "Sai giờ", timeLimitMinutes = bad }, level3.Token);
            AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");
            Assert.Contains("từ 1 đến 180 phút", body.Errors!["timeLimitMinutes"]);
        }

        // Giờ không kèm múi giờ là giờ Việt Nam; giờ có Z hoặc +07:00 đổi về UTC.
        var vn = await _k.CreateAsync(level2, "Giờ Việt Nam", new { openAt = "2031-03-01T08:00:00", closeAt = "2031-03-01T09:30:00+07:00" });
        Assert.Equal(new DateTime(2031, 3, 1, 1, 0, 0, DateTimeKind.Utc), vn.OpenAt);
        Assert.Equal(new DateTime(2031, 3, 1, 2, 30, 0, DateTimeKind.Utc), vn.CloseAt);
        var utc = await _k.CreateAsync(level2, "Giờ UTC", new { openAt = "2031-03-01T01:00:00Z" });
        Assert.Equal(new DateTime(2031, 3, 1, 1, 0, 0, DateTimeKind.Utc), utc.OpenAt);
    }

    [Fact]
    public async Task Creating_rejects_bad_fields_with_errors_by_field()
    {
        var s = await _k.SetupAsync();
        var token = s.Token;

        var (res, body) = await Http.PostAsync<object>(Q, new { lessonId = s.LessonId, name = "" }, token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "REQUIRED");
        Assert.Equal("Vui lòng nhập tên bài kiểm tra.", body.Errors!["name"]);

        (res, body) = await Http.PostAsync<object>(Q, new { lessonId = s.LessonId, name = new string('x', 256) }, token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");

        (res, body) = await Http.PostAsync<object>(Q, new { lessonId = s.LessonId, name = "Tên", description = new string('x', 4001) }, token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");

        (res, body) = await Http.PostAsync<object>(Q, new { name = "Tên" }, token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "REQUIRED");
        Assert.True(body.Errors!.ContainsKey("lessonId"));

        (res, body) = await Http.PostAsync<object>(Q, new { lessonId = s.LessonId, name = "Tên", openAt = "2031-03-01T10:00:00Z", closeAt = "2031-03-01T09:00:00Z" }, token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal("Giờ đóng không hợp lệ: phải sau giờ mở.", body.Errors!["closeAt"]);

        (res, body) = await Http.PostAsync<object>(Q, new { lessonId = s.LessonId, name = "Tên", difficultyLevel = "VERY_HARD" }, token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");
    }

    [Fact]
    public async Task A_quiz_can_be_made_for_a_draft_or_published_lesson_but_not_an_archived_one_or_someone_elses()
    {
        var s = await _k.SetupAsync(2, publishLesson: false);
        var onDraft = await _k.CreateAsync(s, "Trên bài nháp");
        Assert.Equal(ContentStatus.DRAFT, onDraft.LessonStatus);

        var published = await _k.AnotherLessonAsync(s);
        await _k.CreateAsync(s, "Trên bài đã xuất bản", lessonId: published.Id);

        // Bài lưu trữ: không gắn thêm bài kiểm tra.
        await DiscoveryTestKit.Expect(Http.PostAsync<LessonDetailDtoStub>($"/api/v1/teacher/lessons/{published.Id}/archive", null, s.Token));
        var (res, body) = await Http.PostAsync<object>(Q, new { lessonId = published.Id, name = "Không được" }, s.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("Bài học đã lưu trữ", body.Message);

        // Bài của giáo viên khác (cùng trường) hoặc không tồn tại: 404.
        var (_, otherToken) = await _k.Questions.Lesson.Classroom.AnotherTeacherAsync(s.School);
        (res, body) = await Http.PostAsync<object>(Q, new { lessonId = s.LessonId, name = "Của người khác" }, otherToken);
        AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");
        (res, body) = await Http.PostAsync<object>(Q, new { lessonId = 999_999, name = "Không có" }, s.Token);
        AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");
    }

    [Fact]
    public async Task Creating_and_duplicating_need_an_effective_plan_but_editing_do_not()
    {
        var s = await _k.SetupAsync();
        var quiz = await _k.CreateAsync(s, "Có trước khi hết gói");

        await _k.ExpirePlanAsync(s.School.OrgId);

        var (res, body) = await Http.PostAsync<object>(Q, new { lessonId = s.LessonId, name = "Mới" }, s.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "PLAN_REQUIRED");
        Assert.Contains("Không thể tạo bài kiểm tra", body.Message);
        (res, body) = await Http.PostAsync<object>($"{Q}/{quiz.Id}/duplicate", new { }, s.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "PLAN_REQUIRED");

        var saved = await DiscoveryTestKit.Expect(Http.PutAsync<QuizDetailDto>($"{Q}/{quiz.Id}", new { name = "Vẫn sửa được" }, s.Token));
        Assert.Equal("Vẫn sửa được", saved.Name);
    }

    // ---- Sửa và xóa ----

    [Fact]
    public async Task Saving_a_draft_replaces_every_setting_and_an_empty_field_clears_the_value()
    {
        var s = await _k.SetupAsync(3);
        var quiz = await _k.CreateAsync(s, "Bài đầu", new { description = "Mô tả", difficultyLevel = DifficultyLevel.EASY, timeLimitMinutes = 40, shuffleQuestions = true });

        var saved = await DiscoveryTestKit.Expect(Http.PutAsync<QuizDetailDto>($"{Q}/{quiz.Id}", new
        {
            name = "Bài đã sửa", timeLimitMinutes = 25, openAt = "2031-03-01T08:00:00", closeAt = "2031-03-01T09:00:00", showAnswersAfterSubmit = true,
        }, s.Token));

        Assert.Equal(("Bài đã sửa", null, null, 25), (saved.Name, saved.Description, saved.DifficultyLevel, saved.TimeLimitMinutes));
        Assert.Equal((false, true), (saved.ShuffleQuestions, saved.ShowAnswersAfterSubmit));
        Assert.Equal(new DateTime(2031, 3, 1, 1, 0, 0, DateTimeKind.Utc), saved.OpenAt);
        Assert.NotNull(saved.UpdatedAt);

        // Cấp 3 để trống thời gian lúc đang soạn thì được, chưa phát hành được.
        var cleared = await DiscoveryTestKit.Expect(Http.PutAsync<QuizDetailDto>($"{Q}/{quiz.Id}", new { name = "Bỏ thời gian" }, s.Token));
        Assert.Null(cleared.TimeLimitMinutes);

        var (res, body) = await Http.PutAsync<object>($"{Q}/{quiz.Id}", new { name = "", timeLimitMinutes = 25 }, s.Token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "REQUIRED");
    }

    [Fact]
    public async Task Only_a_draft_can_be_deleted_and_a_deleted_quiz_is_gone()
    {
        var s = await _k.SetupAsync();
        var draft = await _k.CreateAsync(s, "Sẽ xóa");
        var (res, body) = await Http.DeleteAsync<object>($"{Q}/{draft.Id}", s.Token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(body.Success);

        var (gone, goneBody) = await Http.GetAsync<QuizDetailDto>($"{Q}/{draft.Id}", s.Token);
        Assert.Equal((HttpStatusCode.NotFound, "BLOCKED"), (gone.StatusCode, goneBody.ErrorCode));
        var list = await DiscoveryTestKit.Expect(Http.GetAsync<QuizPageDto>($"{Q}?lessonId={s.LessonId}", s.Token));
        Assert.Empty(list.Items);

        var published = await _k.PublishedQuizAsync(s, "Đã phát hành");
        var (del, delBody) = await Http.DeleteAsync<object>($"{Q}/{published.Id}", s.Token);
        AssertFail(del, delBody, HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("Chỉ xóa được bài kiểm tra nháp", delBody.Message);
        Assert.Equal(QuizStatus.PUBLISHED, (await _k.GetAsync(s.Token, published.Id)).Status);
    }

    // ---- Danh sách câu ----

    [Fact]
    public async Task Adding_questions_appends_them_in_order_with_the_point_and_the_total_follows()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.CreateAsync(s);
        var questions = await _k.QuestionsAsync(s, 3);

        var first = await _k.AddQuestionsAsync(s.Token, quiz.Id, [questions[0].Id, questions[1].Id]);
        Assert.Equal(new[] { (questions[0].Id, 1, 1m), (questions[1].Id, 2, 1m) }, first.Questions.Select(x => (x.QuestionId, x.SortOrder, x.Point)));
        Assert.Equal((2m, 2), (first.TotalPoint, first.QuestionCount));

        var second = await _k.AddQuestionsAsync(s.Token, quiz.Id, [questions[2].Id], 2.25m);
        Assert.Equal(new[] { 1, 2, 3 }, second.Questions.Select(x => x.SortOrder));
        Assert.Equal((2.25m, 4.25m), (second.Questions[2].Point, second.TotalPoint));
        Assert.Equal((questions[2].Content, QuestionTypes.NameOf(questions[2].QuestionType), true, null),
            (second.Questions[2].Content, second.Questions[2].TypeName, second.Questions[2].IsUsable, second.Questions[2].UnusableReason));

        // Tổng điểm lưu trong CSDL khớp.
        Assert.Equal(4.25m, await factory.WithDbAsync(db => db.Quizzes.Where(x => x.Id == quiz.Id).Select(x => x.TotalPoint).SingleAsync()));
    }

    [Fact]
    public async Task Adding_checks_every_question_and_adds_none_when_one_is_wrong()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.CreateAsync(s);
        var good = await _k.QuestionAsync(s, QuestionType.SINGLE_CHOICE);
        var pending = await _k.QuestionAsync(s, QuestionType.FILL_BLANK);
        await _k.SetQuestionReviewAsync(pending.Id, ReviewStatus.PENDING);
        var mismatch = await _k.QuestionAsync(s, QuestionType.IMAGE_CHOICE);   // chỉ dùng được ở cấp 1

        // Câu chưa duyệt hoặc không có: 400 theo từng câu.
        var (res, body) = await Http.PostAsync<object>($"{Q}/{quiz.Id}/questions", new { questionIds = new[] { good.Id, pending.Id, 999_999L } }, s.Token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal(["questionIds[1]", "questionIds[2]"], body.Errors!.Keys.Order());

        // Câu không hợp cấp của lớp: 422 kèm tên dạng và cấp.
        (res, body) = await Http.PostAsync<object>($"{Q}/{quiz.Id}/questions", new { questionIds = new[] { good.Id, mismatch.Id } }, s.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "QUESTION_LEVEL_MISMATCH");
        Assert.Equal("Câu hỏi dạng \"Chọn hình đúng\" không dùng được ở lớp cấp 2.", body.Message);
        Assert.Equal(["questionIds[1]"], body.Errors!.Keys);

        Assert.Equal(0, (await _k.GetAsync(s.Token, quiz.Id)).QuestionCount);   // không thêm câu nào

        // Câu đã có trong bài, thêm lần nữa: 400.
        await _k.AddQuestionsAsync(s.Token, quiz.Id, [good.Id]);
        (res, body) = await Http.PostAsync<object>($"{Q}/{quiz.Id}/questions", new { questionIds = new[] { good.Id } }, s.Token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");
        Assert.Contains("đã có trong bài kiểm tra", body.Errors!["questionIds[0]"]);

        // Danh sách rỗng hoặc điểm sai bước.
        (res, body) = await Http.PostAsync<object>($"{Q}/{quiz.Id}/questions", new { questionIds = Array.Empty<long>() }, s.Token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "REQUIRED");
        var another = await _k.QuestionAsync(s, QuestionType.TRUE_FALSE);
        foreach (var point in new[] { 0m, -1m, 0.1m, 100.25m })
        {
            (res, body) = await Http.PostAsync<object>($"{Q}/{quiz.Id}/questions", new { questionIds = new[] { another.Id }, point }, s.Token);
            AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");
            Assert.Contains("lớn hơn 0, không quá 100, theo bước 0.25", body.Errors!["point"]);
        }
    }

    [Fact]
    public async Task A_quiz_holds_at_most_fifty_questions()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.CreateAsync(s);
        var ids = new List<long>();
        for (var i = 0; i < 51; i++) ids.Add((await _k.QuestionAsync(s, QuestionType.SINGLE_CHOICE)).Id);

        await _k.AddQuestionsAsync(s.Token, quiz.Id, ids.Take(50));
        var (res, body) = await Http.PostAsync<object>($"{Q}/{quiz.Id}/questions", new { questionIds = new[] { ids[50] } }, s.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("tối đa 50 câu", body.Message);
        Assert.Equal(50, (await _k.GetAsync(s.Token, quiz.Id)).QuestionCount);
    }

    [Fact]
    public async Task A_hidden_or_deleted_question_cannot_be_added()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.CreateAsync(s);
        var hidden = await _k.QuestionAsync(s, QuestionType.SINGLE_CHOICE);
        await _k.SetQuestionReviewAsync(hidden.Id, ReviewStatus.REJECTED);
        var deleted = await _k.QuestionAsync(s, QuestionType.TRUE_FALSE);
        var (del, _) = await Http.DeleteAsync<object>($"{QuestionTestKit.Teacher}/{deleted.Id}", s.Token);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var (res, body) = await Http.PostAsync<object>($"{Q}/{quiz.Id}/questions", new { questionIds = new[] { hidden.Id, deleted.Id } }, s.Token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal(2, body.Errors!.Count);
    }

    [Fact]
    public async Task Replacing_reorders_changes_points_and_drops_the_missing_questions()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.CreateAsync(s);
        var q = await _k.QuestionsAsync(s, 4);
        await _k.AddQuestionsAsync(s.Token, quiz.Id, q.Select(x => x.Id));

        var replaced = await DiscoveryTestKit.Expect(Http.PutAsync<QuizDetailDto>($"{Q}/{quiz.Id}/questions", new
        {
            items = new object[] { new { questionId = q[2].Id, point = 3 }, new { questionId = q[0].Id, point = 0.5 }, new { questionId = q[3].Id, point = 1.25 } },
        }, s.Token));

        Assert.Equal(new[] { (q[2].Id, 1, 3m), (q[0].Id, 2, 0.5m), (q[3].Id, 3, 1.25m) }, replaced.Questions.Select(x => (x.QuestionId, x.SortOrder, x.Point)));
        Assert.Equal(4.75m, replaced.TotalPoint);
        Assert.Equal(new[] { q[0].Id, q[2].Id, q[3].Id }.Order(), await factory.WithDbAsync(db => db.QuizQuestions.Where(x => x.QuizId == quiz.Id).Select(x => x.QuestionId).OrderBy(x => x).ToListAsync()));

        // Câu chưa có trong bài: 400 theo vị trí; câu trùng hoặc điểm sai bước: 400 theo vị trí.
        var (res, body) = await Http.PutAsync<object>($"{Q}/{quiz.Id}/questions", new
        {
            items = new object[] { new { questionId = q[1].Id, point = 1 }, new { questionId = q[0].Id, point = 1 } },
        }, s.Token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal(["items[0].questionId"], body.Errors!.Keys);

        (res, body) = await Http.PutAsync<object>($"{Q}/{quiz.Id}/questions", new
        {
            items = new object[] { new { questionId = q[0].Id, point = 1 }, new { questionId = q[0].Id, point = 0.1 } },
        }, s.Token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal(["items[1].point", "items[1].questionId"], body.Errors!.Keys.Order());

        (res, body) = await Http.PutAsync<object>($"{Q}/{quiz.Id}/questions", new { }, s.Token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "REQUIRED");

        // Danh sách rỗng bỏ hết câu.
        var emptied = await DiscoveryTestKit.Expect(Http.PutAsync<QuizDetailDto>($"{Q}/{quiz.Id}/questions", new { items = Array.Empty<object>() }, s.Token));
        Assert.Equal((0, 0m), (emptied.QuestionCount, emptied.TotalPoint));
    }

    [Fact]
    public async Task Removing_a_question_renumbers_the_rest_and_recalculates_the_total()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.CreateAsync(s);
        var q = await _k.QuestionsAsync(s, 3);
        await _k.AddQuestionsAsync(s.Token, quiz.Id, q.Select(x => x.Id), 2m);

        var after = await DiscoveryTestKit.Expect(Http.DeleteAsync<QuizDetailDto>($"{Q}/{quiz.Id}/questions/{q[0].Id}", s.Token));
        Assert.Equal(new[] { (q[1].Id, 1), (q[2].Id, 2) }, after.Questions.Select(x => (x.QuestionId, x.SortOrder)));
        Assert.Equal(4m, after.TotalPoint);

        var (res, body) = await Http.DeleteAsync<object>($"{Q}/{quiz.Id}/questions/{q[0].Id}", s.Token);
        AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");
    }

    // ---- Hộp chọn câu ----

    [Fact]
    public async Task The_candidate_box_lists_only_approved_questions_that_fit_the_level_of_the_class_and_marks_those_in_the_quiz()
    {
        var s = await _k.SetupAsync(1);
        var quiz = await _k.CreateAsync(s);
        var imageChoice = await _k.QuestionAsync(s, QuestionType.IMAGE_CHOICE, DifficultyLevel.EASY);
        var trueFalse = await _k.QuestionAsync(s, QuestionType.TRUE_FALSE, DifficultyLevel.HARD);
        var singleChoice = await _k.QuestionAsync(s, QuestionType.SINGLE_CHOICE);   // cấp 2-3: không hiện ở lớp cấp 1
        var sourceReading = await _k.QuestionAsync(s, QuestionType.SOURCE_READING);
        var pending = await _k.QuestionAsync(s, QuestionType.ORDERING);
        await _k.SetQuestionReviewAsync(pending.Id, ReviewStatus.PENDING);
        await _k.AddQuestionsAsync(s.Token, quiz.Id, [trueFalse.Id]);

        var page = await DiscoveryTestKit.Expect(Http.GetAsync<QuizCandidatePageDto>($"{Q}/{quiz.Id}/question-candidates?pageSize=100", s.Token));

        Assert.Equal(1, page.Level);
        Assert.Equal(QuestionTypes.ForLevel(1), page.AllowedTypes);
        var ids = page.Items.Select(x => x.Question.Id).ToList();
        Assert.Contains(imageChoice.Id, ids);
        Assert.Contains(trueFalse.Id, ids);
        Assert.DoesNotContain(singleChoice.Id, ids);
        Assert.DoesNotContain(sourceReading.Id, ids);
        Assert.DoesNotContain(pending.Id, ids);
        Assert.True(page.Items.Single(x => x.Question.Id == trueFalse.Id).InQuiz);
        Assert.False(page.Items.Single(x => x.Question.Id == imageChoice.Id).InQuiz);
        Assert.All(page.Items, x => Assert.True(x.Question.LevelMin <= 1 && x.Question.LevelMax >= 1));

        // Lọc theo độ khó và dạng.
        var hard = await DiscoveryTestKit.Expect(Http.GetAsync<QuizCandidatePageDto>($"{Q}/{quiz.Id}/question-candidates?difficultyLevel=HARD&pageSize=100", s.Token));
        Assert.All(hard.Items, x => Assert.Equal(DifficultyLevel.HARD, x.Question.DifficultyLevel));
        Assert.Contains(trueFalse.Id, hard.Items.Select(x => x.Question.Id));
        var byType = await DiscoveryTestKit.Expect(Http.GetAsync<QuizCandidatePageDto>($"{Q}/{quiz.Id}/question-candidates?questionType=IMAGE_CHOICE&pageSize=100", s.Token));
        Assert.All(byType.Items, x => Assert.Equal(QuestionType.IMAGE_CHOICE, x.Question.QuestionType));
    }

    [Fact]
    public async Task The_candidate_box_defaults_to_the_heritages_of_the_lesson_and_can_open_to_every_heritage()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.CreateAsync(s);
        var mine = await _k.QuestionAsync(s, QuestionType.SINGLE_CHOICE);

        // Một câu của di sản khác (do giáo viên khác của trường tạo).
        var (otherHeritage, otherImages) = await _k.Questions.HeritageAsync("Văn Miếu");
        var otherQuestion = await _k.Questions.CreateAsync(s.Token, QuestionTestKit.Samples(otherHeritage.Id, otherImages)[QuestionType.SINGLE_CHOICE]);

        var byLesson = await DiscoveryTestKit.Expect(Http.GetAsync<QuizCandidatePageDto>($"{Q}/{quiz.Id}/question-candidates?pageSize=100", s.Token));
        Assert.Contains(mine.Id, byLesson.Items.Select(x => x.Question.Id));
        Assert.DoesNotContain(otherQuestion.Id, byLesson.Items.Select(x => x.Question.Id));
        Assert.All(byLesson.Items, x => Assert.Equal(s.Heritage.Id, x.Question.HeritageId));

        var all = await DiscoveryTestKit.Expect(Http.GetAsync<QuizCandidatePageDto>($"{Q}/{quiz.Id}/question-candidates?lessonHeritagesOnly=false&pageSize=100", s.Token));
        Assert.Contains(otherQuestion.Id, all.Items.Select(x => x.Question.Id));

        var one = await DiscoveryTestKit.Expect(Http.GetAsync<QuizCandidatePageDto>($"{Q}/{quiz.Id}/question-candidates?heritageId={otherHeritage.Id}&pageSize=100", s.Token));
        Assert.All(one.Items, x => Assert.Equal(otherHeritage.Id, x.Question.HeritageId));

        // Câu đã duyệt của người khác dùng chung: thêm được vào bài của mình.
        var added = await _k.AddQuestionsAsync(s.Token, quiz.Id, [otherQuestion.Id]);
        Assert.Equal(1, added.QuestionCount);
    }

    // ---- Danh sách bài kiểm tra ----

    [Fact]
    public async Task The_list_filters_by_lesson_class_status_and_name_and_counts_submitted_over_class_size()
    {
        var s = await _k.SetupAsync(2);
        var secondClass = await _k.Questions.Lesson.Classroom.CreateClassAsync(s.Token, "9A2", 9);
        var other = await _k.Questions.Lesson.PublishedLessonAsync(s.Token, secondClass.Id, [s.Heritage.Id], "Bài ở lớp 9A2");
        var draft = await _k.CreateAsync(s, "Nháp về sông");
        var published = await _k.PublishedQuizAsync(s, "Đang phát hành về thành");
        var elsewhere = await _k.CreateAsync(s, "Ở lớp khác", lessonId: other.Id);
        await _k.AddAttemptAsync(s, published.Id, AttemptStatus.SUBMITTED);
        await _k.AddAttemptAsync(s, published.Id, AttemptStatus.IN_PROGRESS, "Lê Văn Cường");

        var all = await DiscoveryTestKit.Expect(Http.GetAsync<QuizPageDto>(Q, s.Token));
        Assert.Equal(3, all.TotalItems);

        var byLesson = await DiscoveryTestKit.Expect(Http.GetAsync<QuizPageDto>($"{Q}?lessonId={s.LessonId}", s.Token));
        Assert.Equal(new[] { draft.Id, published.Id }.Order(), byLesson.Items.Select(x => x.Id).Order());
        var byClass = await DiscoveryTestKit.Expect(Http.GetAsync<QuizPageDto>($"{Q}?classId={secondClass.Id}", s.Token));
        Assert.Equal([elsewhere.Id], byClass.Items.Select(x => x.Id));
        Assert.Equal(("9A2", 9, 2), (byClass.Items[0].ClassName, byClass.Items[0].Grade, byClass.Items[0].SchoolLevel));

        var byStatus = await DiscoveryTestKit.Expect(Http.GetAsync<QuizPageDto>($"{Q}?status=PUBLISHED", s.Token));
        var row = Assert.Single(byStatus.Items);
        Assert.Equal((published.Id, "Đang phát hành", QuizOpenState.NOT_OPEN, 3, 6m), (row.Id, row.StatusLabel, row.OpenState, row.QuestionCount, row.TotalPoint));
        Assert.Equal((1, 2), (row.SubmittedCount, row.StudentCount));   // một đã nộp trên sĩ số hai (học sinh thêm cho lượt làm bài)

        var byName = await DiscoveryTestKit.Expect(Http.GetAsync<QuizPageDto>($"{Q}?keyword=sông", s.Token));
        Assert.Equal(draft.Id, Assert.Single(byName.Items).Id);

        var (res, body) = await Http.GetAsync<object>($"{Q}?pageSize=101", s.Token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");
    }

    // ---- Phạm vi theo người dùng ----

    [Fact]
    public async Task Another_teacher_gets_404_on_every_endpoint_and_other_roles_are_refused()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.ReadyQuizAsync(s);
        var question = await _k.QuestionAsync(s);
        var (_, otherToken) = await _k.Questions.Lesson.Classroom.AnotherTeacherAsync(s.School);

        var calls = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Get, $"{Q}/{quiz.Id}", null),
            (HttpMethod.Put, $"{Q}/{quiz.Id}", new { name = "Chiếm quyền" }),
            (HttpMethod.Delete, $"{Q}/{quiz.Id}", null),
            (HttpMethod.Get, $"{Q}/{quiz.Id}/question-candidates", null),
            (HttpMethod.Post, $"{Q}/{quiz.Id}/questions", new { questionIds = new[] { question.Id } }),
            (HttpMethod.Put, $"{Q}/{quiz.Id}/questions", new { items = Array.Empty<object>() }),
            (HttpMethod.Delete, $"{Q}/{quiz.Id}/questions/{quiz.Questions[0].QuestionId}", null),
            (HttpMethod.Post, $"{Q}/{quiz.Id}/publish", null),
            (HttpMethod.Post, $"{Q}/{quiz.Id}/close", null),
            (HttpMethod.Post, $"{Q}/{quiz.Id}/duplicate", new { }),
        };
        foreach (var (method, url, payload) in calls)
        {
            var (res, body) = await Http.SendJsonAsync<object>(method, url, payload, otherToken);
            Assert.True((res.StatusCode, body.ErrorCode) == (HttpStatusCode.NotFound, "BLOCKED"), $"{method} {url}: {(int)res.StatusCode} {body.ErrorCode}");
        }
        var list = await DiscoveryTestKit.Expect(Http.GetAsync<QuizPageDto>(Q, otherToken));
        Assert.Empty(list.Items);

        // Quản trị trường, quản trị hệ thống, học sinh: 403; chưa đăng nhập: 401.
        var student = await _k.Questions.Lesson.StudentTokenAsync(s.Token, s.ClassId);
        foreach (var token in new[] { s.School.OrgAdminToken, await _k.Questions.Lesson.Discovery.Api.AdminTokenAsync(), student })
        {
            var (res, _) = await Http.GetAsync<object>(Q, token);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            (res, _) = await Http.GetAsync<object>($"{Q}/{quiz.Id}", token);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            (res, _) = await Http.PostAsync<object>(Q, new { lessonId = s.LessonId, name = "x" }, token);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
        var (anonymous, _) = await Http.GetAsync<object>(Q);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task A_quiz_of_a_deleted_class_is_not_found()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.CreateAsync(s, "Của lớp sẽ xóa");
        var (del, _) = await Http.DeleteAsync<object>($"/api/v1/teacher/classes/{s.ClassId}", s.Token);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var (res, body) = await Http.GetAsync<object>($"{Q}/{quiz.Id}", s.Token);
        AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");
        Assert.Empty((await DiscoveryTestKit.Expect(Http.GetAsync<QuizPageDto>(Q, s.Token))).Items);
    }

    // ---- Phát hành khóa danh sách câu ----

    [Fact]
    public async Task A_published_quiz_locks_the_question_list()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.PublishedQuizAsync(s);
        var extra = await _k.QuestionAsync(s, QuestionType.SINGLE_CHOICE);

        var calls = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Post, $"{Q}/{quiz.Id}/questions", new { questionIds = new[] { extra.Id } }),
            (HttpMethod.Put, $"{Q}/{quiz.Id}/questions", new { items = Array.Empty<object>() }),
            (HttpMethod.Delete, $"{Q}/{quiz.Id}/questions/{quiz.Questions[0].QuestionId}", null),
        };
        foreach (var (method, url, payload) in calls)
        {
            var (res, body) = await Http.SendJsonAsync<object>(method, url, payload, s.Token);
            Assert.True((res.StatusCode, body.ErrorCode) == (HttpStatusCode.UnprocessableEntity, "BLOCKED"), $"{method} {url}: {(int)res.StatusCode} {body.ErrorCode}");
            Assert.Contains("đã khóa", body.Message);
        }
        Assert.Equal(3, (await _k.GetAsync(s.Token, quiz.Id)).QuestionCount);
    }

    [Fact]
    public async Task A_question_in_a_quiz_is_read_only_until_the_draft_quiz_that_holds_it_is_deleted()
    {
        var s = await _k.SetupAsync(2);
        var quiz = await _k.CreateAsync(s);
        var question = await _k.QuestionAsync(s, QuestionType.SINGLE_CHOICE);
        await _k.AddQuestionsAsync(s.Token, quiz.Id, [question.Id]);

        var held = await _k.Questions.GetAsync(s.Token, question.Id);
        Assert.Equal((true, 1), (held.Access.IsReadOnly, held.QuizCount));
        var (res, body) = await Http.DeleteAsync<object>($"{QuestionTestKit.Teacher}/{question.Id}", s.Token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "BLOCKED");

        await Http.DeleteAsync<object>($"{Q}/{quiz.Id}", s.Token);

        var freed = await _k.Questions.GetAsync(s.Token, question.Id);
        Assert.Equal((false, 0), (freed.Access.IsReadOnly, freed.QuizCount));
        var (del, _) = await Http.DeleteAsync<object>($"{QuestionTestKit.Teacher}/{question.Id}", s.Token);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
    }

    private sealed record LessonDetailDtoStub(long Id);
}
