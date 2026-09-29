using System.Net;
using DSVHVN.Application.Accounts;
using DSVHVN.Application.Common;
using DSVHVN.Application.Organizations;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Tests.Integration;

/// <summary>Quản lý trường qua HTTP: ADMIN tạo, xem, sửa, ngừng trường; thêm, khóa, mở quản trị trường; ORG_ADMIN sửa thông tin liên hệ.</summary>
public sealed class OrganizationApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly PlatformClient _api = new(factory);
    private HttpClient Http => _api.Http;

    [Fact]
    public async Task Admin_creates_an_organization_with_free_plan_and_first_org_admin()
    {
        var admin = await _api.AdminTokenAsync();
        var suffix = PlatformClient.Unique();

        var (res, body) = await Http.PostAsync<OrganizationDetailDto>("/api/v1/admin/organizations", PlatformClient.NewOrgBody(suffix), admin);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal($"Đã gửi email đặt mật khẩu tới thu.{suffix}@nguyendu.edu.vn. Liên kết có hiệu lực 48 giờ.", body.Message);
        var org = body.Data!;
        Assert.Equal(($"Trường THCS Nguyễn Du {suffix}", "25 Nguyễn Du, Quận 1, TP. Hồ Chí Minh"), (org.Name, org.Address));
        Assert.Equal(("Miễn phí", SubscriptionStatus.ACTIVE, true), (org.Subscription!.PlanName, org.Subscription.Status, org.Subscription.IsEffective));
        Assert.Equal((0, 2), (org.TeacherQuota.Used, org.TeacherQuota.Max!.Value));
        Assert.Equal(($"thu.{suffix}", UserStatus.ACTIVE), (org.OrgAdmins.Single().Username, org.OrgAdmins.Single().Status));

        var (list, listBody) = await Http.GetAsync<PagedResult<OrganizationSummaryDto>>(
            $"/api/v1/admin/organizations?keyword={suffix}&status=ACTIVE&page=1&pageSize=10", admin);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var row = Assert.Single(listBody.Data!.Items);
        Assert.Equal((org.Id, "Miễn phí", 0, OrganizationState.ACTIVE), (row.Id, row.PlanName, row.TeacherCount, row.Status));

        var (get, getBody) = await Http.GetAsync<OrganizationDetailDto>($"/api/v1/admin/organizations/{org.Id}", admin);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(org.Name, getBody.Data!.Name);
    }

    [Fact]
    public async Task Invalid_or_duplicate_org_admin_fields_are_reported_under_orgAdmin()
    {
        var admin = await _api.AdminTokenAsync();

        var (missing, missingBody) = await Http.PostAsync<object>("/api/v1/admin/organizations", new { name = "Trường thiếu quản trị" }, admin);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("Vui lòng nhập thông tin tài khoản quản trị trường.", missingBody.Errors!["orgAdmin"]);

        var (bad, badBody) = await Http.PostAsync<object>("/api/v1/admin/organizations", new
        {
            name = "",
            orgAdmin = new { fullName = "Hà", username = "hà nội", email = "sai-email" },
        }, admin);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("Vui lòng nhập tên trường.", badBody.Errors!["name"]);
        Assert.StartsWith("Tên đăng nhập không hợp lệ", badBody.Errors["orgAdmin.username"]);
        Assert.StartsWith("Email không hợp lệ", badBody.Errors["orgAdmin.email"]);

        var suffix = PlatformClient.Unique();
        await _api.CreateOrgAsync(admin, suffix);
        var (dup, dupBody) = await Http.PostAsync<object>("/api/v1/admin/organizations", new
        {
            name = "Trường khác",
            orgAdmin = new { fullName = "Người khác", username = $"THU.{suffix}", email = $"khac.{suffix}@example.vn" },
        }, admin);
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal("DUPLICATE", dupBody.ErrorCode);
        Assert.Equal($"Tên đăng nhập \"thu.{suffix}\" đã được sử dụng cho một tài khoản khác.", dupBody.Errors!["orgAdmin.username"]);
    }

    [Fact]
    public async Task Admin_updates_and_deactivates_an_organization_which_blocks_its_accounts_immediately()
    {
        var admin = await _api.AdminTokenAsync();
        var (org, orgAdminToken) = await _api.OrgWithAdminAsync();

        var (upd, updBody) = await Http.PutAsync<OrganizationDetailDto>($"/api/v1/admin/organizations/{org.Id}",
            new { name = org.Name + " (cơ sở 2)", address = "Thủ Đức", phone = "", email = "" }, admin);
        Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        Assert.Equal(("Lưu thông tin trường thành công.", org.Name + " (cơ sở 2)", null), (updBody.Message, updBody.Data!.Name, updBody.Data.Phone));

        var (del, delBody) = await Http.DeleteAsync<object>($"/api/v1/admin/organizations/{org.Id}", admin);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal("Ngừng trường thành công.", delBody.Message);

        var (me, meBody) = await Http.GetAsync<object>("/api/v1/me", orgAdminToken);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal("Tài khoản không đăng nhập được. Trường của bạn đã ngừng sử dụng nền tảng.", meBody.Message);

        var (login, loginBody) = await Http.PostAsync<object>("/api/v1/auth/login",
            new { emailOrUsername = org.OrgAdmins.Single().Username, password = PlatformClient.Password });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("ACCOUNT_BLOCKED", loginBody.ErrorCode);

        var (again, againBody) = await Http.DeleteAsync<object>($"/api/v1/admin/organizations/{org.Id}", admin);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("BLOCKED", againBody.ErrorCode);
    }

    [Fact]
    public async Task Admin_adds_locks_and_unlocks_org_admins()
    {
        var admin = await _api.AdminTokenAsync();
        var (org, _) = await _api.OrgWithAdminAsync();
        var suffix = PlatformClient.Unique();

        var (add, addBody) = await Http.PostAsync<AccountDto>($"/api/v1/admin/organizations/{org.Id}/org-admins",
            new { fullName = "Võ Minh Khôi", username = $"khoi.{suffix}", email = $"khoi.{suffix}@nguyendu.edu.vn" }, admin);
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        Assert.Equal(RoleCode.ORG_ADMIN, addBody.Data!.Role);
        await _api.ActivateAsync($"khoi.{suffix}@nguyendu.edu.vn");
        var khoiToken = await _api.LoginAsync($"khoi.{suffix}");

        var (lk, lkBody) = await Http.PostAsync<AccountDto>($"/api/v1/admin/organizations/{org.Id}/org-admins/{addBody.Data.Id}/lock", null, admin);
        Assert.Equal(HttpStatusCode.OK, lk.StatusCode);
        Assert.Equal(("Khóa tài khoản thành công.", UserStatus.LOCKED), (lkBody.Message, lkBody.Data!.Status));

        var (blocked, blockedBody) = await Http.GetAsync<object>("/api/v1/org", khoiToken);
        Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);
        Assert.Equal("Tài khoản không đăng nhập được. Tài khoản đã bị khóa. Vui lòng liên hệ quản trị viên.", blockedBody.Message);

        var (ul, _) = await Http.PostAsync<AccountDto>($"/api/v1/admin/organizations/{org.Id}/org-admins/{addBody.Data.Id}/unlock", null, admin);
        Assert.Equal(HttpStatusCode.OK, ul.StatusCode);
        Assert.False(string.IsNullOrEmpty(await _api.LoginAsync($"khoi.{suffix}")));

        // Id tài khoản không phải ORG_ADMIN của đúng trường → 404.
        var (wrongOrg, _) = await Http.PostAsync<object>($"/api/v1/admin/organizations/{org.Id + 999}/org-admins/{addBody.Data.Id}/lock", null, admin);
        Assert.Equal(HttpStatusCode.NotFound, wrongOrg.StatusCode);
    }

    [Fact]
    public async Task Org_admin_edits_only_contact_info_of_own_organization()
    {
        var (org, orgAdminToken) = await _api.OrgWithAdminAsync();

        var (res, body) = await Http.PutAsync<MyOrganizationDto>("/api/v1/org",
            new { name = "Đổi tên không được", address = "180 Cao Lỗ, Quận 8", phone = "028 3850 5520", email = "LienHe@NguyenDu.edu.vn" }, orgAdminToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal((org.Name, "180 Cao Lỗ, Quận 8", "lienhe@nguyendu.edu.vn"), (body.Data!.Name, body.Data.Address, body.Data.Email));
        Assert.Equal(2, body.Data.TeacherQuota.Max);
    }

    [Fact]
    public async Task Admin_endpoints_reject_other_roles_and_unknown_ids()
    {
        var (_, orgAdminToken) = await _api.OrgWithAdminAsync();
        var admin = await _api.AdminTokenAsync();

        var (forbidden, forbiddenBody) = await Http.GetAsync<object>("/api/v1/admin/organizations", orgAdminToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("FORBIDDEN", forbiddenBody.ErrorCode);

        var (adminOnOrg, _) = await Http.GetAsync<object>("/api/v1/org", admin);
        Assert.Equal(HttpStatusCode.Forbidden, adminOnOrg.StatusCode);

        var (missing, missingBody) = await Http.GetAsync<object>("/api/v1/admin/organizations/987654", admin);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("Không thể xem trường. Không tìm thấy trường.", missingBody.Message);

        var (badPage, badPageBody) = await Http.GetAsync<object>("/api/v1/admin/organizations?pageSize=500", admin);
        Assert.Equal(HttpStatusCode.BadRequest, badPage.StatusCode);
        Assert.Equal("INVALID", badPageBody.ErrorCode);
    }
}
