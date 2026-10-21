using System.Net;
using System.Text.Json;
using DSVHVN.Application.Attempts;
using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Recommendations;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// "Gợi ý cho em": năm phần tính riêng cho từng học sinh khi hiển thị (quiz nên làm tiếp, bài học nên ôn lại, di sản nên ôn, di sản liên quan, bài học mới nên học),
/// ở trang học và ở trang kết quả của một lượt; chỉ trong lớp của em, chỉ di sản đã xuất bản, không có mã bài học ở bài mới, cấp 1 không số liệu, dự phòng khi
/// Dịch vụ AI lỗi và không gửi dữ liệu cá nhân sang Dịch vụ AI.
/// </summary>
public sealed class StudentRecommendationApiTests : IClassFixture<ApiFactory>
{
    private readonly StatsTestKit _k;
    private readonly ApiFactory _factory;

    public StudentRecommendationApiTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.RecommendationAi.Reset();
        _k = new StatsTestKit(factory);
    }

    /// <summary>
    /// Lớp cấp 2 có ba bài: A (Hoàng thành, Hà Nội, vật thể), B (Văn Miếu, Hà Nội, vật thể: giống A) và C (Ca trù, TP. Hồ Chí Minh, phi vật thể: khác A); thêm hai di sản
    /// chưa có bài: D (Hà Nội, vật thể: giống A) và E (TP. Hồ Chí Minh, thiên nhiên). Em X là học sinh của quiz A.
    /// </summary>
    private sealed record Scene(
        QuizTestKit.Setup A, QuizTestKit.Setup B, QuizTestKit.Setup C, AttemptTestKit.Quiz QuizA, AttemptTestKit.Quiz QuizB, AttemptTestKit.Quiz QuizC,
        long HeritageD, long HeritageE)
    {
        public string X => QuizA.StudentToken;
    }

    private async Task<Scene> SceneAsync(int level = 2)
    {
        var a = await _k.Quizzes.SetupAsync(level);
        var province = StatsTestKit.NewProvinceCode();   // riêng cho nhóm A, B, D để di sản của test khác trong cùng CSDL không chen vào bảng xếp hạng
        await _k.SetHeritageProvinceAsync(a.Heritage.Id, province);
        var word = "nhom" + Guid.NewGuid().ToString("N")[..6];   // chữ riêng của nhóm: B và D giống A hơn mọi di sản của test khác
        await _k.RenameHeritageAsync(a.Heritage.Id, $"Hoàng thành Phượng {word}");
        var quizA = await _k.QuizAsync(a, "Em X");
        var b = await _k.LessonOnNewHeritageAsync(a, $"Hoàng thành Huế {word}", province, HeritageType.TANGIBLE);
        var c = await _k.LessonOnNewHeritageAsync(a, "Ca trù", "79", HeritageType.INTANGIBLE, level: RecognitionLevel.PROVINCIAL, sameEra: false);
        var quizB = await _k.QuizAsync(b, "Em B");
        var quizC = await _k.QuizAsync(c, "Em C");
        var d = await _k.LooseHeritageAsync($"Hoàng thành Hà Nội {word}", province, HeritageType.TANGIBLE);
        var e = await _k.LooseHeritageAsync("Vịnh Hạ Long", "79", HeritageType.NATURAL, RecognitionLevel.PROVINCIAL, sameEra: false);
        return new Scene(a, b, c, quizA, quizB, quizC, d, e);
    }

    // ---- Em chưa có lượt nào ----

    [Fact]
    public async Task A_new_student_gets_the_open_quizzes_of_the_class_and_the_new_lessons_without_codes_and_an_invitation_to_enter_a_lesson_code()
    {
        var s = await SceneAsync();
        var recs = await _k.RecommendationsAsync(s.X);

        Assert.Equal((2, "Cấp 2", "FREE_STEPS", (long?)null), (recs.Level, recs.LevelName, recs.Layout, recs.AttemptId));
        Assert.Equal(Messages.StudentNoAttemptsYet, recs.Message);

        // (1) mọi quiz đang mở của lớp, tất cả cần mã bài học vì em chưa có lượt nào.
        Assert.Equal(new[] { s.QuizA.Id, s.QuizB.Id, s.QuizC.Id }.Order(), recs.NextQuizzes.Select(q => q.QuizId).Order());
        Assert.All(recs.NextQuizzes, q => Assert.Equal((true, StudentLearningService.NeedsLessonCodeLabel), (q.NeedsLessonCode, q.Label)));

        // (5) bài học mới: chỉ tiêu đề và câu nhờ thầy cô, không có mã.
        Assert.Equal(3, recs.NewLessons.Count);
        Assert.All(recs.NewLessons, l =>
        {
            Assert.Null(l.AccessCode);
            Assert.Equal(RecommendationRules.AskTeacherForLessonCode, l.Hint);
        });

        Assert.Empty(recs.LessonsToReview);
        Assert.Empty(recs.HeritagesToReview);
        Assert.Empty(recs.RelatedHeritages);   // chưa có di sản tham chiếu
        Assert.Null(recs.ReviewPraise);
        Assert.False(recs.RelatedFromFallback);
    }

    [Fact]
    public async Task A_student_in_a_class_with_no_published_lesson_only_gets_the_invitation()
    {
        var s = await _k.Quizzes.SetupAsync(2, publishLesson: false);
        var token = await _k.StudentTokenAsync(s, "Em trống");
        var recs = await _k.RecommendationsAsync(token);

        Assert.Equal(Messages.StudentNoAttemptsYet, recs.Message);
        Assert.Empty(recs.NextQuizzes);
        Assert.Empty(recs.LessonsToReview);
        Assert.Empty(recs.HeritagesToReview);
        Assert.Empty(recs.RelatedHeritages);
        Assert.Empty(recs.NewLessons);
    }

    // ---- Sau khi làm bài sai ----

    [Fact]
    public async Task After_a_wrong_attempt_the_five_sections_follow_the_results_and_the_new_lessons_prefer_heritages_close_to_what_the_student_has_learned()
    {
        var s = await SceneAsync();
        await _k.AllWrongAsync(s.QuizA, s.X);
        var recs = await _k.RecommendationsAsync(s.X);

        // (3) di sản nên ôn: A, kèm lý do có số câu sai.
        var review = Assert.Single(recs.HeritagesToReview);
        Assert.Equal((s.A.Heritage.Id, "Em chưa đúng 4 câu về di sản này"), (review.HeritageId, review.Reason));
        Assert.NotNull(review.Slug);

        // (2) bài học nên ôn lại: bài A, có mã vì em đã mở rồi.
        var lesson = Assert.Single(recs.LessonsToReview);
        Assert.Equal((s.A.LessonId, s.A.AccessCode), (lesson.LessonId, lesson.AccessCode));

        // (1) quiz nên làm tiếp: quiz đã làm không còn; bài B và C em chưa mở nên cần mã.
        Assert.Equal(new[] { s.QuizB.Id, s.QuizC.Id }.Order(), recs.NextQuizzes.Select(q => q.QuizId).Order());
        Assert.All(recs.NextQuizzes, q => Assert.True(q.NeedsLessonCode));

        // (5) bài học mới: B giống A (cùng tỉnh, loại) nên đứng trước C.
        Assert.Equal([s.B.LessonId, s.C.LessonId], recs.NewLessons.Select(l => l.LessonId));
        Assert.Contains("Cùng loại", recs.NewLessons[0].Reason);
        Assert.Equal(RecommendationRules.NewLessonReason, recs.NewLessons[1].Reason);

        // (4) di sản liên quan: B và D giống A nhất (cùng tỉnh, loại, cấp); không có di sản của chính bài A; C và E không có điểm chung với A.
        Assert.InRange(recs.RelatedHeritages.Count, 2, 3);
        Assert.Equal(new[] { s.B.Heritage.Id, s.HeritageD }.Order(), recs.RelatedHeritages.Take(2).Select(h => h.HeritageId).Order());
        Assert.DoesNotContain(recs.RelatedHeritages, h => h.HeritageId == s.A.Heritage.Id);
        Assert.DoesNotContain(recs.RelatedHeritages, h => h.HeritageId == s.C.Heritage.Id || h.HeritageId == s.HeritageE);
        Assert.All(recs.RelatedHeritages.Take(2), h => Assert.Contains("Hoàng thành", h.Reason));
        Assert.Null(recs.ReviewPraise);
        Assert.Null(recs.Message);
        Assert.Equal(DifficultyLevel.EASY, recs.TargetDifficulty);   // đúng 0/4 nên hợp mức Dễ
    }

    [Fact]
    public async Task A_student_who_answers_everything_right_gets_praise_instead_of_heritages_to_review_and_no_lesson_to_review()
    {
        var s = await SceneAsync();
        await _k.AllCorrectAsync(s.QuizA, s.X);
        var recs = await _k.RecommendationsAsync(s.X);

        Assert.Empty(recs.HeritagesToReview);
        Assert.Empty(recs.LessonsToReview);
        Assert.Equal(RecommendationRules.ReviewPraiseForLevel(2), recs.ReviewPraise);
        Assert.Equal(DifficultyLevel.HARD, recs.TargetDifficulty);
    }

    [Fact]
    public async Task The_result_page_reviews_only_the_viewed_attempt_while_the_home_page_reviews_every_attempt()
    {
        var s = await SceneAsync();
        var first = await _k.AllWrongAsync(s.QuizA, s.X);
        var second = await _k.AllWrongAsync(s.QuizB, s.X);

        var home = await _k.RecommendationsAsync(s.X);
        Assert.Equal([s.A.Heritage.Id, s.B.Heritage.Id], home.HeritagesToReview.Select(h => h.HeritageId));

        var secondPage = await _k.AttemptRecommendationsAsync(s.X, second.AttemptId);
        Assert.Equal(second.AttemptId, secondPage.AttemptId);
        Assert.Equal([s.B.Heritage.Id], secondPage.HeritagesToReview.Select(h => h.HeritageId));

        var firstPage = await _k.AttemptRecommendationsAsync(s.X, first.AttemptId);
        Assert.Equal([s.A.Heritage.Id], firstPage.HeritagesToReview.Select(h => h.HeritageId));
        // Hai lượt cùng nằm trong "bài nên ôn lại" và quiz nên làm tiếp chỉ còn quiz C ở cả hai trang.
        Assert.Equal([s.QuizC.Id], firstPage.NextQuizzes.Select(q => q.QuizId));
        Assert.Equal(2, firstPage.LessonsToReview.Count);
    }

    [Fact]
    public async Task A_quiz_of_a_lesson_the_student_already_opened_is_not_marked_as_needing_the_code()
    {
        var s = await SceneAsync();
        var second = await _k.QuizAsync(s.A, "Em phụ");
        await _k.AllWrongAsync(s.QuizA, s.X);
        var recs = await _k.RecommendationsAsync(s.X);

        var sameLesson = recs.NextQuizzes.Single(q => q.QuizId == second.Id);
        Assert.Equal((false, (string?)null), (sameLesson.NeedsLessonCode, sameLesson.Label));
        Assert.True(recs.NextQuizzes.Where(q => q.QuizId != second.Id).All(q => q.NeedsLessonCode));
    }

    // ---- Xếp hạng quiz ----

    [Fact]
    public async Task Quizzes_with_questions_on_a_weak_heritage_come_first_then_the_nearest_closing_time()
    {
        var s = await SceneAsync();
        var again = await _k.QuizAsync(s.A, "Em phụ");                     // thêm quiz về di sản A (em sẽ còn yếu)
        await _k.Quizzes.SetQuizTimesAsync(again.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddDays(10));
        await _k.Quizzes.SetQuizTimesAsync(s.QuizB.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(2));
        await _k.Quizzes.SetQuizTimesAsync(s.QuizC.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(30));

        // Chưa có lượt nào và mọi quiz cùng độ khó Trung bình (đúng mức mục tiêu của cấp 2): theo hạn đóng gần nhất trước.
        var before = await _k.RecommendationsAsync(s.X);
        Assert.Equal([s.QuizB.Id, s.QuizA.Id, s.QuizC.Id], before.NextQuizzes.Select(q => q.QuizId).Take(3));

        // Sau khi sai ở A: quiz về A đứng đầu dù hạn đóng xa nhất.
        await _k.AllWrongAsync(s.QuizA, s.X);
        var after = await _k.RecommendationsAsync(s.X);
        Assert.Equal(again.Id, after.NextQuizzes[0].QuizId);
        Assert.Equal("Có 4 câu về di sản em còn yếu", after.NextQuizzes[0].Reason);
        Assert.Equal([s.QuizB.Id, s.QuizC.Id], after.NextQuizzes.Skip(1).Select(q => q.QuizId));
        Assert.Equal("Sắp đóng", after.NextQuizzes[1].Reason);
    }

    [Fact]
    public async Task Among_equal_quizzes_the_one_whose_difficulty_is_closest_to_the_target_comes_first_and_at_most_three_are_shown()
    {
        var s = await SceneAsync();
        var extra1 = await _k.QuizAsync(s.B, "Phụ 1");
        var extra2 = await _k.QuizAsync(s.B, "Phụ 2");
        var close = DateTime.UtcNow.AddDays(20);
        foreach (var id in new[] { s.QuizA.Id, s.QuizB.Id, s.QuizC.Id, extra1.Id, extra2.Id })
            await _k.Quizzes.SetQuizTimesAsync(id, DateTime.UtcNow.AddHours(-1), close);   // cùng hạn đóng
        await _k.SetQuizDifficultyAsync(s.QuizB.Id, DifficultyLevel.HARD);

        var recs = await _k.RecommendationsAsync(s.QuizA.StudentToken);
        Assert.Equal(DifficultyLevel.MEDIUM, recs.TargetDifficulty);
        Assert.Equal(3, recs.NextQuizzes.Count);
        Assert.DoesNotContain(recs.NextQuizzes, q => q.QuizId == s.QuizB.Id);               // Khó lệch 1 bậc so với mức mục tiêu nên xếp sau
        Assert.All(recs.NextQuizzes, q => Assert.Equal("Trung bình", q.DifficultyName));
    }

    /// <summary>
    /// Em chưa có lượt nào: quiz xếp theo độ khó gần mức khó mục tiêu của cấp trước (cấp 1 Dễ, cấp 2 và 3 Trung bình), hạn đóng chỉ phân thứ tự giữa các quiz
    /// cùng độ lệch. A Khó đóng sớm nhất, B Dễ đóng sau, C Trung bình đóng muộn nhất.
    /// </summary>
    [Theory]
    [InlineData(1, "B,C,A")]   // Dễ (lệch 0), Trung bình (lệch 1), Khó (lệch 2)
    [InlineData(2, "C,A,B")]   // Trung bình (lệch 0); A và B cùng lệch 1 thì A đóng sớm hơn
    [InlineData(3, "C,A,B")]
    public async Task Without_any_attempt_the_quizzes_closest_to_the_target_difficulty_of_the_level_come_first_then_the_nearest_closing_time(int level, string expected)
    {
        var s = await SceneAsync(level);
        await _k.SetQuizDifficultyAsync(s.QuizA.Id, DifficultyLevel.HARD);
        await _k.SetQuizDifficultyAsync(s.QuizB.Id, DifficultyLevel.EASY);
        await _k.SetQuizDifficultyAsync(s.QuizC.Id, DifficultyLevel.MEDIUM);
        await _k.Quizzes.SetQuizTimesAsync(s.QuizA.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(2));
        await _k.Quizzes.SetQuizTimesAsync(s.QuizB.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(30));
        await _k.Quizzes.SetQuizTimesAsync(s.QuizC.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddDays(10));
        var byName = new Dictionary<string, long> { ["A"] = s.QuizA.Id, ["B"] = s.QuizB.Id, ["C"] = s.QuizC.Id };

        var recs = await _k.RecommendationsAsync(s.X);

        Assert.Equal(Messages.StudentNoAttemptsYet, recs.Message);
        Assert.Equal(expected.Split(',').Select(n => byName[n]), recs.NextQuizzes.Select(q => q.QuizId));
    }

    [Fact]
    public async Task After_an_attempt_without_a_weak_heritage_the_quizzes_still_follow_the_closing_time_before_the_difficulty()
    {
        var s = await SceneAsync();
        await _k.AllCorrectAsync(s.QuizA, s.X);                                         // đúng hết: mức mục tiêu Khó, không có di sản yếu
        await _k.SetQuizDifficultyAsync(s.QuizB.Id, DifficultyLevel.MEDIUM);
        await _k.SetQuizDifficultyAsync(s.QuizC.Id, DifficultyLevel.HARD);
        await _k.Quizzes.SetQuizTimesAsync(s.QuizB.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(2));
        await _k.Quizzes.SetQuizTimesAsync(s.QuizC.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddDays(10));

        var recs = await _k.RecommendationsAsync(s.X);

        Assert.Equal(DifficultyLevel.HARD, recs.TargetDifficulty);
        Assert.Equal([s.QuizB.Id, s.QuizC.Id], recs.NextQuizzes.Select(q => q.QuizId));   // B đóng sớm hơn dù C mới đúng mức Khó
    }

    [Fact]
    public async Task The_reason_of_a_quiz_that_closes_within_three_days_counts_the_days()
    {
        var s = await SceneAsync();
        await _k.Quizzes.SetQuizTimesAsync(s.QuizB.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddDays(2).AddHours(12));
        await _k.Quizzes.SetQuizTimesAsync(s.QuizC.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddDays(10));
        var recs = await _k.RecommendationsAsync(s.X);
        Assert.Equal("Còn 3 ngày", recs.NextQuizzes.Single(q => q.QuizId == s.QuizB.Id).Reason);
        Assert.Equal(RecommendationRules.OpenQuizReason, recs.NextQuizzes.Single(q => q.QuizId == s.QuizC.Id).Reason);
    }

    // ---- Xếp hạng bài học mới ----

    /// <summary>
    /// Em chưa có lượt nào: bài học mới xếp theo độ khó quiz của bài gần mức khó mục tiêu của cấp trước, rồi bài mới tạo trước. Bốn bài tạo theo thứ tự A, B, C, D
    /// với quiz A Trung bình, B Khó, C Dễ, D Khó; chỉ hiện 3 bài.
    /// </summary>
    [Theory]
    [InlineData(1, "C,A,D")]   // Dễ (lệch 0), Trung bình (lệch 1); B và D cùng Khó (lệch 2) thì D mới tạo hơn
    [InlineData(2, "A,D,C")]   // Trung bình (lệch 0); B, C, D cùng lệch 1 thì bài mới tạo trước: D rồi C
    [InlineData(3, "A,D,C")]
    public async Task Without_any_attempt_the_new_lessons_closest_to_the_target_difficulty_of_the_level_come_first_then_the_newest_created(int level, string expected)
    {
        var s = await SceneAsync(level);
        var d = await _k.LessonOnNewHeritageAsync(s.A, "Đờn ca tài tử", "79", HeritageType.INTANGIBLE);
        var quizD = await _k.QuizAsync(d, "Em D");
        await _k.SetQuizDifficultyAsync(s.QuizA.Id, DifficultyLevel.MEDIUM);
        await _k.SetQuizDifficultyAsync(s.QuizB.Id, DifficultyLevel.HARD);
        await _k.SetQuizDifficultyAsync(s.QuizC.Id, DifficultyLevel.EASY);
        await _k.SetQuizDifficultyAsync(quizD.Id, DifficultyLevel.HARD);
        var created = DateTime.UtcNow.AddDays(-10);
        await _k.SetLessonCreatedAtAsync(s.A.LessonId, created);
        await _k.SetLessonCreatedAtAsync(s.B.LessonId, created.AddDays(1));
        await _k.SetLessonCreatedAtAsync(s.C.LessonId, created.AddDays(2));
        await _k.SetLessonCreatedAtAsync(d.LessonId, created.AddDays(3));
        var byName = new Dictionary<string, long> { ["A"] = s.A.LessonId, ["B"] = s.B.LessonId, ["C"] = s.C.LessonId, ["D"] = d.LessonId };

        var recs = await _k.RecommendationsAsync(s.X);

        Assert.Equal(expected.Split(',').Select(n => byName[n]), recs.NewLessons.Select(l => l.LessonId));
        Assert.All(recs.NewLessons, l => Assert.Equal((RecommendationRules.NewLessonReason, (string?)null), (l.Reason, l.AccessCode)));
    }

    // ---- Chỉ trong lớp của em, chỉ di sản đã xuất bản ----

    [Fact]
    public async Task Quizzes_closed_not_yet_open_or_of_a_draft_lesson_and_those_of_other_classes_are_never_suggested()
    {
        var s = await SceneAsync();
        var other = await _k.Quizzes.SetupAsync(2);
        var otherQuiz = await _k.QuizAsync(other, "Em lớp khác");
        await _k.Quizzes.SetQuizTimesAsync(s.QuizB.Id, DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddHours(5));         // chưa tới giờ mở
        await _k.Quizzes.SetQuizTimesAsync(s.QuizC.Id, DateTime.UtcNow.AddHours(-3), DateTime.UtcNow.AddMinutes(-5));      // quá giờ đóng
        var recs = await _k.RecommendationsAsync(s.X);
        Assert.Equal([s.QuizA.Id], recs.NextQuizzes.Select(q => q.QuizId));

        await _k.SetLessonStatusAsync(s.B.LessonId, ContentStatus.ARCHIVED);
        var afterArchive = await _k.RecommendationsAsync(s.X);
        Assert.DoesNotContain(afterArchive.NewLessons, l => l.LessonId == s.B.LessonId);
        Assert.DoesNotContain(afterArchive.NextQuizzes, q => q.QuizId == s.QuizB.Id);

        var strangerRecs = await _k.RecommendationsAsync(otherQuiz.StudentToken);
        Assert.Equal([otherQuiz.Id], strangerRecs.NextQuizzes.Select(q => q.QuizId));
        Assert.Equal([other.LessonId], strangerRecs.NewLessons.Select(l => l.LessonId));
        Assert.DoesNotContain(strangerRecs.NewLessons, l => l.LessonId == s.A.LessonId);
    }

    [Fact]
    public async Task A_heritage_that_is_no_longer_published_is_never_suggested_to_review_or_as_related()
    {
        var s = await SceneAsync();
        await _k.AllWrongAsync(s.QuizA, s.X);
        await _k.Quizzes.Questions.Lesson.ArchiveHeritageAsync(s.A.Heritage.Id);
        await _k.Quizzes.Questions.Lesson.ArchiveHeritageAsync(s.HeritageD);
        var recs = await _k.RecommendationsAsync(s.X);

        Assert.Empty(recs.HeritagesToReview);
        Assert.DoesNotContain(recs.RelatedHeritages, h => h.HeritageId == s.HeritageD);
        Assert.DoesNotContain(recs.RelatedHeritages, h => h.HeritageId == s.A.Heritage.Id);
        Assert.Null(recs.ReviewPraise);   // vẫn có câu sai nên không khen
    }

    // ---- Theo cấp ----

    [Fact]
    public async Task For_a_level_one_pupil_the_content_is_the_same_but_the_reasons_carry_no_numbers()
    {
        var s = await SceneAsync(level: 1);
        await _k.AllWrongAsync(s.QuizA, s.X);
        var recs = await _k.RecommendationsAsync(s.X);

        Assert.Equal((1, "Cấp 1", "STEP_BY_STEP"), (recs.Level, recs.LevelName, recs.Layout));
        Assert.Equal(DifficultyLevel.EASY, recs.TargetDifficulty);
        Assert.Single(recs.HeritagesToReview);
        Assert.Single(recs.LessonsToReview);
        Assert.Equal(2, recs.NextQuizzes.Count);
        var reasons = recs.HeritagesToReview.Select(h => h.Reason)
            .Concat(recs.LessonsToReview.Select(l => l.Reason))
            .Concat(recs.NextQuizzes.Select(q => q.Reason));
        Assert.All(reasons, r =>
        {
            Assert.DoesNotContain("%", r);
            Assert.DoesNotContain(r, ch => char.IsDigit(ch));
        });
    }

    [Theory]
    [InlineData(1, DifficultyLevel.EASY)]
    [InlineData(2, DifficultyLevel.MEDIUM)]
    [InlineData(3, DifficultyLevel.MEDIUM)]
    public async Task Without_results_the_target_difficulty_follows_the_school_level(int level, DifficultyLevel expected)
    {
        var s = await SceneAsync(level);
        Assert.Equal(expected, (await _k.RecommendationsAsync(s.X)).TargetDifficulty);
    }

    // ---- Dịch vụ AI ----

    [Fact]
    public async Task When_the_ai_service_fails_the_server_still_fills_all_five_sections_from_the_same_province_or_type()
    {
        var s = await SceneAsync();
        await _k.AllWrongAsync(s.QuizA, s.X);
        _factory.RecommendationAi.Behavior = (_, _) => throw new HttpRequestException("Dịch vụ AI ngừng");
        var recs = await _k.RecommendationsAsync(s.X);

        Assert.True(recs.RelatedFromFallback);
        Assert.Single(recs.HeritagesToReview);
        Assert.Single(recs.LessonsToReview);
        Assert.Equal(2, recs.NextQuizzes.Count);
        Assert.Equal(2, recs.NewLessons.Count);
        Assert.Equal(new[] { s.B.Heritage.Id, s.HeritageD }.Order(), recs.RelatedHeritages.Take(2).Select(h => h.HeritageId).Order());
        Assert.Equal(s.B.LessonId, recs.NewLessons[0].LessonId);   // bài B vẫn đứng trước nhờ cùng tỉnh, cùng loại
    }

    [Fact]
    public async Task Only_numeric_ids_levels_and_heritage_attributes_go_to_the_ai_service_never_names_or_codes()
    {
        var s = await SceneAsync();
        await _k.AllWrongAsync(s.QuizA, s.X);
        _factory.RecommendationAi.Reset();
        await _k.RecommendationsAsync(s.X);

        var request = _factory.RecommendationAi.LastRequest!;
        Assert.Equal(1, _factory.RecommendationAi.Calls);
        Assert.Equal((2, DifficultyLevel.EASY), (request.Level, request.TargetDifficulty));
        Assert.NotNull(request.StudentId);
        Assert.Equal(s.A.Heritage.Id, Assert.Single(request.References).Heritage.Id);
        Assert.Equal(2d, Assert.Single(request.References).Weight);                        // di sản em còn yếu nặng hơn
        Assert.DoesNotContain(request.Candidates, c => c.Id == s.A.Heritage.Id);       // không gợi ý lại di sản đã học

        var json = JsonSerializer.Serialize(request);
        Assert.DoesNotContain("Em X", json);
        Assert.DoesNotContain(s.QuizA.StudentCode, json);
        Assert.DoesNotContain("Lớp thử", json);
    }

    [Fact]
    public async Task Suggestions_do_not_use_the_ai_service_at_all_when_the_student_has_nothing_to_compare()
    {
        var s = await SceneAsync();
        _factory.RecommendationAi.Reset();
        await _k.RecommendationsAsync(s.X);
        Assert.Equal(0, _factory.RecommendationAi.Calls);
    }

    // ---- Quyền ----

    [Fact]
    public async Task Only_a_signed_in_student_reads_the_suggestions_and_never_the_attempt_of_another_student()
    {
        var s = await SceneAsync();
        var result = await _k.AllWrongAsync(s.QuizA, s.X);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.RecommendationsRawAsync("")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _k.RecommendationsRawAsync(s.A.Token)).Response.StatusCode);              // giáo viên
        Assert.Equal(HttpStatusCode.Forbidden, (await _k.RecommendationsRawAsync(s.A.School.OrgAdminToken)).Response.StatusCode);   // quản trị trường

        // Em khác cùng lớp: lượt của em X như không tồn tại.
        Assert.Equal(HttpStatusCode.NotFound, (await _k.AttemptRecommendationsRawAsync(s.QuizB.StudentToken, result.AttemptId)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.AttemptRecommendationsRawAsync(s.X, result.AttemptId + 999)).Response.StatusCode);
    }

    [Fact]
    public async Task The_suggestions_of_an_attempt_still_in_progress_are_refused()
    {
        var s = await SceneAsync();
        var attempt = await _k.Attempts.StartAsync(s.X, s.QuizA.Id);
        var (res, body) = await _k.AttemptRecommendationsRawAsync(s.X, attempt.Id);
        Assert.Equal((HttpStatusCode.Conflict, MessageCodes.AttemptInProgress), (res.StatusCode, body.ErrorCode));
    }
}
