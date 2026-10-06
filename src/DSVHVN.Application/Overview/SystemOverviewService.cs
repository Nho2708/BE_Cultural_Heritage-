using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Overview;

/// <summary>
/// Tổng quan hệ thống cho quản trị hệ thống: số liệu toàn nền tảng và biểu đồ theo thời gian, tính khi hiển thị. Chỉ tính trường chưa ngừng và lớp chưa xóa.
/// Doanh thu từ giao dịch thanh toán chưa có ở bản này: phần doanh thu báo rõ chỗ sẽ nối khi có gói và thanh toán; mọi số khác không phụ thuộc thanh toán.
/// </summary>
public sealed class SystemOverviewService(IAppDbContext db, TimeProvider clock)
{
    public const string RevenueNotAvailable =
        "Doanh thu theo kỳ và danh sách giao dịch sẽ hiện khi nối với gói dịch vụ và thanh toán VNPay. Doanh thu quảng cáo xem ở bảng điều khiển của Google.";

    public async Task<SystemOverviewDto> GetAsync(Actor actor, CancellationToken ct)
    {
        EnsureAdmin(actor);
        var nowUtc = clock.GetUtcNow();
        var now = nowUtc.UtcDateTime;
        var today = VietnamTime.Today(nowUtc);

        var orgTotal = await db.Organizations.AsNoTracking().CountAsync(o => o.DeletedAt == null, ct);
        var withPlan = await db.Subscriptions.AsNoTracking()
            .Where(s => s.Organization!.DeletedAt == null && s.Status == SubscriptionStatus.ACTIVE && s.StartDate <= today && s.EndDate >= today)
            .Select(s => s.OrganizationId).Distinct().CountAsync(ct);

        var teachers = db.Users.AsNoTracking().Where(u => u.RoleId == Roles.TeacherId && u.DeletedAt == null && u.Organization!.DeletedAt == null);
        var teacherTotal = await teachers.CountAsync(ct);
        var teacherActive = await teachers.CountAsync(u => u.Status == UserStatus.ACTIVE, ct);

        var activeStudents = await db.Students.AsNoTracking().CountAsync(s => s.DeletedAt == null && s.IsActive
            && s.Class!.DeletedAt == null && s.Class.Organization!.DeletedAt == null, ct);

        var lessons = db.Lessons.AsNoTracking().Where(l => l.DeletedAt == null && l.Class!.DeletedAt == null && l.Class.Organization!.DeletedAt == null);
        var quizzes = db.Quizzes.AsNoTracking().Where(q => q.DeletedAt == null && q.Lesson!.DeletedAt == null
            && q.Lesson.Class!.DeletedAt == null && q.Lesson.Class.Organization!.DeletedAt == null);
        var questions = db.Questions.AsNoTracking().Where(q => q.DeletedAt == null);
        var content = new SystemContentDto(
            await db.Heritages.AsNoTracking().CountAsync(h => h.DeletedAt == null && h.Status == ContentStatus.PUBLISHED, ct),
            await lessons.CountAsync(ct),
            await lessons.CountAsync(l => l.Status == ContentStatus.PUBLISHED, ct),
            await quizzes.CountAsync(q => q.Status == QuizStatus.PUBLISHED || q.Status == QuizStatus.CLOSED, ct),
            await FinishedAttempts().CountAsync(ct),
            new SystemQuestionsDto(
                await questions.CountAsync(q => q.ReviewStatus == ReviewStatus.APPROVED, ct),
                await questions.CountAsync(q => q.ReviewStatus == ReviewStatus.PENDING, ct),
                await questions.CountAsync(q => q.ReviewStatus == ReviewStatus.REJECTED && q.Reviewer!.RoleId == Roles.AdminId, ct)));

        var ai = LiveAi(now);
        var monthStart = VietnamTime.StartOfDayUtc(new DateOnly(today.Year, today.Month, 1));
        var aiDto = new SystemAiDto(
            await ai.CountAsync(ct),
            await ai.CountAsync(g => g.LessonId != null, ct),
            await ai.CountAsync(g => g.LessonId == null, ct),
            await ai.CountAsync(g => g.CreatedAt >= monthStart, ct));

        return new SystemOverviewDto(
            now,
            new SystemSchoolsDto(orgTotal, withPlan, orgTotal - withPlan),
            new SystemTeachersDto(teacherTotal, teacherActive),
            activeStudents, content, aiDto,
            await PlanUsageAsync(today, ct),
            new SystemRevenueDto(false, RevenueNotAvailable));
    }

    /// <summary>
    /// Số liệu theo thời gian trong khoảng [<paramref name="from"/>, <paramref name="to"/>] (ngày Việt Nam, mặc định 30 ngày gần nhất, tối đa 366 ngày):
    /// trường, giáo viên, học sinh tạo mới, bài học tạo mới, bài kiểm tra phát hành, lượt nộp, lượt AI. Gom theo ngày khi khoảng không quá 92 ngày, dài hơn theo tháng
    /// (hoặc theo <paramref name="granularity"/> khi chọn; gom theo ngày chỉ cho khoảng không quá 92 ngày).
    /// </summary>
    public async Task<SystemTrendDto> TrendAsync(Actor actor, DateOnly? from, DateOnly? to, TrendGranularity? granularity, CancellationToken ct)
    {
        EnsureAdmin(actor);
        var nowUtc = clock.GetUtcNow();
        var today = VietnamTime.Today(nowUtc);
        var end = to ?? today;
        var start = from ?? end.AddDays(1 - OverviewRules.DefaultTrendDays);
        if (end < start) throw AppException.Validation("to", Messages.Invalid("Ngày kết thúc", "không được trước ngày bắt đầu"));
        var days = end.DayNumber - start.DayNumber + 1;
        if (days > OverviewRules.MaxTrendDays)
            throw AppException.Validation("from", Messages.Invalid("Khoảng thời gian", $"tối đa {OverviewRules.MaxTrendDays} ngày"));
        var grain = granularity ?? OverviewRules.GranularityFor(start, end);
        if (grain == TrendGranularity.DAY && days > OverviewRules.MaxDailyTrendDays)
            throw AppException.Validation("granularity", Messages.Invalid("Cách gom nhóm theo ngày", $"chỉ dùng cho khoảng tối đa {OverviewRules.MaxDailyTrendDays} ngày"));

        // Mốc UTC của đầu ngày Việt Nam; so sánh nửa mở để mọi thời điểm trong ngày cuối đều vào khoảng.
        var fromUtc = VietnamTime.StartOfDayUtc(start);
        var toUtc = VietnamTime.StartOfDayUtc(end.AddDays(1));

        var schools = await db.Organizations.AsNoTracking()
            .Where(o => o.DeletedAt == null && o.CreatedAt >= fromUtc && o.CreatedAt < toUtc).Select(o => o.CreatedAt).ToListAsync(ct);
        var teachers = await db.Users.AsNoTracking()
            .Where(u => u.RoleId == Roles.TeacherId && u.DeletedAt == null && u.Organization!.DeletedAt == null && u.CreatedAt >= fromUtc && u.CreatedAt < toUtc)
            .Select(u => u.CreatedAt).ToListAsync(ct);
        var students = await db.Students.AsNoTracking()
            .Where(s => s.DeletedAt == null && s.Class!.DeletedAt == null && s.Class.Organization!.DeletedAt == null && s.CreatedAt >= fromUtc && s.CreatedAt < toUtc)
            .Select(s => s.CreatedAt).ToListAsync(ct);
        var lessons = await db.Lessons.AsNoTracking()
            .Where(l => l.DeletedAt == null && l.Class!.DeletedAt == null && l.Class.Organization!.DeletedAt == null && l.CreatedAt >= fromUtc && l.CreatedAt < toUtc)
            .Select(l => l.CreatedAt).ToListAsync(ct);
        var quizzes = await db.Quizzes.AsNoTracking()
            .Where(q => q.DeletedAt == null && q.Lesson!.DeletedAt == null && q.Lesson.Class!.DeletedAt == null && q.Lesson.Class.Organization!.DeletedAt == null
                        && q.PublishedAt >= fromUtc && q.PublishedAt < toUtc)
            .Select(q => q.PublishedAt).ToListAsync(ct);
        var attempts = await FinishedAttempts().Where(a => a.SubmittedAt >= fromUtc && a.SubmittedAt < toUtc).Select(a => a.SubmittedAt).ToListAsync(ct);
        var ai = await LiveAi(nowUtc.UtcDateTime).Where(g => g.CreatedAt >= fromUtc && g.CreatedAt < toUtc).Select(g => g.CreatedAt).ToListAsync(ct);

        var keys = OverviewRules.Periods(start, end, grain);
        var buckets = keys.ToDictionary(k => k, _ => new int[7]);
        Bump(buckets, schools, 0, grain);
        Bump(buckets, teachers, 1, grain);
        Bump(buckets, students, 2, grain);
        Bump(buckets, lessons, 3, grain);
        Bump(buckets, quizzes, 4, grain);
        Bump(buckets, attempts, 5, grain);
        Bump(buckets, ai, 6, grain);

        var points = keys.Select(k => Point(k, buckets[k])).ToList();
        var totals = Point("total", Enumerable.Range(0, 7).Select(i => buckets.Values.Sum(v => v[i])).ToArray());
        return new SystemTrendDto(start, end, grain, points, totals);
    }

    // Lượt AI tính vào số liệu: thành công, hoặc đang xử lý chưa quá lâu (như cách đếm hạn mức của trường); lượt lỗi không tính.
    private IQueryable<Domain.Lessons.AiGeneration> LiveAi(DateTime now)
    {
        var staleBefore = now - LessonRules.AiStaleProcessing;
        return db.AiGenerations.AsNoTracking().Where(g =>
            g.Status == AiTaskStatus.SUCCEEDED || (g.Status == AiTaskStatus.PROCESSING && g.CreatedAt > staleBefore));
    }

    // Lượt đã nộp hoặc hết giờ tự nộp, ở bài kiểm tra chưa xóa của lớp chưa xóa thuộc trường chưa ngừng.
    private IQueryable<Domain.Quizzes.Attempt> FinishedAttempts() =>
        db.Attempts.AsNoTracking().Where(a => a.Status != AttemptStatus.IN_PROGRESS
            && a.Quiz!.DeletedAt == null && a.Quiz.Lesson!.DeletedAt == null
            && a.Quiz.Lesson.Class!.DeletedAt == null && a.Quiz.Lesson.Class.Organization!.DeletedAt == null);

    private async Task<IReadOnlyList<SystemPlanUsageDto>> PlanUsageAsync(DateOnly today, CancellationToken ct)
    {
        var plans = await db.Plans.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct);
        var effective = await db.Subscriptions.AsNoTracking()
            .Where(s => s.Organization!.DeletedAt == null && s.Status == SubscriptionStatus.ACTIVE && s.StartDate <= today && s.EndDate >= today)
            .Select(s => new { s.PlanId, s.OrganizationId, s.EndDate })
            .ToListAsync(ct);
        return plans.Select(p =>
        {
            var rows = effective.Where(s => s.PlanId == p.Id).ToList();
            // Một trường chỉ tính một lần cho mỗi gói dù có nhiều đăng ký chồng nhau.
            var schools = rows.Select(s => s.OrganizationId).Distinct().Count();
            var expiring = rows.GroupBy(s => s.OrganizationId).Count(g => OverviewRules.IsExpiringSoon(g.Max(s => s.EndDate!.Value), today));
            return new SystemPlanUsageDto(p.Id, p.Name, p.Price, p.IsActive, schools, expiring);
        }).ToList();
    }

    private static void Bump(Dictionary<string, int[]> buckets, IEnumerable<DateTime?> times, int slot, TrendGranularity grain)
    {
        foreach (var t in times)
        {
            if (t is not { } utc) continue;
            var local = DateOnly.FromDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc).Add(VietnamTime.Offset));
            if (buckets.TryGetValue(OverviewRules.PeriodKey(local, grain), out var cell)) cell[slot]++;
        }
    }

    private static SystemTrendPointDto Point(string period, int[] v) => new(period, v[0], v[1], v[2], v[3], v[4], v[5], v[6]);

    private static void EnsureAdmin(Actor actor)
    {
        if (actor.Role != RoleCode.ADMIN) throw AppException.Forbidden(Messages.Forbidden);
    }
}
