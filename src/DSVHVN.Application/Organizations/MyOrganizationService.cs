using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Organizations;

/// <summary>Thông tin trường phía ORG_ADMIN (tab Thông tin trường): chỉ xem và sửa thông tin liên hệ của trường mình.</summary>
public sealed class MyOrganizationService(IAppDbContext db, RequestValidator validator, PlanGuard planGuard)
{
    public async Task<MyOrganizationDto> GetAsync(Actor actor, CancellationToken ct)
    {
        var org = await FindOwnAsync(actor, tracking: false, ct);
        return await ToDtoAsync(org, ct);
    }

    public async Task<MyOrganizationDto> UpdateContactAsync(Actor actor, UpdateOrganizationContactRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var org = await FindOwnAsync(actor, tracking: true, ct);
        org.Address = ValidationRules.TrimToNull(request.Address);
        org.Phone = ValidationRules.TrimToNull(request.Phone);
        org.Email = ValidationRules.TrimToNull(request.Email) is { } email ? EmailAddress.Normalize(email) : null;
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(org, ct);
    }

    // Trường đã ngừng thì tài khoản không qua được bước kiểm mỗi yêu cầu; lọc lại ở đây để service không phụ thuộc route.
    private async Task<Organization> FindOwnAsync(Actor actor, bool tracking, CancellationToken ct)
    {
        var orgId = actor.RequireOrganizationId();
        var query = tracking ? db.Organizations : db.Organizations.AsNoTracking();
        return await query.SingleOrDefaultAsync(o => o.Id == orgId && o.DeletedAt == null, ct)
               ?? throw AppException.Forbidden(Messages.Forbidden);
    }

    private async Task<MyOrganizationDto> ToDtoAsync(Organization org, CancellationToken ct) => new(
        org.Id, org.Name, org.Address, org.Phone, org.Email,
        await planGuard.GetCurrentAsync(org.Id, ct),
        await planGuard.GetTeacherQuotaAsync(org.Id, ct));
}
