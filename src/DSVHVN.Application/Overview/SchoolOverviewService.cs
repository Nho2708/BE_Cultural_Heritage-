using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Overview;

/// <summary>
/// Tổng quan trường cho quản trị trường: chỉ số tổng hợp của chính trường mình, tính khi hiển thị. Quản trị trường không xem lớp, học sinh hay
/// bài làm cụ thể: lớp chỉ hiện dưới dạng một dòng số (tên lớp, khối, sĩ số, số lượt nộp, điểm trung bình), không có tên giáo viên hay học sinh.
/// Lượt nộp và điểm chỉ tính lượt đã nộp hoặc hết giờ tự nộp của học sinh đang hoạt động trong lớp chưa xóa, ở bài kiểm tra chưa xóa.
/// Gói hết hạn không ngăn xem tổng quan; chỉ hiện lời nhắc.
/// </summary>
public sealed class SchoolOverviewService(IAppDbContext db, PlanGuard planGuard, TimeProvider clock)
{
    public async Task<SchoolOverviewDto> GetAsync(Actor actor, CancellationToken ct)
    {
        if (actor.Role != RoleCode.ORG_ADMIN) throw AppException.Forbidden(Messages.Forbidden);
        var orgId = actor.RequireOrganizationId();
        var org = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == orgId && o.DeletedAt == null, ct)
                  ?? throw AppException.Forbidden(Messages.Forbidden);
        var now = clock.GetUtcNow().UtcDateTime;

        var classes = await db.Classes.AsNoTracking()
            .Where(c => c.OrganizationId == orgId && c.DeletedAt == null)
            .OrderBy(c => c.Grade).ThenBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Grade,
                c.SchoolYear,
                Active = db.Students.Count(s => s.ClassId == c.Id && s.DeletedAt == null && s.IsActive),
                Never = db.Students.Count(s => s.ClassId == c.Id && s.DeletedAt == null && s.IsActive && s.User!.LastLoginAt == null),
            })
            .ToListAsync(ct);

        // Lượt đã chốt của học sinh đang hoạt động trong lớp chưa xóa; chỉ lấy ba cột cần để tính trung bình theo lớp, cấp và cả trường.
        var attempts = await db.Attempts.AsNoTracking()
            .Where(a => a.Status != AttemptStatus.IN_PROGRESS
                        && a.Student!.DeletedAt == null && a.Student.IsActive
                        && a.Student.Class!.DeletedAt == null && a.Student.Class.OrganizationId == orgId
                        && a.Quiz!.DeletedAt == null && a.Quiz.Lesson!.DeletedAt == null && a.Quiz.Lesson.ClassId == a.Student.ClassId)
            .Select(a => new { a.Student!.ClassId, a.Score, a.TotalPoint })
            .ToListAsync(ct);
        var percentsByClass = attempts
            .Select(a => (a.ClassId, Percent: OverviewRules.PercentOf(a.Score, a.TotalPoint)))
            .Where(x => x.Percent is not null)
            .ToLookup(x => x.ClassId, x => x.Percent!.Value);
        var attemptsByClass = attempts.ToLookup(a => a.ClassId);

        var classRows = classes.Select(c =>
        {
            var level = SchoolClass.SchoolLevelOf(c.Grade);
            return new OverviewClassDto(c.Id, c.Name, c.Grade, level, SchoolLevels.NameOf(level), c.SchoolYear, c.Active,
                attemptsByClass[c.Id].Count(), OverviewRules.AveragePercent(percentsByClass[c.Id].ToList()));
        }).ToList();

        var levels = Enumerable.Range(1, 3).Select(level =>
        {
            var inLevel = classRows.Where(c => c.SchoolLevel == level).ToList();
            var percents = inLevel.SelectMany(c => percentsByClass[c.ClassId]).ToList();
            return new OverviewLevelDto(level, SchoolLevels.NameOf(level), inLevel.Count, inLevel.Sum(c => c.ActiveStudents),
                inLevel.Sum(c => c.FinishedAttempts), OverviewRules.AveragePercent(percents));
        }).ToList();

        var active = classes.Sum(c => c.Active);
        var never = classes.Sum(c => c.Never);
        var allPercents = percentsByClass.SelectMany(g => g).ToList();

        var lessons = db.Lessons.AsNoTracking().Where(l => l.DeletedAt == null && l.Class!.OrganizationId == orgId && l.Class.DeletedAt == null);
        var lessonTotal = await lessons.CountAsync(ct);
        var lessonPublished = await lessons.CountAsync(l => l.Status == ContentStatus.PUBLISHED, ct);

        var quizzes = db.Quizzes.AsNoTracking().Where(q => q.DeletedAt == null && q.Lesson!.DeletedAt == null
            && q.Lesson.Class!.OrganizationId == orgId && q.Lesson.Class.DeletedAt == null
            && (q.Status == QuizStatus.PUBLISHED || q.Status == QuizStatus.CLOSED));
        var quizPublished = await quizzes.CountAsync(ct);
        var quizOpen = await quizzes.CountAsync(q => q.Status == QuizStatus.PUBLISHED && (q.CloseAt == null || q.CloseAt > now), ct);

        var plan = await planGuard.GetCurrentAsync(orgId, ct);
        var notice = NoticeOf(plan, planGuard.Today());

        return new SchoolOverviewDto(
            org.Id, org.Name, now, plan, notice,
            await planGuard.GetTeacherQuotaAsync(orgId, ct),
            classRows.Count, levels,
            new OverviewStudentsDto(active, active - never, never),
            new OverviewLessonsDto(lessonTotal, lessonPublished),
            new OverviewQuizzesDto(quizPublished, quizOpen),
            new OverviewAttemptsDto(attempts.Count, OverviewRules.AveragePercent(allPercents)),
            classRows,
            await planGuard.GetAiQuotaAsync(orgId, ct));
    }

    /// <summary>Lời nhắc: không có gói hiệu lực thì báo ngay; gói còn ít ngày thì nhắc gia hạn. Học sinh vẫn học và làm bài bình thường.</summary>
    private static PlanNoticeDto NoticeOf(SubscriptionDto? plan, DateOnly today)
    {
        if (plan is not { IsEffective: true, EndDate: { } end })
            return new PlanNoticeDto(PlanNoticeKind.NO_EFFECTIVE_PLAN, null,
                "Trường chưa có gói dịch vụ còn hiệu lực. Hãy mua hoặc gia hạn gói để tạo bài học, bài kiểm tra mới và dùng AI; học sinh vẫn học bình thường.");

        var left = OverviewRules.DaysLeft(end, today);
        return OverviewRules.IsExpiringSoon(end, today)
            ? new PlanNoticeDto(PlanNoticeKind.EXPIRING_SOON, left,
                $"Gói {plan.PlanName} còn {left} ngày, hết hạn ngày {end:dd/MM/yyyy}. Hãy gia hạn gói để không bị gián đoạn.")
            : new PlanNoticeDto(PlanNoticeKind.NONE, left, null);
    }
}
