using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.Statistics;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Kết quả một bài kiểm tra của lớp cho giáo viên: tổng quan (sĩ số, đã nộp, hết giờ tự nộp, đang làm, chưa làm, điểm trung bình, cao nhất, thấp nhất, phổ điểm),
/// từng câu (tỉ lệ đúng, phương án bị chọn nhiều nhất, lỗi hay gặp của câu nhiều phần), từng học sinh, theo chủ đề và chi tiết bài làm; giáo viên luôn thấy điểm
/// ở mọi cấp; bài của giáo viên khác như không tồn tại.
/// </summary>
public sealed class TeacherQuizResultsApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly StatsTestKit _k = new(factory);

    /// <summary>Lớp cấp 2 có một bài kiểm tra (bốn câu × 2 điểm = 8 điểm) và năm học sinh: Em 1 làm đúng hết, Em 2 sai hết, Em 3 đang làm, Em 4 hết giờ tự nộp (đúng 2 câu), Em 5 chưa làm.</summary>
    private sealed record Scene(AttemptTestKit.Quiz Quiz, string Token, long ClassId, long Em1Attempt, long Em2Attempt, long Em4Attempt, long Em4Id)
    {
        public long QuizId => Quiz.Id;
    }

    private async Task<Scene> SceneAsync()
    {
        var quiz = await _k.QuizAsync(await _k.Quizzes.SetupAsync(2), "Em 1");
        var setup = quiz.Setup;
        var em1 = await _k.AllCorrectAsync(quiz);
        var em2Token = await _k.StudentTokenAsync(setup, "Em 2");
        var em2 = await _k.AllWrongAsync(quiz, em2Token);
        var em3Token = await _k.StudentTokenAsync(setup, "Em 3");
        await _k.Attempts.StartAsync(em3Token, quiz.Id);
        await _k.StudentTokenAsync(setup, "Em 5");
        var em4Id = await _k.StudentIdAsync(setup.ClassId, await AddStudentNameAsync(setup, "Em 4"));
        var em4 = await _k.SeedAttemptAsync(quiz.Id, em4Id, i => i < 2, AttemptStatus.EXPIRED);
        return new Scene(quiz, setup.Token, setup.ClassId, em1.AttemptId, em2.AttemptId, em4, em4Id);
    }

    private async Task<string> AddStudentNameAsync(QuizTestKit.Setup s, string name)
    {
        await _k.Quizzes.Questions.Lesson.Classroom.AddStudentAsync(s.Token, s.ClassId, name);
        return name;
    }

    // ---- Tổng quan ----

    [Fact]
    public async Task The_overview_counts_every_status_and_computes_the_average_highest_lowest_and_the_score_distribution()
    {
        var s = await SceneAsync();
        var overview = await _k.OverviewAsync(s.Token, s.QuizId);

        Assert.Equal((s.QuizId, s.Quiz.Detail.Name, 2, "Cấp 2", 4), (overview.QuizId, overview.Name, overview.Level, overview.LevelName, overview.QuestionCount));
        Assert.Equal(8m, overview.TotalPoint);
        Assert.Equal((5, 2, 1, 3, 1, 1), (overview.Enrolled, overview.Submitted, overview.AutoSubmitted, overview.Finished, overview.InProgress, overview.NotStarted));
        Assert.Equal((4m, 8m, 0m), (overview.AverageScore, overview.HighestScore, overview.LowestScore));
        Assert.Equal((50m, 100m, 0m), (overview.AveragePercent, overview.HighestPercent, overview.LowestPercent));
        Assert.Null(overview.Message);

        Assert.Equal(10, overview.Distribution.Count);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => (i * 10, (i + 1) * 10)), overview.Distribution.Select(b => (b.FromPercent, b.ToPercent)));
        Assert.Equal(new[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 1 }, overview.Distribution.Select(b => b.Count));   // 0%, 50% và 100% (100% thuộc khoảng cuối)
    }

    [Fact]
    public async Task A_quiz_nobody_has_submitted_has_no_scores_and_says_so()
    {
        var quiz = await _k.QuizAsync(await _k.Quizzes.SetupAsync(2), "Em 1");
        var overview = await _k.OverviewAsync(quiz.TeacherToken, quiz.Id);

        Assert.Equal((1, 0, 0, 0, 0, 1), (overview.Enrolled, overview.Submitted, overview.AutoSubmitted, overview.Finished, overview.InProgress, overview.NotStarted));
        Assert.Null(overview.AverageScore);
        Assert.Null(overview.HighestPercent);
        Assert.Equal(Messages.ClassNoResultsYet, overview.Message);
        Assert.All(overview.Distribution, b => Assert.Equal(0, b.Count));
    }

    [Fact]
    public async Task Only_active_students_are_counted_and_a_deactivated_student_leaves_the_numbers()
    {
        var s = await SceneAsync();
        await _k.SetStudentActiveAsync(s.Em4Id, false);
        var overview = await _k.OverviewAsync(s.Token, s.QuizId);

        Assert.Equal((4, 2, 0, 2, 1, 1), (overview.Enrolled, overview.Submitted, overview.AutoSubmitted, overview.Finished, overview.InProgress, overview.NotStarted));
        Assert.Equal(4m, overview.AverageScore);
        var students = await _k.QuizStudentsAsync(s.Token, s.QuizId);
        Assert.DoesNotContain(students.Students, r => r.StudentId == s.Em4Id);
    }

    // ---- Từng học sinh ----

    [Fact]
    public async Task The_student_tab_lists_everyone_with_the_status_score_time_and_badges()
    {
        var s = await SceneAsync();
        var list = await _k.QuizStudentsAsync(s.Token, s.QuizId);

        Assert.Equal(2, list.Level);
        Assert.Equal(["Em 1", "Em 2", "Em 3", "Em 4", "Em 5"], list.Students.Select(r => r.FullName).Order());
        var rows = list.Students.ToDictionary(r => r.FullName!);

        var em1 = rows["Em 1"];
        Assert.Equal((QuizStudentStatus.SUBMITTED, "Đã nộp", s.Em1Attempt, 8m, 8m, 100m), (em1.Status, em1.StatusLabel, em1.AttemptId, em1.Score, em1.TotalPoint, em1.Percent));
        Assert.Equal((4, 4), (em1.CorrectCount, em1.TotalQuestions));
        Assert.Null(em1.Stars);   // lớp cấp 2 không có sao
        Assert.Equal([BadgeCode.PERFECT_SCORE, BadgeCode.ON_TIME], em1.Badges.Select(b => b.Code));
        Assert.NotNull(em1.SubmittedAt);

        var em2 = rows["Em 2"];
        Assert.Equal((QuizStudentStatus.SUBMITTED, 0m, 0m), (em2.Status, em2.Score, em2.Percent));

        var em3 = rows["Em 3"];
        Assert.Equal((QuizStudentStatus.IN_PROGRESS, "Đang làm"), (em3.Status, em3.StatusLabel));
        Assert.Null(em3.Score);
        Assert.NotNull(em3.AttemptId);

        var em4 = rows["Em 4"];
        Assert.Equal((QuizStudentStatus.EXPIRED, "Hết giờ, tự nộp", 4m, 50m), (em4.Status, em4.StatusLabel, em4.Score, em4.Percent));
        Assert.DoesNotContain(BadgeCode.ON_TIME, em4.Badges.Select(b => b.Code));

        var em5 = rows["Em 5"];
        Assert.Equal((QuizStudentStatus.NOT_STARTED, "Chưa làm"), (em5.Status, em5.StatusLabel));
        Assert.Null(em5.AttemptId);
        Assert.Null(em5.Score);
    }

    [Fact]
    public async Task For_a_level_one_class_the_teacher_still_sees_scores_and_also_the_stars()
    {
        var quiz = await _k.QuizAsync(await _k.Quizzes.SetupAsync(1), "Em 1");
        await _k.AllCorrectAsync(quiz);
        var other = await _k.StudentTokenAsync(quiz.Setup, "Em 2");
        await _k.AllWrongAsync(quiz, other);

        var list = await _k.QuizStudentsAsync(quiz.TeacherToken, quiz.Id);
        var rows = list.Students.ToDictionary(r => r.FullName!);
        Assert.Equal((1, 3, 8m, 100m), (list.Level, rows["Em 1"].Stars, rows["Em 1"].Score, rows["Em 1"].Percent));
        Assert.Equal((1, 0m), (rows["Em 2"].Stars, rows["Em 2"].Score));
        Assert.Equal(50m, (await _k.OverviewAsync(quiz.TeacherToken, quiz.Id)).AveragePercent);
    }

    // ---- Chi tiết bài làm ----

    [Fact]
    public async Task The_attempt_detail_shows_each_question_with_the_given_answer_the_right_answer_points_and_explanation_even_when_the_quiz_hides_answers_from_students()
    {
        var quiz = await _k.QuizAsync(await _k.Quizzes.SetupAsync(2), "Em 1", showAnswers: false);
        var result = await _k.AllWrongAsync(quiz);
        var studentView = await _k.Attempts.ResultAsync(quiz.StudentToken, result.AttemptId);
        Assert.False(studentView.AnswersShown);
        Assert.Null(studentView.Questions);

        var detail = await _k.AttemptDetailAsync(quiz.TeacherToken, result.AttemptId);
        Assert.Equal((quiz.StudentCode, "Em 1", quiz.Id, 2), (detail.StudentCode, detail.FullName, detail.QuizId, detail.Level));
        Assert.Equal((0m, 8m, 0m, 0, 4), (detail.Score, detail.TotalPoint, detail.Percent, detail.CorrectCount, detail.TotalQuestions));
        Assert.Null(detail.Stars);
        Assert.Equal(4, detail.Questions.Count);
        Assert.All(detail.Questions, q =>
        {
            Assert.Equal((false, 0m), (q.IsCorrect, q.PointEarned));
            Assert.NotNull(q.CorrectAnswer);
            Assert.NotNull(q.Question.Answer);        // câu em đã trả lời
            Assert.Equal(2m, q.Question.Point);
        });
        Assert.Contains(detail.Questions, q => q.PartsTotal > 1 && q.PartsCorrect == 0);   // câu nhiều phần hiện số phần đúng
    }

    [Fact]
    public async Task The_attempt_detail_of_a_level_one_class_adds_the_stars_for_the_teacher()
    {
        var quiz = await _k.QuizAsync(await _k.Quizzes.SetupAsync(1), "Em 1");
        var result = await _k.AllCorrectAsync(quiz);
        var detail = await _k.AttemptDetailAsync(quiz.TeacherToken, result.AttemptId);

        Assert.Equal((3, 8m, 100m), (detail.Stars, detail.Score, detail.Percent));
        Assert.All(detail.Questions, q => Assert.Equal((true, 2m), (q.IsCorrect, q.PointEarned)));
        Assert.Equal([BadgeCode.PERFECT_SCORE, BadgeCode.ON_TIME], detail.Badges.Select(b => b.Code));
    }

    [Fact]
    public async Task An_attempt_still_in_progress_has_no_detail_and_an_auto_submitted_one_does()
    {
        var s = await SceneAsync();
        var inProgress = (await _k.QuizStudentsAsync(s.Token, s.QuizId)).Students.Single(r => r.FullName == "Em 3").AttemptId!.Value;

        var (res, body) = await _k.TeacherGetAsync<TeacherAttemptDetailDto>(s.Token, $"/attempts/{inProgress}");
        Assert.Equal((HttpStatusCode.Conflict, MessageCodes.Blocked), (res.StatusCode, body.ErrorCode));

        var expired = await _k.AttemptDetailAsync(s.Token, s.Em4Attempt);
        Assert.Equal((AttemptStatus.EXPIRED, "Tự nộp khi hết giờ", 4m), (expired.Status, expired.StatusLabel, expired.Score));
    }

    // ---- Từng câu ----

    [Fact]
    public async Task The_question_tab_gives_the_rate_the_most_chosen_option_and_the_common_errors_of_multi_part_questions()
    {
        var quiz = await _k.QuizAsync(await _k.Quizzes.SetupAsync(2), "Em 1");
        await _k.AllCorrectAsync(quiz);
        foreach (var name in new[] { "Em 2", "Em 3" })
            await _k.AllWrongAsync(quiz, await _k.StudentTokenAsync(quiz.Setup, name));

        var stats = await _k.QuestionStatsAsync(quiz.TeacherToken, quiz.Id);
        Assert.Equal((3, 4), (stats.Finished, stats.Questions.Count));
        Assert.Equal([1, 2, 3, 4], stats.Questions.Select(q => q.Position));
        Assert.All(stats.Questions, q => Assert.Equal((3, 1, 33.3m, 2m), (q.Answered, q.Correct, q.RatePercent, q.Point)));
        Assert.All(stats.Questions, q => Assert.Equal(quiz.Setup.Heritage.Id, q.HeritageId));

        // Câu một đáp án đúng: hai em chọn cùng một phương án sai, nên đó là phương án chọn nhiều nhất.
        var single = stats.Questions.Single(q => q.QuestionType == QuestionType.SINGLE_CHOICE);
        Assert.Empty(single.Parts);
        Assert.Empty(single.CommonErrors);
        Assert.Equal(4, single.Choices.Count);
        Assert.Equal(3, single.Choices.Sum(c => c.Count));
        Assert.Equal((2, false), (single.MostChosen!.Count, single.MostChosen.IsCorrect));
        Assert.Equal(66.7m, single.MostChosen.Percent);
        Assert.Equal(1, single.Choices.Single(c => c.IsCorrect).Count);

        // Điền từ: từng chỗ trống có tỉ lệ đúng riêng và chữ gõ sai hay gặp nhất.
        var fill = stats.Questions.Single(q => q.QuestionType == QuestionType.FILL_BLANK);
        Assert.Null(fill.MostChosen);
        Assert.Empty(fill.Choices);
        Assert.All(fill.Parts, p => Assert.Equal((3, 1, 33.3m), (p.Answered, p.Correct, p.RatePercent)));
        Assert.StartsWith("Chỗ trống 1", fill.Parts[0].Label);
        Assert.All(fill.CommonErrors, e => Assert.Equal((2, 66.7m, "sai hoàn toàn"), (e.Wrong, e.WrongPercent, e.MostCommonWrong)));

        // Sắp xếp: có phần cho từng mục; lỗi hay gặp xếp theo số lần sai giảm dần, tối đa ba.
        var ordering = stats.Questions.Single(q => q.QuestionType == QuestionType.ORDERING);
        Assert.True(ordering.Parts.Count >= 3);
        Assert.All(ordering.Parts, p => Assert.Equal(3, p.Answered));
        Assert.InRange(ordering.CommonErrors.Count, 1, 3);
        Assert.Equal(ordering.CommonErrors.Select(e => e.Wrong).OrderByDescending(x => x), ordering.CommonErrors.Select(e => e.Wrong));
        Assert.All(ordering.CommonErrors, e => Assert.Null(e.MostCommonWrong));

        // Theo dạng câu: bốn dạng của cấp 2, mỗi dạng một câu.
        Assert.Equal(4, stats.ByQuestionType.Count);
        Assert.All(stats.ByQuestionType, t => Assert.Equal((3, 1, 33.3m, MasteryLevel.NEEDS_REVIEW, true), (t.Answered, t.Correct, t.RatePercent, t.Level, t.NeedsReview)));
    }

    [Fact]
    public async Task For_matching_the_common_error_names_the_wrong_name_students_connect_most_and_for_true_false_options_every_choice_is_counted()
    {
        var quiz = await _k.QuizAsync(await _k.Quizzes.SetupAsync(1), "Em 1");   // cấp 1: Đúng/Sai, chọn hình đúng, ghép cặp, sắp xếp
        foreach (var name in new[] { "Em 2", "Em 3" }) await _k.AllWrongAsync(quiz, await _k.StudentTokenAsync(quiz.Setup, name));
        await _k.AllCorrectAsync(quiz);

        var stats = await _k.QuestionStatsAsync(quiz.TeacherToken, quiz.Id);
        var matching = stats.Questions.Single(q => q.QuestionType == QuestionType.MATCHING);
        Assert.True(matching.Parts.Count >= 3);
        Assert.All(matching.Parts, p => Assert.Equal((3, 1), (p.Answered, p.Correct)));
        Assert.All(matching.CommonErrors, e =>
        {
            Assert.Equal(2, e.Wrong);
            Assert.False(string.IsNullOrWhiteSpace(e.MostCommonWrong));
        });

        var trueFalse = stats.Questions.Single(q => q.QuestionType == QuestionType.TRUE_FALSE);
        Assert.Equal(3, trueFalse.Choices.Sum(c => c.Count));
        Assert.True(trueFalse.Choices.All(c => c.AnswerId is not null));

        var image = stats.Questions.Single(q => q.QuestionType == QuestionType.IMAGE_CHOICE);
        Assert.All(image.Choices, c => Assert.NotNull(c.ImageUrl));
        Assert.Equal(2, image.MostChosen!.Count);
    }

    [Fact]
    public async Task For_multi_statement_questions_the_common_error_says_whether_students_chose_true_or_false()
    {
        var setup = await _k.Quizzes.SetupAsync(3);
        var quiz = await _k.MixedQuizAsync(setup, "Em 1", (setup, QuestionType.MULTI_TRUE_FALSE), (setup, QuestionType.SOURCE_READING));
        await _k.AllCorrectAsync(quiz);
        foreach (var name in new[] { "Em 2", "Em 3" }) await _k.AllWrongAsync(quiz, await _k.StudentTokenAsync(setup, name));

        var stats = await _k.QuestionStatsAsync(setup.Token, quiz.Id);
        var multi = stats.Questions.Single(q => q.QuestionType == QuestionType.MULTI_TRUE_FALSE);
        Assert.True(multi.Parts.Count >= 3);
        Assert.All(multi.Parts, p => Assert.Equal((3, 1), (p.Answered, p.Correct)));
        Assert.All(multi.CommonErrors, e =>
        {
            Assert.Equal(2, e.Wrong);
            Assert.Contains(e.MostCommonWrong, new[] { "Em chọn Đúng", "Em chọn Sai" });
        });
        var reading = stats.Questions.Single(q => q.QuestionType == QuestionType.SOURCE_READING);
        Assert.Equal((3, 1), (reading.Answered, reading.Correct));
        Assert.Equal(2, reading.MostChosen!.Count);
    }

    // ---- Theo chủ đề ----

    [Fact]
    public async Task The_topic_tab_groups_the_answers_by_heritage_and_by_question_type_and_marks_what_to_review()
    {
        var a = await _k.Quizzes.SetupAsync(2);
        var b = await _k.LessonOnNewHeritageAsync(a, "Văn Miếu", "46");
        var quiz = await _k.MixedQuizAsync(a, "Em 1",
            (a, QuestionType.SINGLE_CHOICE), (a, QuestionType.TRUE_FALSE), (b, QuestionType.SINGLE_CHOICE), (b, QuestionType.TRUE_FALSE));
        var heritageA = a.Heritage.Id;
        var em1 = await _k.Attempts.AnswerAllAndSubmitAsync(quiz, (shown, detail) =>
            IsOfHeritage(quiz, shown.QuestionId, heritageA) ? AttemptTestKit.Correct(shown, detail) : AttemptTestKit.Wrong(shown, detail));
        Assert.Equal(4m, em1.Score);   // đúng hai câu của di sản A, sai hai câu của di sản B

        var topics = await _k.QuizTopicsAsync(a.Token, quiz.Id);
        Assert.Equal(2, topics.ByHeritage.Count);
        var byHeritage = topics.ByHeritage.ToDictionary(t => t.HeritageId!.Value);
        Assert.Equal((2, 2, 100m, MasteryLevel.INSUFFICIENT_DATA, false), (byHeritage[heritageA].Answered, byHeritage[heritageA].Correct, byHeritage[heritageA].RatePercent,
            byHeritage[heritageA].Level, byHeritage[heritageA].NeedsReview));
        var weak = byHeritage[b.Heritage.Id];
        Assert.Equal((2, 0, 0m, true), (weak.Answered, weak.Correct, weak.RatePercent, weak.NeedsReview));
        Assert.Equal(b.Heritage.Id, topics.ByHeritage[0].HeritageId);   // yếu nhất đứng đầu
        Assert.Equal(2, topics.ByQuestionType.Count);
        Assert.All(topics.ByQuestionType, t => Assert.Equal((2, 1, 50m), (t.Answered, t.Correct, t.RatePercent)));
    }

    private static bool IsOfHeritage(AttemptTestKit.Quiz quiz, long questionId, long heritageId) =>
        quiz.Questions.Single(q => q.Id == questionId).HeritageId == heritageId;

    // ---- Quyền ----

    [Fact]
    public async Task Another_teacher_the_school_admin_and_students_cannot_read_the_results_and_the_unknown_is_not_found()
    {
        var s = await SceneAsync();
        var paths = new[]
        {
            $"/quizzes/{s.QuizId}/results", $"/quizzes/{s.QuizId}/results/questions", $"/quizzes/{s.QuizId}/results/students",
            $"/quizzes/{s.QuizId}/results/topics", $"/quizzes/{s.QuizId}/results/recommendations", $"/attempts/{s.Em1Attempt}",
        };
        var (_, otherTeacher) = await _k.Quizzes.Questions.Lesson.Classroom.AnotherTeacherAsync(s.Quiz.Setup.School);
        foreach (var path in paths)
        {
            Assert.Equal(HttpStatusCode.NotFound, await _k.StatusOfAsync(StatsTestKit.Teacher + path, otherTeacher));                  // giáo viên khác cùng trường
            Assert.Equal(HttpStatusCode.Forbidden, await _k.StatusOfAsync(StatsTestKit.Teacher + path, s.Quiz.Setup.School.OrgAdminToken));
            Assert.Equal(HttpStatusCode.Forbidden, await _k.StatusOfAsync(StatsTestKit.Teacher + path, s.Quiz.StudentToken));
            Assert.Equal(HttpStatusCode.Unauthorized, await _k.StatusOfAsync(StatsTestKit.Teacher + path, null));
            Assert.Equal(HttpStatusCode.OK, await _k.StatusOfAsync(StatsTestKit.Teacher + path, s.Token));
        }
        Assert.Equal(HttpStatusCode.NotFound, await _k.StatusOfAsync($"{StatsTestKit.Teacher}/quizzes/{s.QuizId + 9999}/results", s.Token));
        Assert.Equal(HttpStatusCode.NotFound, await _k.StatusOfAsync($"{StatsTestKit.Teacher}/attempts/{s.Em1Attempt + 9999}", s.Token));
    }

    [Fact]
    public async Task A_quiz_in_another_school_and_a_deleted_quiz_are_not_found()
    {
        var s = await SceneAsync();
        var stranger = await _k.QuizAsync(await _k.Quizzes.SetupAsync(2), "Em lạ");
        Assert.Equal(HttpStatusCode.NotFound, await _k.StatusOfAsync($"{StatsTestKit.Teacher}/quizzes/{s.QuizId}/results", stranger.TeacherToken));
        Assert.Equal(HttpStatusCode.NotFound, await _k.StatusOfAsync($"{StatsTestKit.Teacher}/attempts/{s.Em1Attempt}", stranger.TeacherToken));

        await _k.SoftDeleteQuizAsync(s.QuizId);
        Assert.Equal(HttpStatusCode.NotFound, await _k.StatusOfAsync($"{StatsTestKit.Teacher}/quizzes/{s.QuizId}/results", s.Token));
        Assert.Equal(HttpStatusCode.NotFound, await _k.StatusOfAsync($"{StatsTestKit.Teacher}/attempts/{s.Em1Attempt}", s.Token));
    }
}
