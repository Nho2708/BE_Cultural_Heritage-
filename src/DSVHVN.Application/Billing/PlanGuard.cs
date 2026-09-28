using DSVHVN.Application.Common;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Billing;

/// <summary>Gói của trường cho giao diện (tab "Gói và hạn mức" của Thông tin trường, cột gói ở Quản lý trường).</summary>
public sealed record SubscriptionDto(
    long Id,
    int PlanId,
    string? PlanName,
    SubscriptionStatus? Status,
    DateOnly? StartDate,
    DateOnly? EndDate,
    bool IsEffective);

/// <summary>
/// Bộ đếm "x / max_teachers giáo viên" (màn hình Giáo viên của trường). <see cref="Max"/> null khi trường không có gói hiệu lực
/// (hoặc gói chưa khai báo hạn mức — khi đó không thêm được giáo viên).
/// </summary>
public sealed record TeacherQuotaDto(int Used, int? Max, string? PlanName, bool CanAddTeacher);

/// <summary>
/// Service dùng chung "kiểm gói và hạn mức", dựng từ luồng nền tảng. Hiện dùng phần gói hiệu lực
/// và <c>max_teachers</c>; đếm lượt AI thêm khi có bảng <c>ai_generations</c>.
/// </summary>
public sealed class PlanGuard(IAppDbContext db, TimeProvider clock)
{
    /// <summary>Hôm nay theo lịch Việt Nam — ngày của gói hiểu theo giờ Việt Nam.</summary>
    public DateOnly Today() => VietnamTime.Today(clock.GetUtcNow());

    /// <summary>Gói hiệu lực: ACTIVE và start_date ≤ hôm nay ≤ end_date; null nếu không có.</summary>
    public Task<Subscription?> GetEffectiveAsync(long organizationId, CancellationToken ct)
    {
        var today = Today();
        return db.Subscriptions.AsNoTracking().Include(s => s.Plan)
            .Where(s => s.OrganizationId == organizationId && s.Status == SubscriptionStatus.ACTIVE
                        && s.StartDate <= today && s.EndDate >= today)
            .OrderByDescending(s => s.StartDate).ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Gói để hiển thị: gói hiệu lực nếu có, không thì đăng ký gần nhất (để hiện "hết hạn", "đã hủy").</summary>
    public async Task<SubscriptionDto?> GetCurrentAsync(long organizationId, CancellationToken ct)
    {
        var effective = await GetEffectiveAsync(organizationId, ct);
        var shown = effective ?? await db.Subscriptions.AsNoTracking().Include(s => s.Plan)
            .Where(s => s.OrganizationId == organizationId)
            .OrderByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);
        return shown is null ? null : ToDto(shown, Today());
    }

    /// <summary>Số giáo viên = tài khoản TEACHER chưa xóa mềm của trường, mọi trạng thái; không tính ORG_ADMIN.</summary>
    public Task<int> CountTeachersAsync(long organizationId, CancellationToken ct) =>
        db.Users.CountAsync(u => u.OrganizationId == organizationId && u.RoleId == Roles.TeacherId && u.DeletedAt == null, ct);

    public async Task<TeacherQuotaDto> GetTeacherQuotaAsync(long organizationId, CancellationToken ct)
    {
        var used = await CountTeachersAsync(organizationId, ct);
        var plan = (await GetEffectiveAsync(organizationId, ct))?.Plan;
        return new TeacherQuotaDto(used, plan?.MaxTeachers, plan?.Name, plan is not null && used < MaxTeachersOf(plan));
    }

    /// <summary>
    /// Kiểm gói hiệu lực và hạn mức giáo viên trước khi tạo giáo viên. Người gọi phải đang trong giao dịch và đã khóa dòng trường
    /// (<see cref="IAppDbContext.LockOrganizationAsync"/>) để hai yêu cầu đồng thời không cùng vượt hạn mức.
    /// </summary>
    public async Task EnsureCanAddTeacherAsync(long organizationId, CancellationToken ct)
    {
        var plan = (await GetEffectiveAsync(organizationId, ct))?.Plan
                   ?? throw AppException.BusinessRule(Messages.Blocked("thêm giáo viên", "Trường chưa có gói dịch vụ còn hiệu lực."));
        var used = await CountTeachersAsync(organizationId, ct);
        var max = MaxTeachersOf(plan);
        if (used >= max)
            throw AppException.BusinessRule(Messages.TeacherLimitReached(used, max, plan.Name ?? string.Empty));
    }

    /// <summary>Hạn mức giáo viên của gói; cột được NULL trong CSDL, gói chưa khai báo hạn mức coi như 0 (không thêm được).</summary>
    private static int MaxTeachersOf(Plan plan) => plan.MaxTeachers ?? 0;

    /// <summary>
    /// Trường mới nhận ngay gói "Miễn phí" ACTIVE từ hôm nay, không qua VNPay, không có hóa đơn.
    /// Gói Miễn phí là gói giá 0 duy nhất, seed lúc khởi động.
    /// </summary>
    public async Task<(Subscription Subscription, Plan Plan)> AddFreeSubscriptionAsync(long organizationId, CancellationToken ct)
    {
        // Bảng plans chỉ vài dòng: lọc giá 0 trong bộ nhớ để không phụ thuộc cách provider so sánh decimal.
        var plans = await db.Plans.AsNoTracking().ToListAsync(ct);
        var free = plans.Where(p => p.Price == 0m).OrderBy(p => p.Id).FirstOrDefault()
                   ?? throw new InvalidOperationException("Chưa seed gói \"Miễn phí\" (plans giá 0).");
        var durationDays = free.DurationDays ?? throw new InvalidOperationException("Gói \"Miễn phí\" chưa có thời hạn (duration_days).");
        var today = Today();
        var subscription = new Subscription
        {
            OrganizationId = organizationId,
            PlanId = free.Id,
            StartDate = today,
            EndDate = Subscription.EndDateFor(today, durationDays),
            Status = SubscriptionStatus.ACTIVE,
        };
        db.Subscriptions.Add(subscription);
        return (subscription, free);
    }

    public static SubscriptionDto ToDto(Subscription s, DateOnly today) =>
        new(s.Id, s.PlanId, s.Plan?.Name, s.Status, s.StartDate, s.EndDate, s.IsEffectiveOn(today));
}
