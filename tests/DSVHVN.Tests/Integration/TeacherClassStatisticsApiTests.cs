using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.Statistics;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Thống kê của một lớp cho giáo viên: tiến độ (bài kiểm tra đã phát hành, bảng học sinh, ô học sinh × bài kiểm tra, bài học đã hoàn thành, huy hiệu, chưa đăng nhập),
/// mức thành thạo (theo di sản, tỉnh, loại di sản, dạng câu, bảng nhiệt, lọc theo tỉnh và loại) và hồ sơ học tập của từng học sinh; chỉ lượt đã nộp của học sinh đang
/// hoạt động; giáo viên luôn thấy điểm; lớp của giáo viên khác như không tồn tại.
/// </summary>
public sealed class TeacherClassStatisticsApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly StatsTestKit _k = new(factory);

    /// <summary>
    /// Lớp cấp 2 có hai bài: A (Hoàng thành, một tỉnh riêng, vật thể) với quiz QA và B (Ca trù, tỉnh riêng khác, phi vật thể) với quiz QB. Bốn học sinh đang hoạt động:
    /// Em 1 đúng hết QA và sai hết QB; Em 2 sai hết QA; Em 3 chưa làm; Em 5 chưa đăng nhập lần nào.
    /// </summary>
    private sealed record Scene(
        QuizTestKit.Setup A, QuizTestKit.Setup B, AttemptTestKit.Quiz QA, AttemptTestKit.Quiz QB, string Em1, string Em2, string Em3,
        long Em1Id, long Em2Id, long Em3Id, long Em5Id, int ProvinceA, int ProvinceB, long AttemptQA, long AttemptQB)
    {
        public string Token => A.Token;
        public long ClassId => A.ClassId;
    }

    private async Task<Scene> SceneAsync(int level = 2)
    {
        var a = await _k.Quizzes.SetupAsync(level);
        var codeA = StatsTestKit.NewProvinceCode();
        await _k.SetHeritageProvinceAsync(a.Heritage.Id, codeA);
        var b = await _k.LessonOnNewHeritageAsync(a, "Ca trù", StatsTestKit.NewProvinceCode(), HeritageType.INTANGIBLE);
        var provinceA = await _k.Quizzes.Questions.Lesson.Discovery.ProvinceIdAsync(codeA);
        var provinceB = await _k.Factory.WithDbAsync(db => Task.FromResult(db.Heritages.Where(h => h.Id == b.Heritage.Id).Select(h => h.ProvinceId!.Value).Single()));

        var qa = await _k.QuizAsync(a, "Em 1");
        var qb = await _k.QuizAsync(b, "Em 4");
        var em2 = await _k.StudentTokenAsync(a, "Em 2");
        var em3 = await _k.StudentTokenAsync(a, "Em 3");
        await _k.Quizzes.Questions.Lesson.Classroom.AddStudentAsync(a.Token, a.ClassId, "Em 5");

        var attemptQa = (await _k.AllCorrectAsync(qa)).AttemptId;
        var attemptQb = (await _k.AllWrongAsync(qb, qa.StudentToken)).AttemptId;
        await _k.AllWrongAsync(qa, em2);

        var ids = new Dictionary<string, long>();
        foreach (var name in new[] { "Em 1", "Em 2", "Em 3", "Em 5" }) ids[name] = await _k.StudentIdAsync(a.ClassId, name);
        await _k.SetLastLoginAsync(ids["Em 5"], null);
        return new Scene(a, b, qa, qb, qa.StudentToken, em2, em3, ids["Em 1"], ids["Em 2"], ids["Em 3"], ids["Em 5"], provinceA, provinceB, attemptQa, attemptQb);
    }

    // ---- Tiến độ ----

    [Fact]
    public async Task The_progress_tab_gives_the_class_progress_the_published_quizzes_and_one_row_per_student()
    {
        var s = await SceneAsync();
        var progress = await _k.ProgressAsync(s.Token, s.ClassId);

        // Em 4 là học sinh sinh ra khi dựng QB; lớp có 5 học sinh đang hoạt động: Em 1, 2, 3, 4, 5.
        Assert.Equal((s.ClassId, 8, 2, "Cấp 2", 5, 1), (progress.ClassId, progress.Grade, progress.Level, progress.LevelName, progress.Enrolled, progress.NeverLoggedIn));
        Assert.Equal((2, 3), (progress.QuizzesPublished, progress.SubmittedTotal));
        Assert.Equal(30m, progress.ProgressPercent);   // 3 lượt ÷ (5 học sinh × 2 bài kiểm tra)
        Assert.Null(progress.Message);

        var quizzes = progress.Quizzes.ToDictionary(q => q.QuizId);
        Assert.Equal((2, 5, 40m, QuizStatus.PUBLISHED), (quizzes[s.QA.Id].Submitted, quizzes[s.QA.Id].Enrolled, quizzes[s.QA.Id].RatePercent, quizzes[s.QA.Id].Status));
        Assert.Equal((1, 20m), (quizzes[s.QB.Id].Submitted, quizzes[s.QB.Id].RatePercent));

        var rows = progress.Students.ToDictionary(r => r.StudentId);
        var em1 = rows[s.Em1Id];
        Assert.Equal((2, 2, 2, 2, 50m), (em1.QuizzesSubmitted, em1.QuizzesPublished, em1.LessonsCompleted, em1.LessonsWithQuizzes, em1.AveragePercent));
        Assert.Null(em1.TotalStars);   // lớp cấp 2 không có sao
        Assert.False(em1.NeverLoggedIn);
        Assert.Equal([(BadgeCode.PERFECT_SCORE, 1), (BadgeCode.ON_TIME, 2)], em1.Badges.Select(b => (b.Code, b.Count)));

        var em2 = rows[s.Em2Id];
        Assert.Equal((1, 2, 1, 2, 0m), (em2.QuizzesSubmitted, em2.QuizzesPublished, em2.LessonsCompleted, em2.LessonsWithQuizzes, em2.AveragePercent));
        Assert.Equal([(BadgeCode.ON_TIME, 1)], em2.Badges.Select(b => (b.Code, b.Count)));

        var em3 = rows[s.Em3Id];
        Assert.Equal((0, 0), (em3.QuizzesSubmitted, em3.LessonsCompleted));
        Assert.Null(em3.AveragePercent);
        Assert.Empty(em3.Badges);

        var em5 = rows[s.Em5Id];
        Assert.True(em5.NeverLoggedIn);
        Assert.Null(em5.LastLoginAt);

        // Ô học sinh × bài kiểm tra: chỉ ba ô có lượt; ô thiếu là chưa làm.
        Assert.Equal(3, progress.Cells.Count);
        var cell = progress.Cells.Single(c => c.StudentId == s.Em1Id && c.QuizId == s.QA.Id);
        Assert.Equal((s.AttemptQA, QuizStudentStatus.SUBMITTED, 8m, 8m, 100m), (cell.AttemptId, cell.Status, cell.Score, cell.TotalPoint, cell.Percent));
        Assert.Equal(0m, progress.Cells.Single(c => c.StudentId == s.Em1Id && c.QuizId == s.QB.Id).Score);
    }

    [Fact]
    public async Task Draft_not_yet_open_deleted_and_archived_lesson_quizzes_are_not_counted_but_a_closed_quiz_is()
    {
        var s = await SceneAsync();
        var draft = await _k.Quizzes.ReadyQuizAsync(s.A, "Nháp");
        var future = await _k.QuizAsync(s.A, "Em 6");
        await _k.Quizzes.SetQuizTimesAsync(future.Id, DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddHours(5));
        var deleted = await _k.QuizAsync(s.A, "Em 7");
        await _k.SoftDeleteQuizAsync(deleted.Id);
        Assert.Equal(2, (await _k.ProgressAsync(s.Token, s.ClassId)).QuizzesPublished);
        Assert.DoesNotContain((await _k.ProgressAsync(s.Token, s.ClassId)).Quizzes, q => q.QuizId == draft.Id);

        await _k.Quizzes.CloseAsync(s.Token, s.QB.Id);
        var closed = await _k.ProgressAsync(s.Token, s.ClassId);
        Assert.Equal(2, closed.QuizzesPublished);
        Assert.Equal(QuizStatus.CLOSED, closed.Quizzes.Single(q => q.QuizId == s.QB.Id).Status);

        await _k.SetLessonStatusAsync(s.B.LessonId, ContentStatus.ARCHIVED);
        var archived = await _k.ProgressAsync(s.Token, s.ClassId);
        Assert.Equal(1, archived.QuizzesPublished);       // bài học đã lưu trữ không còn trong tiến độ
        Assert.Equal(s.QA.Id, Assert.Single(archived.Quizzes).QuizId);
    }

    [Fact]
    public async Task A_deactivated_student_leaves_the_progress_and_a_class_without_results_says_so()
    {
        var s = await SceneAsync();
        await _k.SetStudentActiveAsync(s.Em2Id, false);
        var progress = await _k.ProgressAsync(s.Token, s.ClassId);
        Assert.Equal((4, 2), (progress.Enrolled, progress.SubmittedTotal));
        Assert.DoesNotContain(progress.Students, r => r.StudentId == s.Em2Id);

        var empty = await _k.Quizzes.SetupAsync(2);
        var emptyProgress = await _k.ProgressAsync(empty.Token, empty.ClassId);
        Assert.Equal((0, 0, 0m), (emptyProgress.Enrolled, emptyProgress.QuizzesPublished, emptyProgress.ProgressPercent));
        Assert.Equal(Messages.ClassNoResultsYet, emptyProgress.Message);
    }

    [Fact]
    public async Task For_a_level_one_class_the_teacher_sees_the_stars_next_to_the_scores()
    {
        var s = await SceneAsync(level: 1);
        var progress = await _k.ProgressAsync(s.Token, s.ClassId);

        var em1 = progress.Students.Single(r => r.StudentId == s.Em1Id);
        Assert.Equal((3 + 1, 50m), (em1.TotalStars, em1.AveragePercent));      // QA 3 sao, QB 1 sao
        var cell = progress.Cells.Single(c => c.StudentId == s.Em1Id && c.QuizId == s.QA.Id);
        Assert.Equal((3, 8m, 100m), (cell.Stars, cell.Score, cell.Percent));
        Assert.Equal(1, progress.Cells.Single(c => c.StudentId == s.Em1Id && c.QuizId == s.QB.Id).Stars);
    }

    // ---- Mức thành thạo ----

    [Fact]
    public async Task The_mastery_tab_groups_by_heritage_province_heritage_type_and_question_type_with_a_heatmap()
    {
        var s = await SceneAsync();
        var mastery = await _k.MasteryAsync(s.Token, s.ClassId);

        // Di sản A: Em 1 đúng 4/4, Em 2 sai 0/4 → 8 câu, 4 đúng, 50%. Di sản B: Em 1 sai 0/4.
        Assert.Equal([s.B.Heritage.Id, s.A.Heritage.Id], mastery.ByHeritage.Select(r => r.Id!.Value));   // yếu nhất đứng đầu
        var heritageA = mastery.ByHeritage.Single(r => r.Id == s.A.Heritage.Id);
        Assert.Equal((8, 4, 50m, MasteryLevel.FAIR, "Đạt", 2), (heritageA.Answered, heritageA.Correct, heritageA.RatePercent, heritageA.Level, heritageA.LevelLabel, heritageA.Stars));
        var heritageB = mastery.ByHeritage.Single(r => r.Id == s.B.Heritage.Id);
        Assert.Equal((4, 0, 0m, MasteryLevel.NEEDS_REVIEW, "Cần ôn"), (heritageB.Answered, heritageB.Correct, heritageB.RatePercent, heritageB.Level, heritageB.LevelLabel));

        Assert.Equal([(s.ProvinceB, 0m), (s.ProvinceA, 50m)], mastery.ByProvince.Select(r => ((int)r.Id!.Value, r.RatePercent)));
        Assert.Equal([((long)HeritageType.INTANGIBLE, "Phi vật thể", 0m), ((long)HeritageType.TANGIBLE, "Vật thể", 50m)],
            mastery.ByHeritageType.Select(r => (r.Id!.Value, r.Name!, r.RatePercent)));

        Assert.Equal(4, mastery.ByQuestionType.Count);
        Assert.All(mastery.ByQuestionType, t => Assert.Equal((3, 1, 33.3m), (t.Answered, t.Correct, t.RatePercent)));

        Assert.Equal(5, mastery.HeatmapStudents.Count);
        Assert.Equal(3, mastery.HeatmapCells.Count);
        var best = mastery.HeatmapCells.Single(c => c.StudentId == s.Em1Id && c.HeritageId == s.A.Heritage.Id);
        Assert.Equal((4, 4, 100m, MasteryLevel.MASTERED), (best.Answered, best.Correct, best.RatePercent, best.Level));
        Assert.Null(mastery.Message);
    }

    [Fact]
    public async Task The_mastery_tab_filters_the_heritage_list_by_province_and_by_heritage_type()
    {
        var s = await SceneAsync();

        var byProvince = await _k.MasteryAsync(s.Token, s.ClassId, $"?provinceId={s.ProvinceA}");
        Assert.Equal([s.A.Heritage.Id], byProvince.ByHeritage.Select(r => r.Id!.Value));
        Assert.Equal(2, byProvince.ByProvince.Count);                                    // tổng hợp theo tỉnh không bị lọc
        Assert.All(byProvince.HeatmapCells, c => Assert.Equal(s.A.Heritage.Id, c.HeritageId));

        var byType = await _k.MasteryAsync(s.Token, s.ClassId, "?heritageType=INTANGIBLE");
        Assert.Equal([s.B.Heritage.Id], byType.ByHeritage.Select(r => r.Id!.Value));

        var none = await _k.MasteryAsync(s.Token, s.ClassId, $"?provinceId={s.ProvinceA}&heritageType=INTANGIBLE");
        Assert.Empty(none.ByHeritage);
        Assert.Empty(none.HeatmapCells);
        Assert.Null(none.Message);

        Assert.Equal(HttpStatusCode.BadRequest, await _k.StatusOfAsync($"{StatsTestKit.Teacher}/classes/{s.ClassId}/mastery?heritageType=KHONG_CO", s.Token));
    }

    [Fact]
    public async Task A_class_with_no_submitted_attempt_has_an_empty_mastery_tab_with_a_message()
    {
        var empty = await _k.Quizzes.SetupAsync(2);
        var mastery = await _k.MasteryAsync(empty.Token, empty.ClassId);
        Assert.Empty(mastery.ByHeritage);
        Assert.Empty(mastery.ByProvince);
        Assert.Equal(Messages.ClassNoResultsYet, mastery.Message);
    }

    // ---- Hồ sơ học tập ----

    [Fact]
    public async Task The_student_profile_has_the_history_the_score_trend_the_completed_lessons_the_mastery_and_the_badges()
    {
        var s = await SceneAsync();
        var profile = await _k.ProfileAsync(s.Token, s.ClassId, s.Em1Id);

        Assert.Equal(("Em 1", s.ClassId, 2, true, 2, 2, 2), (profile.FullName, profile.ClassId, profile.Level, profile.IsActive, profile.QuizzesSubmitted, profile.QuizzesPublished, profile.FinishedAttempts));
        Assert.Equal(50m, profile.AveragePercent);
        Assert.Null(profile.TotalStars);
        Assert.NotNull(profile.LastLoginAt);

        // Lịch sử: mới nhất trước; biểu đồ: cũ nhất trước.
        Assert.Equal([s.AttemptQB, s.AttemptQA], profile.History.Select(h => h.AttemptId));
        Assert.Equal([s.AttemptQA, s.AttemptQB], profile.ScoreTrend.Select(p => p.AttemptId));
        Assert.Equal([100m, 0m], profile.ScoreTrend.Select(p => p.Percent));
        var perfect = profile.History.Single(h => h.AttemptId == s.AttemptQA);
        Assert.Equal((8m, 8m, 100m, "Đã nộp"), (perfect.Score, perfect.TotalPoint, perfect.Percent, perfect.StatusLabel));
        Assert.Equal([BadgeCode.PERFECT_SCORE, BadgeCode.ON_TIME], perfect.Badges.Select(b => b.Code));
        Assert.Null(perfect.Stars);

        Assert.Equal(2, profile.CompletedLessons.Count);
        Assert.All(profile.CompletedLessons, l => Assert.Equal((1, 1), (l.QuizzesDone, l.QuizzesPublished)));
        Assert.Equal([(s.B.Heritage.Id, 0m), (s.A.Heritage.Id, 100m)], profile.MasteryByHeritage.OrderBy(m => m.RatePercent).Select(m => (m.Id!.Value, m.RatePercent)));
        Assert.Equal(2, profile.MasteryByProvince.Count);
        Assert.Equal(2, profile.MasteryByHeritageType.Count);
        Assert.Equal([(BadgeCode.PERFECT_SCORE, 1), (BadgeCode.ON_TIME, 2)], profile.Badges.Select(b => (b.Code, b.Count)));
        Assert.Null(profile.Message);
    }

    [Fact]
    public async Task A_student_who_has_not_submitted_anything_has_an_empty_profile_and_a_deactivated_student_can_still_be_viewed()
    {
        var s = await SceneAsync();
        var none = await _k.ProfileAsync(s.Token, s.ClassId, s.Em3Id);
        Assert.Empty(none.History);
        Assert.Empty(none.ScoreTrend);
        Assert.Empty(none.CompletedLessons);
        Assert.Null(none.AveragePercent);
        Assert.NotNull(none.Message);

        await _k.SetStudentActiveAsync(s.Em1Id, false);
        var inactive = await _k.ProfileAsync(s.Token, s.ClassId, s.Em1Id);
        Assert.False(inactive.IsActive);
        Assert.Equal(2, inactive.History.Count);
    }

    [Fact]
    public async Task For_a_level_one_class_the_profile_shows_stars_and_history_rows_carry_stars()
    {
        var s = await SceneAsync(level: 1);
        var profile = await _k.ProfileAsync(s.Token, s.ClassId, s.Em1Id);
        Assert.Equal(4, profile.TotalStars);
        Assert.Equal([1, 3], profile.History.Select(h => h.Stars));   // QB mới nhất (1 sao), QA (3 sao)
    }

    // ---- Quyền ----

    [Fact]
    public async Task Other_teachers_the_school_admin_and_students_cannot_read_the_class_statistics_and_other_classes_are_not_found()
    {
        var s = await SceneAsync();
        var (_, otherTeacher) = await _k.Quizzes.Questions.Lesson.Classroom.AnotherTeacherAsync(s.A.School);
        var paths = new[]
        {
            $"/classes/{s.ClassId}/progress", $"/classes/{s.ClassId}/mastery", $"/classes/{s.ClassId}/recommendations",
            $"/classes/{s.ClassId}/students/{s.Em1Id}/profile",
        };
        foreach (var path in paths)
        {
            Assert.Equal(HttpStatusCode.OK, await _k.StatusOfAsync(StatsTestKit.Teacher + path, s.Token));
            Assert.Equal(HttpStatusCode.NotFound, await _k.StatusOfAsync(StatsTestKit.Teacher + path, otherTeacher));
            Assert.Equal(HttpStatusCode.Forbidden, await _k.StatusOfAsync(StatsTestKit.Teacher + path, s.A.School.OrgAdminToken));
            Assert.Equal(HttpStatusCode.Forbidden, await _k.StatusOfAsync(StatsTestKit.Teacher + path, s.Em1));
            Assert.Equal(HttpStatusCode.Unauthorized, await _k.StatusOfAsync(StatsTestKit.Teacher + path, null));
        }

        // Lớp khác của chính giáo viên: học sinh của lớp này không thuộc lớp đó.
        var secondClass = await _k.Quizzes.Questions.Lesson.Classroom.CreateClassAsync(s.Token, "9A2", 9);
        Assert.Equal(HttpStatusCode.NotFound, await _k.StatusOfAsync($"{StatsTestKit.Teacher}/classes/{secondClass.Id}/students/{s.Em1Id}/profile", s.Token));
        Assert.Equal(HttpStatusCode.NotFound, await _k.StatusOfAsync($"{StatsTestKit.Teacher}/classes/{s.ClassId}/students/{s.Em1Id + 9999}/profile", s.Token));
        Assert.Equal(HttpStatusCode.NotFound, await _k.StatusOfAsync($"{StatsTestKit.Teacher}/classes/{s.ClassId + 9999}/progress", s.Token));
    }
}
