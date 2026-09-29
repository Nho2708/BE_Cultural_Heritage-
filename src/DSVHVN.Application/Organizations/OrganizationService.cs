using DSVHVN.Application.Accounts;
using DSVHVN.Application.Audit;
using DSVHVN.Application.Auth;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Organizations;

/// <summary>
/// Quản lý trường phía ADMIN: tạo trường kèm ORG_ADMIN đầu tiên và gói "Miễn phí", sửa, ngừng trường,
/// thêm, khóa, mở khóa quản trị trường. ADMIN không thuộc trường nào nên thấy mọi trường, kể cả trường đã ngừng.
/// </summary>
public sealed class OrganizationService(
    IAppDbContext db,
    RequestValidator validator,
    AccountProvisioner provisioner,
    AccountActions accountActions,
    PasswordTokenService passwordTokens,
    PlanGuard planGuard,
    IAuditLogger audit,
    TimeProvider clock)
{
    public async Task<PagedResult<OrganizationSummaryDto>> ListAsync(OrganizationListQuery query, CancellationToken ct)
    {
        var today = planGuard.Today();
        var orgs = db.Organizations.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            orgs = orgs.Where(o => o.Name.Contains(keyword) || (o.Email != null && o.Email.Contains(keyword)));
        }
        orgs = query.Status switch
        {
            OrganizationState.ACTIVE => orgs.Where(o => o.DeletedAt == null),
            OrganizationState.DEACTIVATED => orgs.Where(o => o.DeletedAt != null),
            _ => orgs,
        };

        var page = await orgs
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Select(o => new
            {
                o.Id, o.Name, o.Email, o.Phone, o.CreatedAt, o.DeletedAt,
                TeacherCount = db.Users.Count(u =>
                    u.OrganizationId == o.Id && u.RoleId == Roles.TeacherId && u.DeletedAt == null),
                // Gói hiệu lực nếu có, không thì đăng ký gần nhất.
                Subscription = db.Subscriptions
                    .Where(s => s.OrganizationId == o.Id)
                    .OrderByDescending(s => s.Status == SubscriptionStatus.ACTIVE && s.StartDate <= today && s.EndDate >= today)
                    .ThenByDescending(s => s.Id)
                    .Select(s => new { s.Status, s.StartDate, s.EndDate, PlanName = s.Plan!.Name })
                    .FirstOrDefault(),
            })
            .ToPagedAsync(query.Page, query.PageSize, ct);

        var items = page.Items.Select(o => new OrganizationSummaryDto(
            o.Id, o.Name, o.Email, o.Phone,
            o.Subscription?.PlanName, o.Subscription?.Status, o.Subscription?.EndDate,
            o.Subscription is { Status: SubscriptionStatus.ACTIVE } s && s.StartDate <= today && today <= s.EndDate,
            o.TeacherCount,
            o.DeletedAt is null ? OrganizationState.ACTIVE : OrganizationState.DEACTIVATED,
            o.CreatedAt, o.DeletedAt)).ToList();
        return new PagedResult<OrganizationSummaryDto>(items, page.Page, page.PageSize, page.TotalItems, page.TotalPages);
    }

    public async Task<OrganizationDetailDto> GetAsync(long id, CancellationToken ct)
    {
        var org = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, ct)
                  ?? throw AppException.NotFound(Messages.NotFound("xem trường", "trường"));
        return await ToDetailAsync(org, ct);
    }

    /// <summary>
    /// Một giao dịch tạo trường, đăng ký gói "Miễn phí" ACTIVE từ hôm nay, tài khoản ORG_ADMIN đầu tiên
    /// (mật khẩu ngẫu nhiên, liên kết 48 giờ). Ghi ORG_CREATED và ACCOUNT_CREATED. Thư gửi sau khi commit.
    /// </summary>
    public async Task<Created<OrganizationDetailDto>> CreateAsync(CreateOrganizationRequest request, Actor actor, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        ProvisionedAccount orgAdmin;
        Organization org;

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            org = new Organization
            {
                Name = request.Name!.Trim(),
                Address = ValidationRules.TrimToNull(request.Address),
                Phone = ValidationRules.TrimToNull(request.Phone),
                Email = ValidationRules.TrimToNull(request.Email) is { } email ? EmailAddress.Normalize(email) : null,
            };
            db.Organizations.Add(org);
            await db.SaveChangesAsync(ct);

            var (subscription, freePlan) = await planGuard.AddFreeSubscriptionAsync(org.Id, ct);
            await db.SaveChangesAsync(ct);

            audit.Record(AuditAction.ORG_CREATED, AuditTargets.Organizations, org.Id, actor, newValue: new
            {
                name = org.Name,
                email = org.Email,
                plan = freePlan.Name,
                subscriptionId = subscription.Id,
                startDate = subscription.StartDate,
                endDate = subscription.EndDate,
            });

            orgAdmin = await provisioner.CreateAsync(request.OrgAdmin!, RoleCode.ORG_ADMIN, org.Id, actor, ct, "orgAdmin.");
            await tx.CommitAsync(ct);
        }

        await provisioner.SendFirstPasswordLinkAsync(orgAdmin, org.Name, ct);
        return new Created<OrganizationDetailDto>(await ToDetailAsync(org, ct), orgAdmin.Email);
    }

    /// <summary>ADMIN sửa thông tin trường (cả tên). Trường đã ngừng thì không sửa.</summary>
    public async Task<OrganizationDetailDto> UpdateAsync(long id, OrganizationInfoRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var org = await FindActiveAsync(id, "sửa thông tin trường", ct);
        org.Name = request.Name!.Trim();
        org.Address = ValidationRules.TrimToNull(request.Address);
        org.Phone = ValidationRules.TrimToNull(request.Phone);
        org.Email = ValidationRules.TrimToNull(request.Email) is { } email ? EmailAddress.Normalize(email) : null;
        await db.SaveChangesAsync(ct);
        return await ToDetailAsync(org, ct);
    }

    /// <summary>
    /// Ngừng trường = xóa mềm. Mọi tài khoản của trường không đăng nhập được và token đang dùng bị từ chối ngay;
    /// liên kết đặt mật khẩu chưa dùng bị vô hiệu; dữ liệu giữ nguyên. Ghi ORG_DEACTIVATED.
    /// </summary>
    public async Task DeactivateAsync(long id, Actor actor, CancellationToken ct)
    {
        var org = await FindActiveAsync(id, "ngừng trường", ct);
        org.DeletedAt = clock.GetUtcNow().UtcDateTime;
        await passwordTokens.RevokeAllOfOrganizationAsync(org.Id, ct);
        audit.Record(AuditAction.ORG_DEACTIVATED, AuditTargets.Organizations, org.Id, actor,
            oldValue: new { name = org.Name, deletedAt = (DateTime?)null }, newValue: new { deletedAt = org.DeletedAt });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Nút "Thêm quản trị trường" ở Chi tiết trường (biểu mẫu tài khoản với vai trò Quản trị trường). Không tính vào <c>max_teachers</c>.</summary>
    public async Task<Created<AccountDto>> AddOrgAdminAsync(long organizationId, NewAccountRequest request, Actor actor, CancellationToken ct)
    {
        await provisioner.ValidateAsync(request, ct);
        var org = await FindActiveAsync(organizationId, "thêm quản trị trường", ct);

        ProvisionedAccount account;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            account = await provisioner.CreateAsync(request, RoleCode.ORG_ADMIN, org.Id, actor, ct);
            await tx.CommitAsync(ct);
        }

        await provisioner.SendFirstPasswordLinkAsync(account, org.Name, ct);
        return new Created<AccountDto>(AccountDto.From(account.User), account.Email);
    }

    public async Task<AccountDto> LockOrgAdminAsync(long organizationId, long userId, Actor actor, CancellationToken ct)
    {
        var user = await FindOrgAdminAsync(organizationId, userId, "khóa tài khoản", ct);
        await accountActions.LockAsync(user, actor, ct);
        return AccountDto.From(user);
    }

    public async Task<AccountDto> UnlockOrgAdminAsync(long organizationId, long userId, Actor actor, CancellationToken ct)
    {
        var user = await FindOrgAdminAsync(organizationId, userId, "mở khóa tài khoản", ct);
        await accountActions.UnlockAsync(user, actor, ct);
        return AccountDto.From(user);
    }

    private async Task<Organization> FindActiveAsync(long id, string action, CancellationToken ct)
    {
        var org = await db.Organizations.SingleOrDefaultAsync(o => o.Id == id, ct)
                  ?? throw AppException.NotFound(Messages.NotFound(action, "trường"));
        if (org.DeletedAt is not null)
            throw AppException.BusinessRule(Messages.Blocked(action, "Trường đã ngừng sử dụng nền tảng."));
        return org;
    }

    private async Task<User> FindOrgAdminAsync(long organizationId, long userId, string action, CancellationToken ct)
    {
        await FindActiveAsync(organizationId, action, ct);
        return await db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.OrganizationId == organizationId
                                                        && u.RoleId == Roles.OrgAdminId && u.DeletedAt == null, ct)
               ?? throw AppException.NotFound(Messages.NotFound(action, "quản trị trường"));
    }

    private async Task<OrganizationDetailDto> ToDetailAsync(Organization org, CancellationToken ct)
    {
        var orgAdmins = await db.Users.AsNoTracking()
            .Where(u => u.OrganizationId == org.Id && u.RoleId == Roles.OrgAdminId && u.DeletedAt == null)
            .OrderBy(u => u.CreatedAt).ThenBy(u => u.Id)
            .ToListAsync(ct);

        return new OrganizationDetailDto(
            org.Id, org.Name, org.Address, org.Phone, org.Email,
            org.DeletedAt is null ? OrganizationState.ACTIVE : OrganizationState.DEACTIVATED,
            org.CreatedAt, org.UpdatedAt, org.DeletedAt,
            await planGuard.GetCurrentAsync(org.Id, ct),
            await planGuard.GetTeacherQuotaAsync(org.Id, ct),
            orgAdmins.Select(AccountDto.From).ToList());
    }
}
