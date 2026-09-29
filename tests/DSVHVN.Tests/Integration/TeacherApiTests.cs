using System.Net;
using DSVHVN.Application.Accounts;
using DSVHVN.Application.Auth;
using DSVHVN.Application.Teachers;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Tests.Integration;

/// <summary>Quản lý giáo viên qua HTTP: tạo giáo viên trong max_teachers, khóa, mở, xóa mềm; tách dữ liệu giữa hai trường.</summary>
public sealed class TeacherApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly PlatformClient _api = new(factory);
    private HttpClient Http => _api.Http;

    [Fact]
    public async Task Org_admin_creates_teachers_up_to_max_teachers_then_gets_teacher_limit_reached()
    {
        var (_, orgAdmin) = await _api.OrgWithAdminAsync();
        var suffix = PlatformClient.Unique();

        var (first, firstBody) = await Http.PostAsync<AccountDto>("/api/v1/org/teachers",
            new { fullName = "Đặng Thị Mỹ Linh", username = $"linh.{suffix}", email = $"linh.{suffix}@nguyendu.edu.vn", phone = "0912345678" }, orgAdmin);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal($"Đã gửi email đặt mật khẩu tới linh.{suffix}@nguyendu.edu.vn. Liên kết có hiệu lực 48 giờ.", firstBody.Message);
        Assert.Equal((RoleCode.TEACHER, "Đặng Thị Mỹ Linh"), (firstBody.Data!.Role, firstBody.Data.FullName));

        await _api.CreateTeacherAsync(orgAdmin, $"gv2.{suffix}");
        var (third, thirdBody) = await Http.PostAsync<object>("/api/v1/org/teachers",
            new { fullName = "Giáo viên thứ ba", username = $"gv3.{suffix}", email = $"gv3.{suffix}@nguyendu.edu.vn" }, orgAdmin);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, third.StatusCode);
        Assert.Equal("TEACHER_LIMIT_REACHED", thirdBody.ErrorCode);

        var (list, listBody) = await Http.GetAsync<TeacherListDto>("/api/v1/org/teachers?keyword=Linh", orgAdmin);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal($"linh.{suffix}", Assert.Single(listBody.Data!.Items).Username);
        Assert.Equal((2, 2, false), (listBody.Data.Quota.Used, listBody.Data.Quota.Max!.Value, listBody.Data.Quota.CanAddTeacher));
    }

    [Fact]
    public async Task Teacher_signs_in_but_cannot_use_org_admin_or_admin_endpoints()
    {
        var (org, orgAdmin) = await _api.OrgWithAdminAsync();
        var (teacher, token) = await _api.TeacherAsync(orgAdmin);

        var (me, meBody) = await Http.GetAsync<ProfileDto>("/api/v1/me", token);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal((teacher.Id, RoleCode.TEACHER, org.Id), (meBody.Data!.Id, meBody.Data.Role, meBody.Data.Organization!.Id));

        foreach (var url in new[] { "/api/v1/org", "/api/v1/org/teachers", "/api/v1/admin/organizations", "/api/v1/admin/audit-logs" })
        {
            var (res, body) = await Http.GetAsync<object>(url, token);
            Assert.True(res.StatusCode == HttpStatusCode.Forbidden, url);
            Assert.Equal("FORBIDDEN", body.ErrorCode);
        }
    }

    [Fact]
    public async Task Org_admin_of_school_A_gets_404_for_teachers_of_school_B()
    {
        var (_, orgAdminA) = await _api.OrgWithAdminAsync();
        var (_, orgAdminB) = await _api.OrgWithAdminAsync();
        var teacherOfB = await _api.CreateTeacherAsync(orgAdminB, "gv.b." + PlatformClient.Unique());

        var (get, getBody) = await Http.GetAsync<object>($"/api/v1/org/teachers/{teacherOfB.Id}", orgAdminA);
        var (lk, _) = await Http.PostAsync<object>($"/api/v1/org/teachers/{teacherOfB.Id}/lock", null, orgAdminA);
        var (del, _) = await Http.DeleteAsync<object>($"/api/v1/org/teachers/{teacherOfB.Id}", orgAdminA);
        var (list, listBody) = await Http.GetAsync<TeacherListDto>("/api/v1/org/teachers", orgAdminA);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal("Không thể xem tài khoản. Không tìm thấy giáo viên trong trường của bạn.", getBody.Message);
        Assert.Equal(HttpStatusCode.NotFound, lk.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
        Assert.DoesNotContain(listBody.Data!.Items, t => t.Id == teacherOfB.Id);

        var (stillThere, stillBody) = await Http.GetAsync<AccountDto>($"/api/v1/org/teachers/{teacherOfB.Id}", orgAdminB);
        Assert.Equal((HttpStatusCode.OK, UserStatus.ACTIVE), (stillThere.StatusCode, stillBody.Data!.Status));
    }

    [Fact]
    public async Task Locking_a_teacher_takes_effect_on_the_next_request_and_unlocking_restores_access()
    {
        var (_, orgAdmin) = await _api.OrgWithAdminAsync();
        var (teacher, token) = await _api.TeacherAsync(orgAdmin);

        var (lk, lkBody) = await Http.PostAsync<AccountDto>($"/api/v1/org/teachers/{teacher.Id}/lock", null, orgAdmin);
        Assert.Equal((HttpStatusCode.OK, UserStatus.LOCKED), (lk.StatusCode, lkBody.Data!.Status));

        var (me, meBody) = await Http.GetAsync<object>("/api/v1/me", token);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal("ACCOUNT_BLOCKED", meBody.ErrorCode);
        var (login, _) = await Http.PostAsync<object>("/api/v1/auth/login", new { emailOrUsername = teacher.Username, password = PlatformClient.Password });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);

        var (ul, ulBody) = await Http.PostAsync<AccountDto>($"/api/v1/org/teachers/{teacher.Id}/unlock", null, orgAdmin);
        Assert.Equal(("Mở khóa tài khoản thành công.", UserStatus.ACTIVE), (ulBody.Message, ulBody.Data!.Status));
        Assert.Equal(HttpStatusCode.OK, ul.StatusCode);
        var (again, _) = await Http.GetAsync<object>("/api/v1/me", token);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Deleted_teacher_cannot_sign_in_and_the_email_can_be_reused()
    {
        var (_, orgAdmin) = await _api.OrgWithAdminAsync();
        var username = "gv.xoa." + PlatformClient.Unique();
        var (teacher, token) = await _api.TeacherAsync(orgAdmin, username);

        var (del, delBody) = await Http.DeleteAsync<object>($"/api/v1/org/teachers/{teacher.Id}", orgAdmin);
        Assert.Equal((HttpStatusCode.OK, "Xóa tài khoản giáo viên thành công."), (del.StatusCode, delBody.Message));

        var (me, _) = await Http.GetAsync<object>("/api/v1/me", token);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        var (login, loginBody) = await Http.PostAsync<object>("/api/v1/auth/login", new { emailOrUsername = username, password = PlatformClient.Password });
        Assert.Equal((HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS"), (login.StatusCode, loginBody.ErrorCode));

        var again = await _api.CreateTeacherAsync(orgAdmin, username);
        Assert.NotEqual(teacher.Id, again.Id);
    }
}
