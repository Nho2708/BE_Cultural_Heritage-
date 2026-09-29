using DSVHVN.Application.Accounts;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Teachers;

public sealed record TeacherListQuery(string? Keyword, UserStatus? Status, int? Page, int? PageSize);

/// <summary>Màn hình Giáo viên của trường: bảng giáo viên kèm bộ đếm "x / max_teachers".</summary>
public sealed record TeacherListDto(
    IReadOnlyList<AccountDto> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages,
    TeacherQuotaDto Quota);

/// <summary>
/// Quản lý tài khoản giáo viên (ORG_ADMIN). Mọi truy vấn lọc theo trường của người gọi:
/// id giáo viên của trường khác trả 404 như không tồn tại.
/// </summary>
public sealed class TeacherService(
    IAppDbContext db,
    AccountProvisioner provisioner,
    AccountActions accountActions,
    PlanGuard planGuard)
{
    public async Task<TeacherListDto> ListAsync(Actor actor, TeacherListQuery query, CancellationToken ct)
    {
        var orgId = actor.RequireOrganizationId();
        var teachers = TeachersOf(orgId).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            teachers = teachers.Where(u => u.FullName.Contains(keyword) || u.Username.Contains(keyword) || u.Email.Contains(keyword));
        }
        if (query.Status is { } status) teachers = teachers.Where(u => u.Status == status);

        var page = await teachers.OrderBy(u => u.FullName).ThenBy(u => u.Id).ToPagedAsync(query.Page, query.PageSize, ct);
        return new TeacherListDto(page.Items.Select(AccountDto.From).ToList(), page.Page, page.PageSize,
            page.TotalItems, page.TotalPages, await planGuard.GetTeacherQuotaAsync(orgId, ct));
    }

    public async Task<AccountDto> GetAsync(Actor actor, long id, CancellationToken ct) =>
        AccountDto.From(await FindAsync(actor, id, "xem tài khoản", tracking: false, ct));

    /// <summary>
    /// Trong một giao dịch có khóa dòng trường — kiểm gói hiệu lực, kiểm số giáo viên &lt; max_teachers,
    /// tạo tài khoản TEACHER (mật khẩu ngẫu nhiên, liên kết 48 giờ), ghi ACCOUNT_CREATED. Thư gửi sau khi commit.
    /// </summary>
    public async Task<Created<AccountDto>> CreateAsync(Actor actor, NewAccountRequest request, CancellationToken ct)
    {
        var orgId = actor.RequireOrganizationId();
        await provisioner.ValidateAsync(request, ct);

        ProvisionedAccount account;
        string orgName;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.LockOrganizationAsync(orgId, ct);
            orgName = await db.Organizations.Where(o => o.Id == orgId).Select(o => o.Name).SingleAsync(ct);
            await planGuard.EnsureCanAddTeacherAsync(orgId, ct);
            account = await provisioner.CreateAsync(request, RoleCode.TEACHER, orgId, actor, ct);
            await tx.CommitAsync(ct);
        }

        await provisioner.SendFirstPasswordLinkAsync(account, orgName, ct);
        return new Created<AccountDto>(AccountDto.From(account.User), account.User.Email);
    }

    public async Task<AccountDto> LockAsync(Actor actor, long id, CancellationToken ct)
    {
        var user = await FindAsync(actor, id, "khóa tài khoản", tracking: true, ct);
        await accountActions.LockAsync(user, actor, ct);
        return AccountDto.From(user);
    }

    public async Task<AccountDto> UnlockAsync(Actor actor, long id, CancellationToken ct)
    {
        var user = await FindAsync(actor, id, "mở khóa tài khoản", tracking: true, ct);
        await accountActions.UnlockAsync(user, actor, ct);
        return AccountDto.From(user);
    }

    public async Task DeleteAsync(Actor actor, long id, CancellationToken ct)
    {
        var user = await FindAsync(actor, id, "xóa tài khoản", tracking: true, ct);
        await accountActions.SoftDeleteAsync(user, actor, ct);
    }

    /// <summary>Tầng lọc dùng chung của luồng: chỉ TEACHER chưa xóa mềm thuộc đúng trường.</summary>
    private IQueryable<User> TeachersOf(long organizationId) =>
        db.Users.Where(u => u.OrganizationId == organizationId && u.RoleId == Roles.TeacherId && u.DeletedAt == null);

    private async Task<User> FindAsync(Actor actor, long id, string action, bool tracking, CancellationToken ct)
    {
        var query = TeachersOf(actor.RequireOrganizationId());
        if (!tracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(u => u.Id == id, ct)
               ?? throw AppException.NotFound(Messages.NotFound(action, "giáo viên trong trường của bạn"));
    }
}
