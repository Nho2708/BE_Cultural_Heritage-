using DSVHVN.Application.Accounts;
using DSVHVN.Application.Auth;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Application.Organizations;
using DSVHVN.Application.Teachers;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Unit;

/// <summary>Quản lý trường và giáo viên ở tầng service: gói Miễn phí cho trường mới, liên kết 48 giờ, phạm vi theo trường, hạn mức giáo viên, nhật ký thao tác, kiểm gói và hạn mức.</summary>
public sealed class OrganizationServiceTests : IDisposable
{
    private readonly ServiceHarness _h = new(freePlanMaxTeachers: 2);
    private readonly Actor _admin;

    public OrganizationServiceTests()
    {
        var admin = _h.AddUserAsync("admin.test", RoleCode.ADMIN, null).GetAwaiter().GetResult();
        _admin = new Actor(admin.Id, RoleCode.ADMIN, null, "203.0.113.5");
    }

    public void Dispose() => _h.Dispose();

    private static CreateOrganizationRequest NewOrg(string suffix = "a") => new(
        $"Trường Tiểu học Lê Lợi {suffix}", "12 Lê Lợi, Quận 1, TP. Hồ Chí Minh", "028 3822 1234", $"lienhe.{suffix}@truong.edu.vn",
        new NewAccountRequest($"Trần Thị Hạnh {suffix}", $"hanh.{suffix}", $"hanh.{suffix}@truong.edu.vn", "0912 345 678"));

    private Task<Created<OrganizationDetailDto>> CreateOrg(string suffix = "a") =>
        _h.Service<OrganizationService, Created<OrganizationDetailDto>>(s => s.CreateAsync(NewOrg(suffix), _admin, default));

    private static NewAccountRequest NewTeacher(string username) =>
        new("Giáo viên " + username, username, username + "@truong.edu.vn", null);

    private static Actor OrgAdminOf(OrganizationDetailDto org) =>
        new(org.OrgAdmins.Single().Id, RoleCode.ORG_ADMIN, org.Id, "203.0.113.6");

    [Fact]
    public async Task Creating_an_organization_adds_free_plan_org_admin_48h_link_and_audit_entries()
    {
        var created = await CreateOrg();
        var org = created.Data;

        Assert.Equal("hanh.a@truong.edu.vn", created.NotifiedEmail);
        Assert.Equal(OrganizationState.ACTIVE, org.Status);
        Assert.Equal(Plan.FreePlanName, org.Subscription!.PlanName);
        Assert.Equal(SubscriptionStatus.ACTIVE, org.Subscription.Status);
        Assert.True(org.Subscription.IsEffective);
        Assert.Equal(new DateOnly(2026, 9, 29), org.Subscription.StartDate);        // hôm nay theo giờ Việt Nam
        Assert.Equal(new DateOnly(2026, 10, 28), org.Subscription.EndDate);         // 30 ngày
        Assert.Equal(new TeacherQuotaDto(0, 2, Plan.FreePlanName, true), org.TeacherQuota);

        var orgAdmin = Assert.Single(org.OrgAdmins);
        Assert.Equal(("hanh.a", RoleCode.ORG_ADMIN, UserStatus.ACTIVE), (orgAdmin.Username, orgAdmin.Role, orgAdmin.Status));

        var mail = _h.Mail.LastTo("hanh.a@truong.edu.vn")!.Value;
        Assert.Contains("48 giờ", mail.Body);
        Assert.Contains("Tên đăng nhập: hanh.a", mail.Body);
        Assert.Contains("Trường Tiểu học Lê Lợi a", mail.Body);

        var actions = await _h.QueryAsync(db => db.AuditLogs.OrderBy(l => l.Id).Select(l => new { l.Action, l.TargetType, l.UserId, l.ActorRole }).ToListAsync());
        Assert.Equal([AuditAction.ORG_CREATED, AuditAction.ACCOUNT_CREATED], actions.Select(a => a.Action));
        Assert.All(actions, a => Assert.Equal((_admin.UserId, ActorRole.ADMIN), (a.UserId!.Value, a.ActorRole)));
        Assert.Equal([AuditTargets.Organizations, AuditTargets.Users], actions.Select(a => a.TargetType));
    }

    [Fact]
    public async Task First_password_link_lasts_48_hours()
    {
        await CreateOrg();
        var token = _h.Mail.LastTokenFor("hanh.a@truong.edu.vn")!;

        _h.Clock.Advance(TimeSpan.FromHours(47));
        var info = await _h.Service<AuthService, PasswordTokenInfoDto>(s => s.CheckPasswordTokenAsync(new PasswordTokenRequest(token), default));
        Assert.Equal("hanh.a", info.Username);

        _h.Clock.Advance(TimeSpan.FromHours(2));
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            _h.Service<AuthService>(s => s.SetPasswordAsync(new SetPasswordRequest(token, "MatKhau2026"), default)));
        Assert.Equal("PASSWORD_LINK_INVALID", ex.AppMessage.Code);
    }

    [Fact]
    public async Task Duplicate_org_admin_email_rolls_back_the_whole_organization()
    {
        await CreateOrg("a");
        var dup = NewOrg("b") with { OrgAdmin = new NewAccountRequest("Người khác", "nguoi.khac", "HANH.A@truong.edu.vn", null) };

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            _h.Service<OrganizationService, Created<OrganizationDetailDto>>(s => s.CreateAsync(dup, _admin, default)));

        Assert.Equal((409, "DUPLICATE"), (ex.StatusCode, ex.AppMessage.Code));
        Assert.Equal("Email \"hanh.a@truong.edu.vn\" đã được sử dụng cho một tài khoản khác.", ex.Errors!["orgAdmin.email"]);
        Assert.Equal(1, await _h.QueryAsync(db => db.Organizations.CountAsync()));
        Assert.Equal(1, await _h.QueryAsync(db => db.Subscriptions.CountAsync()));
    }

    [Fact]
    public async Task Teacher_quota_counts_teachers_not_soft_deleted_in_any_status_but_not_org_admins()
    {
        var org = (await CreateOrg()).Data;
        await _h.AddUserAsync("gv.khoa", RoleCode.TEACHER, org.Id, status: UserStatus.LOCKED);
        var deleted = await _h.AddUserAsync("gv.daxoa", RoleCode.TEACHER, org.Id);
        await _h.QueryAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == deleted.Id)).DeletedAt = _h.Clock.GetUtcNow().UtcDateTime;
            return await db.SaveChangesAsync();
        });
        var other = await _h.AddOrganizationAsync("Trường khác");
        await _h.AddUserAsync("gv.truongkhac", RoleCode.TEACHER, other.Id);

        var quota = await _h.Service<PlanGuard, TeacherQuotaDto>(g => g.GetTeacherQuotaAsync(org.Id, default));
        Assert.Equal(new TeacherQuotaDto(1, 2, Plan.FreePlanName, true), quota);
    }

    [Fact]
    public async Task Creating_teachers_stops_at_max_teachers_with_teacher_limit_reached()
    {
        var org = (await CreateOrg()).Data;
        var orgAdmin = OrgAdminOf(org);

        await _h.Service<TeacherService, Created<AccountDto>>(s => s.CreateAsync(orgAdmin, NewTeacher("gv.mot"), default));
        await _h.Service<TeacherService, Created<AccountDto>>(s => s.CreateAsync(orgAdmin, NewTeacher("gv.hai"), default));
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            _h.Service<TeacherService, Created<AccountDto>>(s => s.CreateAsync(orgAdmin, NewTeacher("gv.ba"), default)));

        Assert.Equal((422, "TEACHER_LIMIT_REACHED"), (ex.StatusCode, ex.AppMessage.Code));
        Assert.Equal("Trường đã dùng 2/2 tài khoản giáo viên của gói Miễn phí. " +
                     "Hãy nâng cấp gói hoặc xóa tài khoản không còn dùng để thêm giáo viên.", ex.Message);

        // Xóa mềm một giáo viên thì thêm được, cùng email/username với người đã xóa.
        var first = await _h.QueryAsync(db => db.Users.SingleAsync(u => u.Username == "gv.mot"));
        await _h.Service<TeacherService>(s => s.DeleteAsync(orgAdmin, first.Id, default));
        var again = await _h.Service<TeacherService, Created<AccountDto>>(s => s.CreateAsync(orgAdmin, NewTeacher("gv.mot"), default));
        Assert.NotEqual(first.Id, again.Data.Id);
    }

    [Fact]
    public async Task Creating_a_teacher_without_an_effective_plan_is_blocked()
    {
        var org = (await CreateOrg()).Data;
        var orgAdmin = OrgAdminOf(org);
        _h.Clock.Advance(TimeSpan.FromDays(30));                               // gói Miễn phí 30 ngày đã hết

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            _h.Service<TeacherService, Created<AccountDto>>(s => s.CreateAsync(orgAdmin, NewTeacher("gv.mot"), default)));
        Assert.Equal((422, "BLOCKED"), (ex.StatusCode, ex.AppMessage.Code));
        Assert.Equal("Không thể thêm giáo viên. Trường chưa có gói dịch vụ còn hiệu lực.", ex.Message);
    }

    [Fact]
    public async Task Org_admin_cannot_reach_teachers_of_another_organization()
    {
        var a = (await CreateOrg("a")).Data;
        var b = (await CreateOrg("b")).Data;
        var teacherOfB = await _h.Service<TeacherService, Created<AccountDto>>(s =>
            s.CreateAsync(new Actor(b.OrgAdmins.Single().Id, RoleCode.ORG_ADMIN, b.Id, null), NewTeacher("gv.truongb"), default));
        var orgAdminOfA = OrgAdminOf(a);

        var get = await Assert.ThrowsAsync<AppException>(() =>
            _h.Service<TeacherService, AccountDto>(s => s.GetAsync(orgAdminOfA, teacherOfB.Data.Id, default)));
        var lockIt = await Assert.ThrowsAsync<AppException>(() =>
            _h.Service<TeacherService, AccountDto>(s => s.LockAsync(orgAdminOfA, teacherOfB.Data.Id, default)));
        var list = await _h.Service<TeacherService, TeacherListDto>(s => s.ListAsync(orgAdminOfA, new TeacherListQuery(null, null, null, null), default));

        Assert.Equal(404, get.StatusCode);
        Assert.Equal(404, lockIt.StatusCode);
        Assert.Empty(list.Items);
        var status = await _h.QueryAsync(db => db.Users.Where(u => u.Id == teacherOfB.Data.Id).Select(u => u.Status).SingleAsync());
        Assert.Equal(UserStatus.ACTIVE, status);
    }

    [Fact]
    public async Task Lock_unlock_delete_teacher_are_audited_and_revoke_unused_links()
    {
        var org = (await CreateOrg()).Data;
        var orgAdmin = OrgAdminOf(org);
        var teacher = (await _h.Service<TeacherService, Created<AccountDto>>(s => s.CreateAsync(orgAdmin, NewTeacher("gv.mot"), default))).Data;
        var link = _h.Mail.LastTokenFor("gv.mot@truong.edu.vn")!;

        var locked = await _h.Service<TeacherService, AccountDto>(s => s.LockAsync(orgAdmin, teacher.Id, default));
        Assert.Equal(UserStatus.LOCKED, locked.Status);
        var twice = await Assert.ThrowsAsync<AppException>(() => _h.Service<TeacherService, AccountDto>(s => s.LockAsync(orgAdmin, teacher.Id, default)));
        Assert.Equal("Không thể khóa tài khoản. Tài khoản đang bị khóa.", twice.Message);

        var unlocked = await _h.Service<TeacherService, AccountDto>(s => s.UnlockAsync(orgAdmin, teacher.Id, default));
        Assert.Equal(UserStatus.ACTIVE, unlocked.Status);
        var linkAfterLock = await Assert.ThrowsAsync<AppException>(() =>
            _h.Service<AuthService>(s => s.SetPasswordAsync(new SetPasswordRequest(link, "MatKhau2026"), default)));
        Assert.Equal("PASSWORD_LINK_INVALID", linkAfterLock.AppMessage.Code);

        await _h.Service<TeacherService>(s => s.DeleteAsync(orgAdmin, teacher.Id, default));
        var logs = await _h.QueryAsync(db => db.AuditLogs.Where(l => l.TargetId == teacher.Id && l.TargetType == AuditTargets.Users)
            .OrderBy(l => l.Id).Select(l => new { l.Action, l.OldValue, l.NewValue, l.ActorRole }).ToListAsync());
        Assert.Equal([AuditAction.ACCOUNT_CREATED, AuditAction.ACCOUNT_LOCKED, AuditAction.ACCOUNT_UNLOCKED, AuditAction.ACCOUNT_DELETED],
            logs.Select(l => l.Action));
        Assert.Equal("{\"status\":\"ACTIVE\"}", logs[1].OldValue);
        Assert.Equal("{\"status\":\"LOCKED\"}", logs[1].NewValue);
        Assert.All(logs, l => Assert.Equal(ActorRole.ORG_ADMIN, l.ActorRole));
    }

    [Fact]
    public async Task Deactivating_an_organization_is_audited_and_blocks_further_changes()
    {
        var org = (await CreateOrg()).Data;
        var link = _h.Mail.LastTokenFor("hanh.a@truong.edu.vn")!;

        await _h.Service<OrganizationService>(s => s.DeactivateAsync(org.Id, _admin, default));

        var detail = await _h.Service<OrganizationService, OrganizationDetailDto>(s => s.GetAsync(org.Id, default));
        Assert.Equal(OrganizationState.DEACTIVATED, detail.Status);
        var again = await Assert.ThrowsAsync<AppException>(() => _h.Service<OrganizationService>(s => s.DeactivateAsync(org.Id, _admin, default)));
        Assert.Equal((422, "Không thể ngừng trường. Trường đã ngừng sử dụng nền tảng."), (again.StatusCode, again.Message));
        var linkAfter = await Assert.ThrowsAsync<AppException>(() =>
            _h.Service<AuthService>(s => s.SetPasswordAsync(new SetPasswordRequest(link, "MatKhau2026"), default)));
        Assert.Equal("PASSWORD_LINK_INVALID", linkAfter.AppMessage.Code);
        Assert.True(await _h.QueryAsync(db => db.AuditLogs.AnyAsync(l => l.Action == AuditAction.ORG_DEACTIVATED && l.TargetId == org.Id)));
    }

    [Fact]
    public async Task Admin_lists_organizations_with_plan_teacher_count_and_status()
    {
        var a = (await CreateOrg("a")).Data;
        await CreateOrg("b");
        await _h.AddUserAsync("gv.a1", RoleCode.TEACHER, a.Id);
        await _h.Service<OrganizationService>(s => s.DeactivateAsync(a.Id, _admin, default));

        var all = await _h.Service<OrganizationService, PagedResult<OrganizationSummaryDto>>(s =>
            s.ListAsync(new OrganizationListQuery(null, null, 1, 20), default));
        var active = await _h.Service<OrganizationService, PagedResult<OrganizationSummaryDto>>(s =>
            s.ListAsync(new OrganizationListQuery("Lê Lợi", OrganizationState.ACTIVE, null, null), default));

        Assert.Equal(2, all.TotalItems);
        var rowA = all.Items.Single(o => o.Id == a.Id);
        Assert.Equal((OrganizationState.DEACTIVATED, 1, Plan.FreePlanName, true), (rowA.Status, rowA.TeacherCount, rowA.PlanName, rowA.HasEffectivePlan));
        Assert.Equal("Trường Tiểu học Lê Lợi b", Assert.Single(active.Items).Name);
    }
}
