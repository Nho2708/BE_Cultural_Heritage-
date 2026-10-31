using DSVHVN.Application.Attempts;
using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Quizzes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Notifications;
using DSVHVN.Domain.Rules;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Notifications;

public sealed class NotifyClassRequestValidator : AbstractValidator<NotifyClassRequest>
{
    public NotifyClassRequestValidator() =>
        RuleFor(x => x.Message).OptionalText("Lời nhắn", StudentNotificationRules.MessageMaxLength);
}

/// <summary>
/// Thông báo cho học sinh: thầy cô gửi cho cả lớp về một bài học đã xuất bản hoặc một bài kiểm tra đang phát hành (giao bài về nhà), em xem ở nút Thông báo
/// trên trang học và mở được bài ngay, không phải gõ mã. Chỉ gửi cho em còn hoạt động trong lớp của bài.
/// </summary>
public sealed class StudentNotificationService(IAppDbContext db, RequestValidator validator, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<NotifyClassResultDto> NotifyLessonAsync(Actor actor, long lessonId, NotifyClassRequest request, CancellationToken ct)
    {
        const string action = "gửi thông báo";
        await validator.EnsureValidAsync(request, ct);
        var lesson = await db.FindLessonAsync(actor, lessonId, action, tracking: false, ct);
        if (lesson.Status != ContentStatus.PUBLISHED)
            throw AppException.BusinessRule(Messages.Blocked(action, "Bài học chưa xuất bản nên học sinh chưa mở được."));

        var title = $"Bài học mới: {lesson.Title}";
        var body = StudentNotificationRules.BodyOr(request.Message, $"Thầy cô giao bài học \"{lesson.Title}\". Em mở bài để học nhé.");
        return await SendAsync(actor, StudentNotificationKind.LESSON, lesson.ClassId, lesson.Id, null, title, body, action, ct);
    }

    public async Task<NotifyClassResultDto> NotifyQuizAsync(Actor actor, long quizId, NotifyClassRequest request, CancellationToken ct)
    {
        const string action = "gửi thông báo";
        await validator.EnsureValidAsync(request, ct);
        var quiz = await db.QuizzesOf(actor).AsNoTracking().Where(q => q.Id == quizId)
            .Select(q => new { q.Id, q.Name, q.Status, q.OpenAt, q.CloseAt, q.LessonId, q.Lesson!.ClassId })
            .SingleOrDefaultAsync(ct)
            ?? throw AppException.NotFound(Messages.NotFound(action, "bài kiểm tra của bạn"));
        var state = QuizRules.OpenStateOf(quiz.Status, quiz.OpenAt, quiz.CloseAt, Now);
        if (state is QuizOpenState.NONE or QuizOpenState.ENDED)
            throw AppException.BusinessRule(Messages.Blocked(action, "Chỉ gửi được khi bài kiểm tra đang phát hành và chưa hết hạn nhận bài."));

        var title = $"Bài kiểm tra mới: {quiz.Name}";
        var fallback = quiz.CloseAt is { } close
            ? $"Thầy cô giao bài kiểm tra \"{quiz.Name}\", hạn nộp {QuizRules.FormatVietnamTime(close)}."
            : $"Thầy cô giao bài kiểm tra \"{quiz.Name}\". Em vào làm bài nhé.";
        return await SendAsync(actor, StudentNotificationKind.QUIZ, quiz.ClassId, quiz.LessonId, quiz.Id, title, StudentNotificationRules.BodyOr(request.Message, fallback), action, ct);
    }

    private async Task<NotifyClassResultDto> SendAsync(
        Actor actor, StudentNotificationKind kind, long classId, long lessonId, long? quizId, string title, string body, string action, CancellationToken ct)
    {
        var students = await db.Students.AsNoTracking().Where(s => s.ClassId == classId && s.DeletedAt == null && s.IsActive).Select(s => s.Id).ToListAsync(ct);
        if (students.Count == 0) throw AppException.BusinessRule(Messages.Blocked(action, "Lớp chưa có học sinh."));

        var now = Now;
        db.StudentNotifications.AddRange(students.Select(id => new StudentNotification
        {
            StudentId = id, Kind = kind, LessonId = lessonId, QuizId = quizId, Title = title, Body = body, SentBy = actor.UserId, CreatedAt = now,
        }));
        await db.SaveChangesAsync(ct);
        return new NotifyClassResultDto(students.Count, $"Đã gửi thông báo cho {students.Count} học sinh.");
    }

    /// <summary>Thông báo của em, mới nhất trước, kèm số thông báo chưa đọc. Bài học đã gỡ khỏi lớp thì không hiện.</summary>
    public async Task<StudentNotificationPageDto> ListAsync(Actor actor, int? page, int? pageSize, CancellationToken ct)
    {
        var student = await StudentContext.LoadAsync(db, actor, ct);
        var mine = db.StudentNotifications.AsNoTracking()
            .Where(n => n.StudentId == student.Id && n.Lesson!.DeletedAt == null && n.Lesson.Status == ContentStatus.PUBLISHED);
        var unread = await mine.CountAsync(n => n.ReadAt == null, ct);
        var paged = await mine.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Select(n => new StudentNotificationDto(n.Id, n.Kind, n.Title, n.Body, n.LessonId, n.Lesson!.AccessCode, n.QuizId, n.CreatedAt, n.ReadAt != null))
            .ToPagedAsync(page, pageSize, ct);
        return new StudentNotificationPageDto(paged.Items, paged.Page, paged.PageSize, paged.TotalItems, paged.TotalPages, unread);
    }

    public async Task MarkReadAsync(Actor actor, long id, CancellationToken ct)
    {
        var student = await StudentContext.LoadAsync(db, actor, ct);
        var notification = await db.StudentNotifications.SingleOrDefaultAsync(n => n.Id == id && n.StudentId == student.Id, ct)
                           ?? throw AppException.NotFound(Messages.NotFound("đánh dấu đã đọc", "thông báo của em"));
        notification.ReadAt ??= Now;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkAllReadAsync(Actor actor, CancellationToken ct)
    {
        var student = await StudentContext.LoadAsync(db, actor, ct);
        var unread = await db.StudentNotifications.Where(n => n.StudentId == student.Id && n.ReadAt == null).ToListAsync(ct);
        var now = Now;
        foreach (var notification in unread) notification.ReadAt = now;
        await db.SaveChangesAsync(ct);
    }
}
