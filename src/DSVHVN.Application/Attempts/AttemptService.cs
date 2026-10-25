using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Questions;
using DSVHVN.Application.Speech;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Quizzes;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Attempts;

/// <summary>
/// Làm bài kiểm tra của học sinh (web học tập và chế độ Học sinh của app dùng chung một lượt, gắn với tài khoản).
/// <list type="bullet">
/// <item>Vào quiz từ trang bài học đã mở bằng mã, không có mã quiz và không nhập mã lần hai. Chỉ tạo lượt khi: em còn hoạt động, bài học của quiz
/// đang Đã xuất bản và thuộc lớp của em, quiz đang mở (đã tới giờ mở, chưa tới giờ đóng) và em chưa có lượt. Gói của trường hết hạn không chặn.</item>
/// <item>Mỗi em một lượt mỗi quiz: ràng buộc không trùng chặn cả hai yêu cầu bắt đầu gửi đồng thời; đã có lượt đang làm thì vào tiếp đúng lượt đó
/// (giờ vẫn tính từ lúc bắt đầu), đã nộp thì chỉ xem kết quả.</item>
/// <item>Mỗi thao tác trả lời lưu ngay (ghi đè cả câu), kèm chấm tạm; câu trả lời phải hợp lệ với đúng câu đó. Chỉ chủ lượt gửi được.</item>
/// <item>Cấp 1 là bài chơi dạng luyện tập: em bấm kiểm tra từng câu, lần kiểm đầu tiên được lưu làm câu trả lời để chấm (không ghi đè nữa), các lần sau
/// chỉ cho biết đúng sai để em thử lại.</item>
/// <item>Hạn nộp = lúc bắt đầu + thời gian làm, không muộn hơn giờ đóng (cấp 1 không có thời gian làm). Máy chủ nhận bài tới hạn nộp cộng 30 giây;
/// quá thì lượt được chốt thành hết giờ với các câu đã lưu.</item>
/// <item>Quiz bật xáo câu thì thứ tự câu cố định suốt lượt; các thẻ, cột, mục cần xáo luôn được xáo theo cách tất định từ mã lượt.</item>
/// </list>
/// </summary>
public sealed class AttemptService(IAppDbContext db, AttemptLifecycle lifecycle, AttemptResultService results, ISpeechPrefetcher speech)
{
    private DateTime Now => lifecycle.Now;

    /// <summary>Kết quả của lượt đã nộp (xem lại từ lịch sử hoặc trang bài học).</summary>
    public async Task<StudentAttemptResultDto> GetResultAsync(Actor actor, long attemptId, CancellationToken ct) =>
        await results.GetAsync(await StudentContext.LoadAsync(db, actor, ct), attemptId, ct);

    // ---- Trước khi làm ----

    /// <summary>
    /// Trang trước khi bắt đầu: tên, số câu, thời gian làm, giờ mở và đóng, em làm được gì lúc này. Quiz nháp, của lớp khác hay của bài học đã lưu trữ
    /// (khi em chưa có lượt) như không tồn tại.
    /// </summary>
    public async Task<StudentQuizIntroDto> GetIntroAsync(Actor actor, long quizId, CancellationToken ct)
    {
        var student = await StudentContext.LoadAsync(db, actor, ct);
        var quiz = await FindQuizAsync(student, quizId, ct) ?? throw NotFoundQuiz();
        var attempt = await db.Attempts.AsNoTracking().Where(a => a.QuizId == quizId && a.StudentId == student.Id)
            .Select(a => new { a.Id, a.Status }).SingleOrDefaultAsync(ct);
        if (quiz.LessonStatus != ContentStatus.PUBLISHED && attempt is null) throw NotFoundQuiz();

        var status = attempt?.Status;
        if (attempt is { Status: AttemptStatus.IN_PROGRESS } && await lifecycle.ExpireIfOverdueAsync(attempt.Id, student.Level, ct))
            status = AttemptStatus.EXPIRED;

        var now = Now;
        var state = StudentLessonService.StateOf(quiz.Status, quiz.OpenAt, quiz.CloseAt, status, now);
        var action = state switch
        {
            StudentQuizState.IN_PROGRESS => StudentQuizAction.CONTINUE,
            StudentQuizState.SUBMITTED => StudentQuizAction.VIEW_RESULT,
            StudentQuizState.NOT_OPEN => StudentQuizAction.NOT_OPEN,
            StudentQuizState.CLOSED => StudentQuizAction.CLOSED,
            _ => StudentQuizAction.START,
        };

        var limit = AttemptRules.EffectiveTimeLimit(student.Level, quiz.TimeLimitMinutes);
        var clock = AttemptRules.ShowsClock(student.Level, limit, AttemptRules.DeadlineOf(now, limit, quiz.CloseAt));
        return new StudentQuizIntroDto(
            quiz.Id, quiz.Name, quiz.Description, quiz.LessonId, quiz.LessonTitle, student.Level, student.LevelName,
            QuestionPreviewBuilder.LayoutOf(student.Level), quiz.QuestionCount, student.Level == 1 ? null : quiz.TotalPoint, limit, clock,
            quiz.OpenAt, quiz.CloseAt, state, StudentLessonService.LabelOf(state), action, ActionLabel(action), attempt?.Id, status,
            action == StudentQuizAction.START ? (student.Level == 1 ? Messages.QuizPracticeNotice : Messages.QuizOnlyOnceNotice) : null);
    }

    /// <summary>
    /// Bắt đầu làm bài. Đã có lượt đang làm thì trả đúng lượt đó (<c>Created</c> = false) mà không tạo lượt mới; đã nộp thì 409 để em xem kết quả;
    /// chưa có lượt thì chỉ tạo khi quiz đang mở (chưa tới giờ mở hoặc đã hết hạn là 422).
    /// </summary>
    public async Task<(StudentAttemptDto Attempt, bool Created)> StartAsync(Actor actor, long quizId, CancellationToken ct)
    {
        var student = await StudentContext.LoadAsync(db, actor, ct);

        var existing = await db.Attempts.AsNoTracking().Where(a => a.QuizId == quizId && a.StudentId == student.Id).Select(a => a.Id).SingleOrDefaultAsync(ct);
        if (existing != 0) return (await ViewAsync(student, existing, ct), false);

        var quiz = await FindQuizAsync(student, quizId, ct);
        if (quiz is null || quiz.LessonStatus != ContentStatus.PUBLISHED) throw NotFoundQuiz();

        var now = Now;
        switch (QuizRules.OpenStateOf(quiz.Status, quiz.OpenAt, quiz.CloseAt, now))
        {
            case QuizOpenState.NOT_OPEN: throw new AppException(422, Messages.QuizNotOpen(quiz.OpenAt));
            case QuizOpenState.ENDED: throw new AppException(422, Messages.QuizEnded);
            case QuizOpenState.NONE: throw NotFoundQuiz();
        }

        var attempt = new Attempt
        {
            QuizId = quizId,
            StudentId = student.Id,
            StartedAt = now,
            Status = AttemptStatus.IN_PROGRESS,
            TotalPoint = quiz.TotalPoint,
            TotalQuestions = quiz.QuestionCount,
        };
        db.Attempts.Add(attempt);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Hai yêu cầu bắt đầu gửi đồng thời: ràng buộc không trùng chỉ cho một lượt, yêu cầu còn lại vào tiếp lượt đó.
            db.Attempts.Remove(attempt);
            var winner = await db.Attempts.AsNoTracking().Where(a => a.QuizId == quizId && a.StudentId == student.Id).Select(a => a.Id).SingleOrDefaultAsync(ct);
            if (winner == 0) throw;
            return (await ViewAsync(student, winner, ct), false);
        }
        var view = await ViewAsync(student, attempt.Id, ct);
        WarmSpeech(view);
        return (view, true);
    }

    /// <summary>
    /// Lượt mới của bài chơi cấp 1 (đọc to): nhờ tạo trước tiếng lời đọc của mọi câu theo đúng thứ tự đang hiện cho em, vì ghép cặp và sắp xếp
    /// xáo khác nhau mỗi lượt nên không tạo sẵn được lúc thầy cô phát hành. Chỉ xếp vào hàng đợi nền, không chờ.
    /// </summary>
    private void WarmSpeech(StudentAttemptDto view)
    {
        if (view.ReadAloud && speech.Enabled) speech.Enqueue(SpeechWarmUpTexts.ForAttempt(view.Questions));
    }

    // ---- Trong lúc làm ----

    /// <summary>Lượt đang làm dở của em với các câu và câu trả lời đã lưu (vào tiếp sau khi mất kết nối, tải lại, đổi thiết bị).</summary>
    public async Task<StudentAttemptDto> GetAsync(Actor actor, long attemptId, CancellationToken ct)
    {
        var student = await StudentContext.LoadAsync(db, actor, ct);
        return await ViewAsync(student, attemptId, ct);
    }

    /// <summary>
    /// Lưu câu trả lời của một câu (ghi đè cả câu; gửi rỗng là xóa câu trả lời) và chấm tạm. Chỉ chủ lượt; lượt phải đang làm và chưa quá hạn nhận bài.
    /// Câu trả lời sai cấu trúc bị từ chối 400.
    /// </summary>
    public async Task<SavedAnswerDto> SaveAnswerAsync(Actor actor, long attemptId, long questionId, QuestionSubmission? answer, CancellationToken ct)
    {
        var student = await StudentContext.LoadAsync(db, actor, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await EnsureOwnedAsync(student, attemptId, ct);
        await db.LockAttemptAsync(attemptId, ct);

        var attempt = await db.Attempts.Include(a => a.Quiz).SingleAsync(a => a.Id == attemptId, ct);
        var now = Now;
        var deadline = AttemptFinisher.DeadlineOf(attempt, attempt.Quiz!, student.Level);
        if (attempt.Status != AttemptStatus.IN_PROGRESS) throw new AppException(409, Messages.AttemptFinished(attempt.Status == AttemptStatus.EXPIRED));
        if (AttemptRules.IsOverdue(deadline, now))
        {
            await FinishAsync(attempt, student.Level, AttemptStatus.EXPIRED, AttemptFinisher.SubmitTime(now, deadline), ct);
            await tx.CommitAsync(ct);
            throw new AppException(409, Messages.AttemptFinished(autoSubmitted: true));
        }

        var items = await AttemptFinisher.LoadQuizQuestionsAsync(db, attempt.QuizId, ct);
        var item = items.FirstOrDefault(x => x.QuestionId == questionId)
                   ?? throw AppException.NotFound(Messages.NotFound("lưu câu trả lời", "câu hỏi này trong bài kiểm tra của em"));
        var layout = QuestionLayout.Build(item.Question!, attempt.Id);
        var stored = AttemptAnswerCodec.ToStored(layout, answer);

        var row = await db.AttemptAnswers.Include(a => a.Parts).SingleOrDefaultAsync(a => a.AttemptId == attemptId && a.QuestionId == questionId, ct);
        if (student.Level == 1 && row is not null && AttemptRules.IsAnswerFixed(row.TryCount))
        {
            // Bài chơi cấp 1: câu đã kiểm thì lần trả lời đầu đã chốt để chấm; giữ nguyên và trả như đã lưu (bản app cũ vẫn chạy).
            var kept = await AttemptFinisher.LoadAnswersAsync(db, attemptId, ct);
            var keptCount = CountAnswered(items, kept, attempt.Id);
            await tx.CommitAsync(ct);
            return new SavedAnswerDto(questionId, AttemptAnswerCodec.ToStored(layout, row) is not null, row.AnsweredAt ?? now, keptCount, items.Count,
                AttemptViews.RemainingSeconds(deadline, now));
        }
        if (row is null)
        {
            row = new AttemptAnswer { AttemptId = attemptId, QuestionId = questionId };
            db.AttemptAnswers.Add(row);
        }
        foreach (var part in AttemptAnswerCodec.Apply(row, layout, stored, item.Point)) db.AttemptAnswerParts.Add(part);
        row.AnsweredAt = now;
        await db.SaveChangesAsync(ct);

        var answers = await AttemptFinisher.LoadAnswersAsync(db, attemptId, ct);
        var answered = CountAnswered(items, answers, attempt.Id);
        await tx.CommitAsync(ct);
        return new SavedAnswerDto(questionId, stored is not null, now, answered, items.Count, AttemptViews.RemainingSeconds(deadline, now));
    }

    /// <summary>
    /// Cấp 1: kiểm một câu ngay khi em trả lời (cùng các bước kiểm như lưu câu trả lời: chủ lượt, lượt đang làm, chưa quá hạn nhận bài, câu thuộc bài,
    /// đúng cấu trúc). Câu trả lời trống và ghép cặp chưa nối đủ mọi hình bị từ chối 400. Lần kiểm đầu tiên của câu (chưa có dòng hoặc chưa kiểm lần nào)
    /// lưu và chấm như lưu câu trả lời, đó là lần được chấm; các lần sau không đổi câu trả lời đã lưu, chỉ chấm câu gửi lên trong bộ nhớ và tăng số lần kiểm.
    /// Lớp không phải cấp 1 là 422.
    /// </summary>
    public async Task<AnswerCheckDto> CheckAnswerAsync(Actor actor, long attemptId, long questionId, QuestionSubmission? answer, CancellationToken ct)
    {
        var student = await StudentContext.LoadAsync(db, actor, ct);
        if (student.Level != 1) throw AppException.BusinessRule(Messages.PracticeOnlyForYoungStudents);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await EnsureOwnedAsync(student, attemptId, ct);
        await db.LockAttemptAsync(attemptId, ct);

        var attempt = await db.Attempts.Include(a => a.Quiz).SingleAsync(a => a.Id == attemptId, ct);
        var now = Now;
        var deadline = AttemptFinisher.DeadlineOf(attempt, attempt.Quiz!, student.Level);
        if (attempt.Status != AttemptStatus.IN_PROGRESS) throw new AppException(409, Messages.AttemptFinished(attempt.Status == AttemptStatus.EXPIRED));
        if (AttemptRules.IsOverdue(deadline, now))
        {
            await FinishAsync(attempt, student.Level, AttemptStatus.EXPIRED, AttemptFinisher.SubmitTime(now, deadline), ct);
            await tx.CommitAsync(ct);
            throw new AppException(409, Messages.AttemptFinished(autoSubmitted: true));
        }

        var items = await AttemptFinisher.LoadQuizQuestionsAsync(db, attempt.QuizId, ct);
        var item = items.FirstOrDefault(x => x.QuestionId == questionId)
                   ?? throw AppException.NotFound(Messages.NotFound("kiểm tra câu trả lời", "câu hỏi này trong bài kiểm tra của em"));
        var layout = QuestionLayout.Build(item.Question!, attempt.Id);
        var stored = AttemptAnswerCodec.ToStored(layout, answer);
        if (layout.Type == QuestionType.MATCHING)
        {
            if (stored?.Matches is not { } matches || matches.Any(m => m is null)) throw AppException.Validation("answer", Messages.MatchesIncomplete);
        }
        else if (stored is null)
        {
            throw AppException.Validation("answer", Messages.AnswerRequiredToCheck);
        }

        var row = await db.AttemptAnswers.Include(a => a.Parts).SingleOrDefaultAsync(a => a.AttemptId == attemptId && a.QuestionId == questionId, ct);
        if (row is null)
        {
            row = new AttemptAnswer { AttemptId = attemptId, QuestionId = questionId };
            db.AttemptAnswers.Add(row);
        }
        if (!AttemptRules.IsAnswerFixed(row.TryCount))
        {
            // Lần kiểm đầu tiên: lưu và chấm như lưu câu trả lời; đây là lần được chấm (sao và thống kê của giáo viên).
            foreach (var part in AttemptAnswerCodec.Apply(row, layout, stored, item.Point)) db.AttemptAnswerParts.Add(part);
            row.AnsweredAt = now;
        }
        row.TryCount = AttemptRules.NextTryCount(row.TryCount);
        await db.SaveChangesAsync(ct);

        var grade = QuestionGrader.Grade(layout.Type, layout.Rows, stored);
        var answers = await AttemptFinisher.LoadAnswersAsync(db, attemptId, ct);
        var answered = CountAnswered(items, answers, attempt.Id);
        await tx.CommitAsync(ct);
        return new AnswerCheckDto(
            questionId, grade.IsCorrect, row.IsCorrect, row.TryCount, ShownParts(layout, grade), AttemptViews.CorrectAnswer(layout),
            item.Question!.Explanation, answered, items.Count);
    }

    /// <summary>Đúng sai từng phần của một lần kiểm theo thứ tự đang hiện: một phần với dạng chọn, mỗi hình bên trái với ghép cặp, mỗi mục với sắp xếp.</summary>
    private static IReadOnlyList<bool> ShownParts(QuestionLayout l, GradeResult grade) => l.Type switch
    {
        QuestionType.MATCHING => l.LeftOrder.Select(stored => grade.Parts[stored]).ToList(),
        QuestionType.ORDERING => l.ItemOrder.Select(stored => grade.Parts[stored]).ToList(),
        _ => grade.Parts.ToList(),
    };

    // ---- Nộp bài ----

    /// <summary>
    /// Nộp bài: chấm cố định từng câu (bỏ trống ghi 0 điểm), tính điểm, số câu đúng, thời gian làm rồi trả kết quả theo cấp của lớp. Nộp trong hạn nộp
    /// cộng 30 giây ân hạn là đã nộp; quá thì chốt thành hết giờ. Nộp lần hai (bấm đúp) trả lại kết quả cũ, không chấm lại.
    /// </summary>
    public async Task<StudentAttemptResultDto> SubmitAsync(Actor actor, long attemptId, CancellationToken ct)
    {
        var student = await StudentContext.LoadAsync(db, actor, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await EnsureOwnedAsync(student, attemptId, ct);
        await db.LockAttemptAsync(attemptId, ct);

        var attempt = await db.Attempts.Include(a => a.Quiz).SingleAsync(a => a.Id == attemptId, ct);
        if (attempt.Status == AttemptStatus.IN_PROGRESS)
        {
            var now = Now;
            var deadline = AttemptFinisher.DeadlineOf(attempt, attempt.Quiz!, student.Level);
            var status = AttemptRules.IsOverdue(deadline, now) ? AttemptStatus.EXPIRED : AttemptStatus.SUBMITTED;
            await FinishAsync(attempt, student.Level, status, AttemptFinisher.SubmitTime(now, deadline), ct);
        }
        await tx.CommitAsync(ct);
        return await results.GetAsync(student, attemptId, ct);
    }

    // ---- Nội bộ ----

    private sealed record QuizInfo(
        long Id, string? Name, string? Description, long LessonId, string? LessonTitle, ContentStatus? LessonStatus, QuizStatus? Status,
        DateTime? OpenAt, DateTime? CloseAt, int? TimeLimitMinutes, decimal? TotalPoint, int QuestionCount);

    /// <summary>Quiz đã phát hành (hoặc đã đóng) của một bài học chưa xóa của lớp em.</summary>
    private async Task<QuizInfo?> FindQuizAsync(StudentContext student, long quizId, CancellationToken ct) =>
        await db.Quizzes.AsNoTracking()
            .Where(q => q.Id == quizId && q.DeletedAt == null && (q.Status == QuizStatus.PUBLISHED || q.Status == QuizStatus.CLOSED)
                        && q.Lesson!.DeletedAt == null && q.Lesson.ClassId == student.ClassId)
            .Select(q => new QuizInfo(q.Id, q.Name, q.Description, q.LessonId, q.Lesson!.Title, q.Lesson.Status, q.Status, q.OpenAt, q.CloseAt,
                q.TimeLimitMinutes, q.TotalPoint, q.Questions.Count))
            .SingleOrDefaultAsync(ct);

    private async Task EnsureOwnedAsync(StudentContext student, long attemptId, CancellationToken ct)
    {
        if (!await db.Attempts.AnyAsync(a => a.Id == attemptId && a.StudentId == student.Id, ct))
            throw AppException.NotFound(Messages.NotFound("mở bài làm", "bài làm của em"));
    }

    private async Task FinishAsync(Attempt attempt, int level, AttemptStatus status, DateTime submittedAt, CancellationToken ct)
    {
        var items = await AttemptFinisher.LoadQuizQuestionsAsync(db, attempt.QuizId, ct);
        var answers = await AttemptFinisher.LoadAnswersAsync(db, attempt.Id, ct);
        AttemptFinisher.Finish(db, attempt.Quiz!, items, attempt, answers, status, submittedAt, level);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Dựng lượt đang làm. Lượt đã nộp hoặc vừa hết hạn thì 409 để giao diện chuyển sang xem kết quả.</summary>
    private async Task<StudentAttemptDto> ViewAsync(StudentContext student, long attemptId, CancellationToken ct)
    {
        var owned = await db.Attempts.AsNoTracking().Where(a => a.Id == attemptId && a.StudentId == student.Id).Select(a => a.Status).SingleOrDefaultAsync(ct);
        if (owned is null) throw AppException.NotFound(Messages.NotFound("mở bài làm", "bài làm của em"));
        if (owned == AttemptStatus.IN_PROGRESS && await lifecycle.ExpireIfOverdueAsync(attemptId, student.Level, ct))
            throw new AppException(409, Messages.AttemptFinished(autoSubmitted: true));
        if (owned != AttemptStatus.IN_PROGRESS) throw new AppException(409, Messages.AttemptFinished(owned == AttemptStatus.EXPIRED));

        var attempt = await db.Attempts.AsNoTracking().Include(a => a.Quiz).ThenInclude(q => q!.Lesson)
            .SingleAsync(a => a.Id == attemptId, ct);
        var quiz = attempt.Quiz!;
        var items = await AttemptFinisher.LoadQuizQuestionsAsync(db, quiz.Id, ct);
        var answers = await AttemptFinisher.LoadAnswersAsync(db, attemptId, ct);

        var level = student.Level;
        var now = Now;
        var deadline = AttemptFinisher.DeadlineOf(attempt, quiz, level);
        var limit = AttemptRules.EffectiveTimeLimit(level, quiz.TimeLimitMinutes);

        var ordered = QuestionLayout.OrderQuestions(items, quiz.ShuffleQuestions, attempt.Id);
        var questions = new List<StudentAttemptQuestionDto>();
        var answered = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            var layout = QuestionLayout.Build(item.Question!, attempt.Id);
            var row = answers.FirstOrDefault(a => a.QuestionId == item.QuestionId);
            var shown = AttemptAnswerCodec.ToShown(layout, AttemptAnswerCodec.ToStored(layout, row));
            if (shown is not null) answered++;
            questions.Add(AttemptViews.Question(layout, i + 1, level == 1 ? null : item.Point, shown, level == 1 && row is not null && AttemptRules.IsAnswerFixed(row.TryCount)));
        }

        return new StudentAttemptDto(
            attempt.Id, quiz.Id, quiz.Name, quiz.LessonId, quiz.Lesson?.Title, level, QuestionPreviewBuilder.LayoutOf(level), level == 1, level == 1,
            AttemptRules.ShowsClock(level, limit, deadline), AttemptStatus.IN_PROGRESS, attempt.StartedAt ?? now, deadline,
            AttemptViews.RemainingSeconds(deadline, now), now, limit, items.Count, answered, level == 1 ? null : quiz.TotalPoint, questions);
    }

    private static int CountAnswered(List<QuizQuestion> items, List<AttemptAnswer> answers, long attemptId) =>
        items.Count(item =>
        {
            var row = answers.FirstOrDefault(a => a.QuestionId == item.QuestionId);
            return row is not null && AttemptAnswerCodec.ToStored(QuestionLayout.Build(item.Question!, attemptId), row) is not null;
        });

    private static string ActionLabel(StudentQuizAction action) => action switch
    {
        StudentQuizAction.START => "Bắt đầu",
        StudentQuizAction.CONTINUE => "Làm tiếp",
        StudentQuizAction.VIEW_RESULT => "Xem kết quả",
        StudentQuizAction.NOT_OPEN => "Chưa tới giờ mở",
        _ => "Đã đóng",
    };

    private static AppException NotFoundQuiz() => AppException.NotFound(Messages.NotFound("mở bài kiểm tra", "bài kiểm tra này của lớp em"));
}
