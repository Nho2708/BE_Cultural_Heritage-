using System.Net;
using System.Text.Json;
using DSVHVN.Application.Common;
using DSVHVN.Application.Recommendations;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Gợi ý ôn tập cho lớp: di sản nên ôn (tỉ lệ sai từ 40% và ít nhất 10 câu trả lời, tối đa 5, kèm lý do), di sản liên quan (tối đa 5), lọc theo bài kiểm tra và
/// khoảng thời gian nộp, chỉ di sản đã xuất bản, dự phòng khi Dịch vụ AI lỗi và không gửi dữ liệu học sinh sang Dịch vụ AI.
/// </summary>
public sealed class TeacherRecommendationApiTests : IClassFixture<ApiFactory>
{
    private readonly StatsTestKit _k;
    private readonly ApiFactory _factory;

    public TeacherRecommendationApiTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.RecommendationAi.Reset();
        _k = new StatsTestKit(factory);
    }

    /// <summary>
    /// Lớp cấp 2 có ba bài về ba di sản, mỗi bài một quiz bốn câu, và ba học sinh được ghi lượt thẳng vào CSDL:
    /// QA (di sản A): Em 1 sai 2, Em 2 sai 2, Em 3 sai 1 → 5 sai trên 12 câu (42%) nên nên ôn;
    /// QB (di sản B): sai 2, 1, 1 → 4 trên 12 (33%) nên chưa tới ngưỡng;
    /// QC (di sản C): chỉ Em 1 và Em 2 làm, sai hết → 8 câu, chưa đủ 10 câu trả lời.
    /// D là di sản chưa có bài, giống A (cùng tỉnh, cùng loại).
    /// </summary>
    private sealed record Scene(
        QuizTestKit.Setup A, QuizTestKit.Setup B, QuizTestKit.Setup C, AttemptTestKit.Quiz QA, AttemptTestKit.Quiz QB, AttemptTestKit.Quiz QC,
        long HeritageD, long Em1, long Em2, long Em3)
    {
        public string Token => A.Token;
        public long ClassId => A.ClassId;
    }

    private async Task<Scene> SceneAsync(DateTime? oldSubmittedAt = null)
    {
        var a = await _k.Quizzes.SetupAsync(2);
        var province = StatsTestKit.NewProvinceCode();
        var word = "nhom" + Guid.NewGuid().ToString("N")[..6];
        await _k.SetHeritageProvinceAsync(a.Heritage.Id, province);
        await _k.RenameHeritageAsync(a.Heritage.Id, $"Hoàng thành Phượng {word}");
        var b = await _k.LessonOnNewHeritageAsync(a, "Văn Miếu", StatsTestKit.NewProvinceCode(), HeritageType.INTANGIBLE, level: RecognitionLevel.PROVINCIAL, sameEra: false);
        var c = await _k.LessonOnNewHeritageAsync(a, "Chùa Một Cột", StatsTestKit.NewProvinceCode(), HeritageType.NATURAL, level: RecognitionLevel.PROVINCIAL, sameEra: false);
        var qa = await _k.QuizAsync(a, "Em 1");
        var qb = await _k.QuizAsync(b, "Em 2");
        var qc = await _k.QuizAsync(c, "Em 3");
        var d = await _k.LooseHeritageAsync($"Hoàng thành Hà Nội {word}", province, HeritageType.TANGIBLE);

        var em1 = await _k.StudentIdAsync(a.ClassId, "Em 1");
        var em2 = await _k.StudentIdAsync(a.ClassId, "Em 2");
        var em3 = await _k.StudentIdAsync(a.ClassId, "Em 3");
        await _k.SeedAttemptAsync(qa.Id, em1, i => i >= 2, submittedAt: oldSubmittedAt);
        await _k.SeedAttemptAsync(qa.Id, em2, i => i >= 2);
        await _k.SeedAttemptAsync(qa.Id, em3, i => i != 0);
        await _k.SeedAttemptAsync(qb.Id, em1, i => i >= 2);
        await _k.SeedAttemptAsync(qb.Id, em2, i => i != 0);
        await _k.SeedAttemptAsync(qb.Id, em3, i => i != 0);
        await _k.SeedAttemptAsync(qc.Id, em1, _ => false);
        await _k.SeedAttemptAsync(qc.Id, em2, _ => false);
        return new Scene(a, b, c, qa, qb, qc, d, em1, em2, em3);
    }

    private static string Query(DateTime? from = null, DateTime? to = null, long? quizId = null)
    {
        var parts = new List<string>();
        if (quizId is not null) parts.Add($"quizId={quizId}");
        if (from is not null) parts.Add("from=" + Uri.EscapeDataString(from.Value.ToString("O")));
        if (to is not null) parts.Add("to=" + Uri.EscapeDataString(to.Value.ToString("O")));
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    [Fact]
    public async Task Only_a_heritage_with_at_least_forty_percent_wrong_and_ten_answers_is_suggested_to_review_with_a_reason_naming_the_percent_and_the_count()
    {
        var s = await SceneAsync();
        var recs = await _k.ClassRecommendationsAsync(s.Token, s.ClassId);

        Assert.Equal((s.ClassId, 2, (long?)null, 8, 32), (recs.ClassId, recs.Level, recs.QuizId, recs.SubmittedAttempts, recs.AnsweredQuestions));
        var review = Assert.Single(recs.HeritagesToReview);
        Assert.Equal((s.A.Heritage.Id, 12, 5, 41.7m), (review.HeritageId, review.Answered, review.Wrong, review.WrongPercent));
        Assert.Equal("Lớp sai 42% ở 12 câu về di sản này", review.Reason);
        Assert.NotNull(review.Slug);
        Assert.Equal([s.A.Heritage.Id], recs.SuggestedHeritageIds);
        Assert.Null(recs.Message);
        Assert.False(recs.RelatedFromFallback);
    }

    [Fact]
    public async Task The_related_heritages_are_the_closest_to_the_heritages_given_to_the_class_never_one_already_given_and_at_most_five()
    {
        var s = await SceneAsync();
        var recs = await _k.ClassRecommendationsAsync(s.Token, s.ClassId);

        Assert.InRange(recs.RelatedHeritages.Count, 1, RecommendationRules.ClassSectionLimit);
        Assert.Equal(s.HeritageD, recs.RelatedHeritages[0].HeritageId);   // cùng tỉnh, cùng loại với di sản nên ôn A
        Assert.Contains("Hoàng thành Phượng", recs.RelatedHeritages[0].Reason);
        var given = new[] { s.A.Heritage.Id, s.B.Heritage.Id, s.C.Heritage.Id };
        Assert.DoesNotContain(recs.RelatedHeritages, h => given.Contains(h.HeritageId));
    }

    [Fact]
    public async Task Filtering_by_quiz_changes_the_answers_that_are_counted()
    {
        var s = await SceneAsync();

        var onlyA = await _k.ClassRecommendationsAsync(s.Token, s.ClassId, Query(quizId: s.QA.Id));
        Assert.Equal((s.QA.Id, 3, 12), (onlyA.QuizId, onlyA.SubmittedAttempts, onlyA.AnsweredQuestions));
        Assert.Equal([s.A.Heritage.Id], onlyA.HeritagesToReview.Select(r => r.HeritageId));

        var onlyB = await _k.ClassRecommendationsAsync(s.Token, s.ClassId, Query(quizId: s.QB.Id));
        Assert.Empty(onlyB.HeritagesToReview);
        Assert.Empty(onlyB.SuggestedHeritageIds);
        Assert.Null(onlyB.Message);   // có lượt nộp nên không phải lời báo "chưa có kết quả"

        // Cùng kết quả khi mở từ tab Gợi ý của bài kiểm tra.
        var fromQuiz = await _k.QuizRecommendationsAsync(s.Token, s.QA.Id);
        Assert.Equal(onlyA.HeritagesToReview.Select(r => r.Reason), fromQuiz.HeritagesToReview.Select(r => r.Reason));
        Assert.Equal((s.ClassId, s.QA.Id), (fromQuiz.ClassId, fromQuiz.QuizId));
    }

    [Fact]
    public async Task A_single_quiz_suggests_its_wrong_heritage_even_with_fewer_than_ten_answers()
    {
        var s = await SceneAsync();

        // Di sản C sai hết trên tám câu: cả lớp chưa đủ mười câu nên chưa gợi ý, xem riêng bài của di sản đó thì gợi ý.
        Assert.DoesNotContain(s.C.Heritage.Id, (await _k.ClassRecommendationsAsync(s.Token, s.ClassId, Query())).HeritagesToReview.Select(r => r.HeritageId));

        var fromQuiz = await _k.QuizRecommendationsAsync(s.Token, s.QC.Id);

        Assert.Equal([s.C.Heritage.Id], fromQuiz.HeritagesToReview.Select(r => r.HeritageId));
    }

    [Fact]
    public async Task Filtering_by_the_submission_time_leaves_only_the_attempts_in_the_period()
    {
        var s = await SceneAsync(oldSubmittedAt: DateTime.UtcNow.AddDays(-10));

        // Bỏ lượt cũ của Em 1 (10 ngày trước): còn 8 câu của QA, chưa đủ 10 câu nên không còn nên ôn.
        var recent = await _k.ClassRecommendationsAsync(s.Token, s.ClassId, Query(from: DateTime.UtcNow.AddDays(-2), quizId: s.QA.Id));
        Assert.Equal((2, 8), (recent.SubmittedAttempts, recent.AnsweredQuestions));
        Assert.Empty(recent.HeritagesToReview);

        var all = await _k.ClassRecommendationsAsync(s.Token, s.ClassId, Query(to: DateTime.UtcNow.AddDays(1), quizId: s.QA.Id));
        Assert.Equal(12, all.AnsweredQuestions);
        Assert.Single(all.HeritagesToReview);

        var old = await _k.ClassRecommendationsAsync(s.Token, s.ClassId, Query(to: DateTime.UtcNow.AddDays(-2)));
        Assert.Equal((1, 4), (old.SubmittedAttempts, old.AnsweredQuestions));

        var (res, body) = await _k.TeacherGetAsync<ClassRecommendationsDto>(s.Token,
            $"/classes/{s.ClassId}/recommendations{Query(from: DateTime.UtcNow, to: DateTime.UtcNow.AddDays(-1))}");
        Assert.Equal((HttpStatusCode.BadRequest, MessageCodes.Invalid), (res.StatusCode, body.ErrorCode));
    }

    [Fact]
    public async Task A_deactivated_student_no_longer_counts_toward_the_class_suggestions()
    {
        var s = await SceneAsync();
        await _k.SetStudentActiveAsync(s.Em1, false);
        var recs = await _k.ClassRecommendationsAsync(s.Token, s.ClassId, Query(quizId: s.QA.Id));
        Assert.Equal((2, 8), (recs.SubmittedAttempts, recs.AnsweredQuestions));
        Assert.Empty(recs.HeritagesToReview);   // còn 8 câu: chưa đủ 10
    }

    [Fact]
    public async Task A_class_with_no_submitted_attempt_gets_only_the_message()
    {
        var s = await _k.Quizzes.SetupAsync(2);
        var recs = await _k.ClassRecommendationsAsync(s.Token, s.ClassId);
        Assert.Equal((0, 0), (recs.SubmittedAttempts, recs.AnsweredQuestions));
        Assert.Empty(recs.HeritagesToReview);
        Assert.Empty(recs.RelatedHeritages);
        Assert.Equal(Messages.ClassNoResultsYet, recs.Message);
    }

    [Fact]
    public async Task A_heritage_that_is_no_longer_published_is_not_suggested_for_review_or_as_related()
    {
        var s = await SceneAsync();
        await _k.Quizzes.Questions.Lesson.ArchiveHeritageAsync(s.A.Heritage.Id);
        await _k.Quizzes.Questions.Lesson.ArchiveHeritageAsync(s.HeritageD);
        var recs = await _k.ClassRecommendationsAsync(s.Token, s.ClassId);

        Assert.Empty(recs.HeritagesToReview);
        Assert.DoesNotContain(recs.RelatedHeritages, h => h.HeritageId == s.HeritageD);
        Assert.DoesNotContain(recs.RelatedHeritages, h => h.HeritageId == s.A.Heritage.Id);
    }

    [Fact]
    public async Task When_the_ai_service_fails_the_review_part_is_unchanged_and_the_related_part_falls_back_to_the_same_province_or_type()
    {
        var s = await SceneAsync();
        _factory.RecommendationAi.Behavior = (_, _) => throw new TimeoutException("Dịch vụ AI quá giờ");
        var recs = await _k.ClassRecommendationsAsync(s.Token, s.ClassId);

        Assert.True(recs.RelatedFromFallback);
        Assert.Equal([s.A.Heritage.Id], recs.HeritagesToReview.Select(r => r.HeritageId));
        Assert.Equal(s.HeritageD, recs.RelatedHeritages[0].HeritageId);
    }

    [Fact]
    public async Task Nothing_about_students_goes_to_the_ai_service_only_heritage_attributes_with_the_heritages_to_review_weighted_higher()
    {
        var s = await SceneAsync();
        _factory.RecommendationAi.Reset();
        await _k.ClassRecommendationsAsync(s.Token, s.ClassId);

        var request = _factory.RecommendationAi.LastRequest!;
        Assert.Equal(1, _factory.RecommendationAi.Calls);
        Assert.Null(request.StudentId);
        Assert.Null(request.Level);
        Assert.Equal(new[] { s.A.Heritage.Id, s.B.Heritage.Id, s.C.Heritage.Id }.Order(), request.References.Select(r => r.Heritage.Id).Order());
        Assert.Equal(2d, request.References.Single(r => r.Heritage.Id == s.A.Heritage.Id).Weight);
        Assert.All(request.References.Where(r => r.Heritage.Id != s.A.Heritage.Id), r => Assert.Equal(1d, r.Weight));
        var json = JsonSerializer.Serialize(request);
        foreach (var name in new[] { "Em 1", "Em 2", "Em 3", "Lớp thử" }) Assert.DoesNotContain(name, json);
    }

    [Fact]
    public async Task Another_teacher_the_school_admin_students_and_a_quiz_of_another_class_are_refused()
    {
        var s = await SceneAsync();
        var (_, otherTeacher) = await _k.Quizzes.Questions.Lesson.Classroom.AnotherTeacherAsync(s.A.School);
        var path = $"{StatsTestKit.Teacher}/classes/{s.ClassId}/recommendations";
        Assert.Equal(HttpStatusCode.OK, await _k.StatusOfAsync(path, s.Token));
        Assert.Equal(HttpStatusCode.NotFound, await _k.StatusOfAsync(path, otherTeacher));
        Assert.Equal(HttpStatusCode.Forbidden, await _k.StatusOfAsync(path, s.A.School.OrgAdminToken));
        Assert.Equal(HttpStatusCode.Forbidden, await _k.StatusOfAsync(path, s.QA.StudentToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await _k.StatusOfAsync(path, null));

        // Bài kiểm tra của một lớp khác (của chính giáo viên) không đi kèm lớp này.
        var secondClass = await _k.Quizzes.Questions.Lesson.Classroom.CreateClassAsync(s.Token, "9A2", 9);
        Assert.Equal(HttpStatusCode.NotFound, await _k.StatusOfAsync($"{StatsTestKit.Teacher}/classes/{secondClass.Id}/recommendations?quizId={s.QA.Id}", s.Token));
        Assert.Equal(HttpStatusCode.NotFound, await _k.StatusOfAsync($"{path}?quizId={s.QA.Id + 9999}", s.Token));
        Assert.Equal(HttpStatusCode.NotFound, await _k.StatusOfAsync($"{StatsTestKit.Teacher}/quizzes/{s.QA.Id}/results/recommendations", otherTeacher));
    }
}
