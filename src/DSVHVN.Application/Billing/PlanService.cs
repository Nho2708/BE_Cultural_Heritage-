using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Billing;

/// <summary>
/// Quản lý gói dịch vụ của quản trị hệ thống (màn hình Gói dịch vụ): tạo, sửa, bật và tắt bán. Mọi thay đổi ghi nhật ký thao tác.
/// Tên gói không trùng (không phân biệt hoa thường và dấu); giá là số nguyên đồng không âm; gói Miễn phí là gói giá 0 duy nhất,
/// luôn đang bán, giữ nguyên tên và giá. Đổi giá không hồi tố: hóa đơn đã tạo giữ số tiền lúc tạo. Không có thao tác xóa gói.
/// </summary>
public sealed class PlanService(IAppDbContext db, RequestValidator validator, PlanGuard planGuard, IAuditLogger audit)
{
    public async Task<IReadOnlyList<PlanDto>> ListAsync(CancellationToken ct)
    {
        var plans = await db.Plans.AsNoTracking().ToListAsync(ct);
        var counts = await SchoolCountsAsync(ct);
        return plans
            .OrderBy(p => p.Price).ThenBy(p => p.Id)
            .Select(p => ToDto(p, counts.GetValueOrDefault(p.Id)))
            .ToList();
    }

    public async Task<PlanDto> GetAsync(int id, CancellationToken ct)
    {
        var plan = await db.Plans.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct)
                   ?? throw AppException.NotFound(BillingMessages.PlanNotFound("xem gói"));
        return ToDto(plan, (await SchoolCountsAsync(ct)).GetValueOrDefault(id));
    }

    public async Task<PlanDto> CreateAsync(PlanRequest request, Actor actor, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var plans = await db.Plans.ToListAsync(ct);
        EnsureNameFree(plans, request.Name!, exceptId: null);
        if (request.Price == 0m)
            throw AppException.Validation("price", Messages.Invalid("Giá gói", "gói Miễn phí là gói giá 0 duy nhất, các gói khác phải có giá lớn hơn 0"));

        var plan = new Plan
        {
            Name = request.Name!.Trim(),
            Price = request.Price,
            DurationDays = request.DurationDays,
            MaxTeachers = request.MaxTeachers,
            MaxAiRequests = request.MaxAiRequests,
            IsActive = true,
        };
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            db.Plans.Add(plan);
            await db.SaveChangesAsync(ct);
            audit.Record(AuditAction.PLAN_UPDATED, AuditTargets.Plans, plan.Id, actor, newValue: Snapshot(plan, "created"));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return ToDto(plan, 0);
    }

    public async Task<PlanDto> UpdateAsync(int id, PlanRequest request, Actor actor, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var plans = await db.Plans.ToListAsync(ct);
        var plan = plans.SingleOrDefault(p => p.Id == id) ?? throw AppException.NotFound(BillingMessages.PlanNotFound("sửa gói"));
        var name = request.Name!.Trim();
        EnsureNameFree(plans, name, exceptId: id);

        if (IsFree(plan))
        {
            if (request.Price != 0m)
                throw AppException.Validation("price", Messages.Invalid("Giá gói", "gói Miễn phí luôn có giá 0"));
            if (!SameName(plan.Name, name))
                throw AppException.Validation("name", Messages.Invalid("Tên gói", "gói Miễn phí giữ nguyên tên"));
        }
        else if (request.Price == 0m)
        {
            throw AppException.Validation("price", Messages.Invalid("Giá gói", "gói Miễn phí là gói giá 0 duy nhất, các gói khác phải có giá lớn hơn 0"));
        }

        var before = Snapshot(plan, "before");
        plan.Name = IsFree(plan) ? plan.Name : name;
        plan.Price = request.Price;
        plan.DurationDays = request.DurationDays;
        plan.MaxTeachers = request.MaxTeachers;
        plan.MaxAiRequests = request.MaxAiRequests;
        audit.Record(AuditAction.PLAN_UPDATED, AuditTargets.Plans, plan.Id, actor, oldValue: before, newValue: Snapshot(plan, "updated"));
        await db.SaveChangesAsync(ct);
        return ToDto(plan, (await SchoolCountsAsync(ct)).GetValueOrDefault(id));
    }

    /// <summary>Bật bán: gói được chọn mua và gia hạn trở lại.</summary>
    public Task<PlanDto> EnableAsync(int id, Actor actor, CancellationToken ct) => SetSaleAsync(id, true, actor, ct);

    /// <summary>Tắt bán: không bán mới và không gia hạn được, đăng ký đang có vẫn dùng tới hết hạn. Gói Miễn phí không tắt được.</summary>
    public Task<PlanDto> DisableAsync(int id, Actor actor, CancellationToken ct) => SetSaleAsync(id, false, actor, ct);

    private async Task<PlanDto> SetSaleAsync(int id, bool active, Actor actor, CancellationToken ct)
    {
        var plan = await db.Plans.SingleOrDefaultAsync(p => p.Id == id, ct)
                   ?? throw AppException.NotFound(BillingMessages.PlanNotFound(active ? "bật bán gói" : "tắt bán gói"));
        if (!active && IsFree(plan)) throw AppException.BusinessRule(BillingMessages.FreePlanCannotBeDisabled);

        if (plan.IsActive != active)
        {
            plan.IsActive = active;
            audit.Record(AuditAction.PLAN_UPDATED, AuditTargets.Plans, plan.Id, actor,
                oldValue: new { isActive = !active }, newValue: new { isActive = active, name = plan.Name });
            await db.SaveChangesAsync(ct);
        }
        return ToDto(plan, (await SchoolCountsAsync(ct)).GetValueOrDefault(id));
    }

    /// <summary>Số trường có gói hiệu lực thuộc từng gói; trường đã ngừng không tính.</summary>
    private async Task<Dictionary<int, int>> SchoolCountsAsync(CancellationToken ct)
    {
        var today = planGuard.Today();
        var rows = await db.Subscriptions.AsNoTracking()
            .Where(s => s.Status == SubscriptionStatus.ACTIVE && s.StartDate <= today && s.EndDate >= today
                        && s.Organization!.DeletedAt == null)
            .Select(s => new { s.PlanId, s.OrganizationId })
            .ToListAsync(ct);
        return rows.Distinct().GroupBy(r => r.PlanId).ToDictionary(g => g.Key, g => g.Count());
    }

    private static bool IsFree(Plan plan) => plan.Price == 0m;

    private static bool SameName(string? a, string? b) => SearchText.Fold(a) == SearchText.Fold(b);

    private static void EnsureNameFree(IEnumerable<Plan> plans, string name, int? exceptId)
    {
        if (plans.Any(p => p.Id != exceptId && SameName(p.Name, name)))
            throw AppException.Conflict("name", BillingMessages.PlanNameTaken(name.Trim()));
    }

    private static PlanDto ToDto(Plan p, int schoolCount) => new(
        p.Id, p.Name, p.Price, p.DurationDays, p.MaxTeachers, p.MaxAiRequests, p.IsActive, IsFree(p), schoolCount);

    private static object Snapshot(Plan p, string state) => new
    {
        state, name = p.Name, price = p.Price, durationDays = p.DurationDays, maxTeachers = p.MaxTeachers,
        maxAiRequests = p.MaxAiRequests, isActive = p.IsActive,
    };
}
