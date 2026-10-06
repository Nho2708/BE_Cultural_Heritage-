using System.Net;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Quizzes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Quiz và bài học cùng nhau qua Api thật: nhân bản bài học kèm bài kiểm tra sang lớp khác (bỏ câu đã ẩn và câu không hợp cấp lớp đích, chuẩn hóa thời gian làm,
/// bỏ giờ mở và đóng) và lọc danh sách bài học theo độ khó tính từ các câu trong bài kiểm tra.
/// </summary>
public sealed class QuizLessonApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly QuizTestKit _k = new(factory);
    private HttpClient Http => _k.Http;
    private const string L = LessonTestKit.Teacher;

    private async Task<DuplicateLessonResultDto> DuplicateAsync(string token, long lessonId, long targetClassId, bool includeQuizzes) =>
        await DiscoveryTestKit.Expect(Http.PostAsync<DuplicateLessonResultDto>($"{L}/{lessonId}/duplicate", new { targetClassId, includeQuizzes }, token), HttpStatusCode.Created);

    private async Task<List<DSVHVN.Domain.Quizzes.Quiz>> QuizzesOfAsync(long lessonId) =>
        await factory.WithDbAsync(db => db.Quizzes.AsNoTracking().Include(q => q.Questions).Where(q => q.LessonId == lessonId && q.DeletedAt == null)
            .OrderBy(q => q.Id).ToListAsync());

    // ---- Nhân bản bài học kèm bài kiểm tra ----

    [Fact]
    public async Task Duplicating_a_lesson_without_the_option_copies_no_quiz()
    {
        var s = await _k.SetupAsync(2);
        await _k.PublishedQuizAsync(s);
        var target = await _k.Questions.Lesson.Classroom.CreateClassAsync(s.Token, "9A1", 9);

        var result = await DuplicateAsync(s.Token, s.LessonId, target.Id, includeQuizzes: false);

        Assert.Equal((0, 0, 0), (result.CopiedQuizzes, result.SkippedQuestions, result.Lesson.QuizCount));
        Assert.Equal($"Đã tạo bài học nháp ở lớp 9A1.", result.Message);
        Assert.Empty(await QuizzesOfAsync(result.Lesson.Id));
    }

    [Fact]
    public async Task Duplicating_with_quizzes_copies_them_as_drafts_with_the_same_questions_points_and_settings_and_no_times()
    {
        var s = await _k.SetupAsync(2);
        var published = await _k.PublishedQuizAsync(s, "Đã phát hành", questions: 4);
        var closed = await _k.PublishedQuizAsync(s, "Đã đóng", questions: 2);
        await _k.CloseAsync(s.Token, closed.Id);
        var draft = await _k.ReadyQuizAsync(s, "Đang soạn", questions: 3);
        var gone = await _k.CreateAsync(s, "Đã xóa");
        await Http.DeleteAsync<object>($"{QuizTestKit.Quizzes}/{gone.Id}", s.Token);
        await _k.SetQuestionReviewAsync(published.Questions[1].QuestionId, ReviewStatus.REJECTED);   // một câu đã bị ẩn
        var target = await _k.Questions.Lesson.Classroom.CreateClassAsync(s.Token, "9A1", 9);        // cùng cấp 2

        var result = await DuplicateAsync(s.Token, s.LessonId, target.Id, includeQuizzes: true);

        Assert.Equal((3, 1, 3), (result.CopiedQuizzes, result.SkippedQuestions, result.Lesson.QuizCount));
        Assert.Equal("Đã tạo bài học nháp ở lớp 9A1. Đã chép 3 bài kiểm tra thành bản nháp; hãy đặt lại giờ mở và giờ đóng. " +
                     "Bỏ qua 1 câu hỏi đã ẩn hoặc không dùng được ở cấp của lớp.", result.Message);

        var copies = await DiscoveryTestKit.Expect(Http.GetAsync<QuizPageDto>($"{QuizTestKit.Quizzes}?lessonId={result.Lesson.Id}", s.Token));
        Assert.Equal(3, copies.TotalItems);
        Assert.All(copies.Items, c =>
        {
            Assert.Equal((QuizStatus.DRAFT, null, null, null), (c.Status, c.OpenAt, c.CloseAt, c.PublishedAt));
            Assert.Equal((target.Id, "9A1"), (c.ClassId, c.ClassName));
            Assert.Equal(0, c.SubmittedCount);
        });

        var stored = await QuizzesOfAsync(result.Lesson.Id);
        Assert.Equal(["Đã phát hành", "Đã đóng", "Đang soạn"], stored.Select(q => q.Name));   // cùng tên, cùng thứ tự tạo
        Assert.All(stored, q => Assert.Equal((s.School.Teacher.Id, QuizStatus.DRAFT, 30), (q.CreatedBy, q.Status, q.TimeLimitMinutes)));

        // Bài đã phát hành mất câu bị ẩn: còn ba câu, đánh lại thứ tự, tổng điểm tính lại.
        var copyOfPublished = await _k.GetAsync(s.Token, stored[0].Id);
        Assert.Equal(published.Questions.Where((_, i) => i != 1).Select(x => x.QuestionId), copyOfPublished.Questions.Select(x => x.QuestionId));
        Assert.Equal(new[] { 1, 2, 3 }, copyOfPublished.Questions.Select(x => x.SortOrder));
        Assert.Equal((6m, 3), (copyOfPublished.TotalPoint, copyOfPublished.QuestionCount));
        Assert.Equal((published.DifficultyLevel, published.ShuffleQuestions, published.ShowAnswersAfterSubmit), (copyOfPublished.DifficultyLevel, copyOfPublished.ShuffleQuestions, copyOfPublished.ShowAnswersAfterSubmit));

        // Các bài gốc không đổi.
        var originals = await QuizzesOfAsync(s.LessonId);
        Assert.Equal([QuizStatus.PUBLISHED, QuizStatus.CLOSED, QuizStatus.DRAFT], originals.Select(q => q.Status!.Value));
        Assert.NotNull(originals[0].OpenAt);
        Assert.Equal(4, originals[0].Questions.Count);
        Assert.Equal(draft.Questions.Count, originals[2].Questions.Count);
    }

    [Fact]
    public async Task Duplicating_to_a_class_of_another_level_drops_the_questions_that_do_not_fit_and_normalizes_the_time_limit()
    {
        var s = await _k.SetupAsync(2);
        // Bốn câu: một đáp án đúng, Đúng/Sai, sắp xếp, điền từ. Cấp 1 dùng được Đúng/Sai và sắp xếp (câu Đúng/Sai ở đây bị ẩn).
        var withAll = await _k.PublishedQuizAsync(s, "Đủ bốn dạng", questions: 4);
        await _k.SetQuestionReviewAsync(withAll.Questions[1].QuestionId, ReviewStatus.REJECTED);
        var class1 = await _k.Questions.Lesson.Classroom.CreateClassAsync(s.Token, "3A1", 3);
        var class3 = await _k.Questions.Lesson.Classroom.CreateClassAsync(s.Token, "11A1", 11);

        var toLevel1 = await DuplicateAsync(s.Token, s.LessonId, class1.Id, includeQuizzes: true);
        Assert.Equal((1, 3), (toLevel1.CopiedQuizzes, toLevel1.SkippedQuestions));   // một đáp án đúng, câu ẩn và điền từ bị bỏ
        Assert.NotNull(toLevel1.LevelNotice);
        var copy1 = await _k.GetAsync(s.Token, (await QuizzesOfAsync(toLevel1.Lesson.Id)).Single().Id);
        Assert.Equal([QuestionType.ORDERING], copy1.Questions.Select(x => x.QuestionType!.Value));
        Assert.Equal((1, null, TimeLimitRule.NOT_ALLOWED), (copy1.SchoolLevel, copy1.TimeLimitMinutes, copy1.TimeLimitRule));   // cấp 1 bỏ giới hạn thời gian
        Assert.All(copy1.Questions, x => Assert.True(x.IsUsable));

        var toLevel3 = await DuplicateAsync(s.Token, s.LessonId, class3.Id, includeQuizzes: true);
        Assert.Equal((1, 3), (toLevel3.CopiedQuizzes, toLevel3.SkippedQuestions));   // chỉ câu một đáp án đúng dùng được ở cấp 3
        var copy3 = await _k.GetAsync(s.Token, (await QuizzesOfAsync(toLevel3.Lesson.Id)).Single().Id);
        Assert.Equal([QuestionType.SINGLE_CHOICE], copy3.Questions.Select(x => x.QuestionType!.Value));
        Assert.Equal((3, 30, TimeLimitRule.REQUIRED), (copy3.SchoolLevel, copy3.TimeLimitMinutes, copy3.TimeLimitRule));   // cấp 3 giữ giá trị cũ
    }

    [Fact]
    public async Task A_quiz_without_any_usable_question_is_still_copied_as_an_empty_draft()
    {
        var s = await _k.SetupAsync(3);
        var quiz = await _k.PublishedQuizAsync(s, "Chỉ cấp 3", questions: 2);   // một đáp án đúng, nhiều mệnh đề Đúng/Sai
        var class1 = await _k.Questions.Lesson.Classroom.CreateClassAsync(s.Token, "3A1", 3);

        var result = await DuplicateAsync(s.Token, s.LessonId, class1.Id, includeQuizzes: true);

        Assert.Equal((1, 2), (result.CopiedQuizzes, result.SkippedQuestions));
        var copy = await _k.GetAsync(s.Token, (await QuizzesOfAsync(result.Lesson.Id)).Single().Id);
        Assert.Equal((0, 0m, QuizStatus.DRAFT), (copy.QuestionCount, copy.TotalPoint, copy.Status));
        Assert.Equal(quiz.Name, copy.Name);
        Assert.False(copy.Readiness.IsReady);
    }

    [Fact]
    public async Task The_copied_quizzes_are_the_teachers_own_and_publishing_needs_the_new_lesson_published_first()
    {
        var s = await _k.SetupAsync(2);
        await _k.PublishedQuizAsync(s, "Bài gốc", questions: 3);
        var target = await _k.Questions.Lesson.Classroom.CreateClassAsync(s.Token, "9A1", 9);
        var result = await DuplicateAsync(s.Token, s.LessonId, target.Id, includeQuizzes: true);
        var copyId = (await QuizzesOfAsync(result.Lesson.Id)).Single().Id;

        // Đặt giờ rồi phát hành: bị chặn vì bài học mới còn Nháp.
        await DiscoveryTestKit.Expect(Http.PutAsync<QuizDetailDto>($"{QuizTestKit.Quizzes}/{copyId}", new
        {
            name = "Bài gốc", difficultyLevel = DifficultyLevel.MEDIUM, timeLimitMinutes = 30, openAt = QuizTestKit.OpenSoon, closeAt = QuizTestKit.CloseLater,
        }, s.Token));
        var (res, body) = await Http.PostAsync<object>($"{QuizTestKit.Quizzes}/{copyId}/publish", null, s.Token);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "NOT_READY"), (res.StatusCode, body.ErrorCode));
        Assert.Contains("bài học của bài kiểm tra chưa xuất bản", body.Message);

        // Soạn và xuất bản bài học mới (mã riêng) rồi phát hành bài kiểm tra.
        var lessons = _k.Questions.Lesson;
        await lessons.SaveDraftAsync(s.Token, result.Lesson.Id, await lessons.ComposeAsync(s.Token, result.Lesson.Id));
        var published = await lessons.PublishAsync(s.Token, result.Lesson.Id);
        Assert.NotEqual(s.AccessCode, published.AccessCode);
        var quiz = (await _k.PublishAsync(s.Token, copyId)).Quiz;
        Assert.Equal((QuizStatus.PUBLISHED, published.AccessCode, "9A1"), (quiz.Status, quiz.LessonAccessCode, quiz.ClassName));
    }

    // ---- Độ khó của bài học ----

    private async Task<long> LessonWithQuizAsync(QuizTestKit.Setup s, string title, params (DifficultyLevel Level, int Count)[] groups)
    {
        var lesson = await _k.AnotherLessonAsync(s, title);
        if (groups.Length == 0) return lesson.Id;
        var quiz = await _k.CreateAsync(s, "Bài kiểm tra của " + title, lessonId: lesson.Id);
        var ids = new List<long>();
        foreach (var (level, count) in groups)
            for (var i = 0; i < count; i++) ids.Add((await _k.QuestionAsync(s, QuestionType.SINGLE_CHOICE, level)).Id);
        if (ids.Count > 0) await _k.AddQuestionsAsync(s.Token, quiz.Id, ids);
        return lesson.Id;
    }

    [Fact]
    public async Task The_difficulty_of_a_lesson_is_the_level_with_the_most_questions_in_its_quizzes_and_a_tie_takes_the_higher_level()
    {
        var s = await _k.SetupAsync(2);
        var easy = await LessonWithQuizAsync(s, "Bài dễ", (DifficultyLevel.EASY, 3), (DifficultyLevel.MEDIUM, 1));
        var tie = await LessonWithQuizAsync(s, "Bài hòa", (DifficultyLevel.MEDIUM, 2), (DifficultyLevel.HARD, 2));
        var tieEasyMedium = await LessonWithQuizAsync(s, "Bài hòa dễ vừa", (DifficultyLevel.EASY, 1), (DifficultyLevel.MEDIUM, 1));
        var none = await LessonWithQuizAsync(s, "Bài chưa có bài kiểm tra");
        var empty = await LessonWithQuizAsync(s, "Bài kiểm tra rỗng", (DifficultyLevel.HARD, 0));

        var page = await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{L}?pageSize=100", s.Token));
        var byId = page.Items.ToDictionary(x => x.Id);
        Assert.Equal((DifficultyLevel.EASY, "Dễ"), (byId[easy].Difficulty, byId[easy].DifficultyLabel));
        Assert.Equal((DifficultyLevel.HARD, "Khó"), (byId[tie].Difficulty, byId[tie].DifficultyLabel));
        Assert.Equal((DifficultyLevel.MEDIUM, "Trung bình"), (byId[tieEasyMedium].Difficulty, byId[tieEasyMedium].DifficultyLabel));
        Assert.Equal((null, "Chưa có bài kiểm tra"), (byId[none].Difficulty, byId[none].DifficultyLabel));
        Assert.Equal((null, "Chưa có bài kiểm tra", 1), (byId[empty].Difficulty, byId[empty].DifficultyLabel, byId[empty].QuizCount));   // có bài kiểm tra nhưng chưa có câu
        Assert.Equal((null, "Chưa có bài kiểm tra"), (byId[s.LessonId].Difficulty, byId[s.LessonId].DifficultyLabel));
    }

    [Fact]
    public async Task The_difficulty_filter_keeps_only_matching_lessons_and_combines_with_the_other_filters()
    {
        var s = await _k.SetupAsync(2);
        var easy = await LessonWithQuizAsync(s, "Bài dễ", (DifficultyLevel.EASY, 2));
        var hard = await LessonWithQuizAsync(s, "Bài khó", (DifficultyLevel.HARD, 2), (DifficultyLevel.EASY, 1));
        var otherClass = await _k.Questions.Lesson.Classroom.CreateClassAsync(s.Token, "9A1", 9);
        var hardElsewhere = await _k.Questions.Lesson.PublishedLessonAsync(s.Token, otherClass.Id, [s.Heritage.Id], "Bài khó ở lớp khác");
        var quiz = await _k.CreateAsync(s, "Kiểm tra", lessonId: hardElsewhere.Id);
        await _k.AddQuestionsAsync(s.Token, quiz.Id, [(await _k.QuestionAsync(s, QuestionType.SINGLE_CHOICE, DifficultyLevel.HARD)).Id]);

        var easyPage = await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{L}?difficulty=EASY&pageSize=100", s.Token));
        Assert.Equal([easy], easyPage.Items.Select(x => x.Id));
        var hardPage = await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{L}?difficulty=HARD&pageSize=100", s.Token));
        Assert.Equal(new[] { hard, hardElsewhere.Id }.Order(), hardPage.Items.Select(x => x.Id).Order());
        var hardInClass = await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{L}?difficulty=HARD&classId={otherClass.Id}", s.Token));
        Assert.Equal([hardElsewhere.Id], hardInClass.Items.Select(x => x.Id));
        var mediumPage = await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{L}?difficulty=MEDIUM", s.Token));
        Assert.Empty(mediumPage.Items);
        Assert.Equal(0, mediumPage.TotalItems);

        var (res, body) = await Http.GetAsync<object>($"{L}?difficulty=IMPOSSIBLE", s.Token);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.False(body.Success);
    }

    [Fact]
    public async Task A_deleted_quiz_does_not_count_toward_the_difficulty_and_the_filter_only_sees_the_teachers_own_lessons()
    {
        var s = await _k.SetupAsync(2);
        var lesson = await LessonWithQuizAsync(s, "Bài dễ", (DifficultyLevel.EASY, 1));
        var hardQuiz = await _k.CreateAsync(s, "Nhiều câu khó", lessonId: lesson);
        var hardIds = new List<long>();
        for (var i = 0; i < 3; i++) hardIds.Add((await _k.QuestionAsync(s, QuestionType.SINGLE_CHOICE, DifficultyLevel.HARD)).Id);
        await _k.AddQuestionsAsync(s.Token, hardQuiz.Id, hardIds);
        Assert.Equal(DifficultyLevel.HARD, (await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{L}?difficulty=HARD", s.Token))).Items.Single(x => x.Id == lesson).Difficulty);

        await Http.DeleteAsync<object>($"{QuizTestKit.Quizzes}/{hardQuiz.Id}", s.Token);   // nháp xóa mềm: không tính nữa
        Assert.Equal(DifficultyLevel.EASY, (await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{L}?pageSize=100", s.Token))).Items.Single(x => x.Id == lesson).Difficulty);
        Assert.DoesNotContain(lesson, (await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{L}?difficulty=HARD", s.Token))).Items.Select(x => x.Id));

        // Giáo viên khác cùng trường không thấy bài này dù lọc đúng mức độ khó.
        var (_, otherToken) = await _k.Questions.Lesson.Classroom.AnotherTeacherAsync(s.School);
        Assert.Empty((await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{L}?difficulty=EASY", otherToken))).Items);
    }
}
