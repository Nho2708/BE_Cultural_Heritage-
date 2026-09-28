using System.Net;
using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Tests.Integration;

/// <summary>Nhật ký thao tác qua HTTP: các thao tác nền tảng sinh đúng mã hành động; chỉ ADMIN xem, lọc được.</summary>
public sealed class AuditLogApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly PlatformClient _api = new(factory);
    private HttpClient Http => _api.Http;

    [Fact]
    public async Task Platform_actions_are_logged_with_actor_role_ip_and_vietnamese_labels()
    {
        var (org, orgAdmin) = await _api.OrgWithAdminAsync();
        var (teacher, _) = await _api.TeacherAsync(orgAdmin);
        await Http.PostAsync<object>($"/api/v1/org/teachers/{teacher.Id}/lock", null, orgAdmin);
        var admin = await _api.AdminTokenAsync();

        var (created, createdBody) = await Http.GetAsync<PagedResult<AuditLogDto>>(
            $"/api/v1/admin/audit-logs?action=ORG_CREATED&targetType=organizations&targetId={org.Id}", admin);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var orgCreated = Assert.Single(createdBody.Data!.Items);
        Assert.Equal(("Tạo trường", ActorRole.ADMIN, "admin.test", "203.0.113.10"),
            (orgCreated.ActionLabel, orgCreated.ActorRole, orgCreated.Username, orgCreated.IpAddress));
        Assert.Contains("\"plan\":\"Miễn phí\"", orgCreated.NewValue);

        var (byTeacher, byTeacherBody) = await Http.GetAsync<PagedResult<AuditLogDto>>(
            $"/api/v1/admin/audit-logs?targetType=users&targetId={teacher.Id}", admin);
        Assert.Equal(HttpStatusCode.OK, byTeacher.StatusCode);
        Assert.Equal([AuditAction.ACCOUNT_LOCKED, AuditAction.LOGIN, AuditAction.ACCOUNT_CREATED],
            byTeacherBody.Data!.Items.Select(l => l.Action));                             // mới nhất trước
        Assert.Equal(ActorRole.ORG_ADMIN, byTeacherBody.Data.Items[0].ActorRole);

        var (logins, loginsBody) = await Http.GetAsync<PagedResult<AuditLogDto>>(
            "/api/v1/admin/audit-logs?action=LOGIN&actorRole=ORG_ADMIN&pageSize=100", admin);
        Assert.Equal(HttpStatusCode.OK, logins.StatusCode);
        Assert.Contains(loginsBody.Data!.Items, l => l.UserId == org.OrgAdmins.Single().Id);
        Assert.All(loginsBody.Data.Items, l => Assert.Equal("Đăng nhập web quản trị", l.ActionLabel));
    }

    [Fact]
    public async Task Only_admin_reads_the_log_and_bad_filters_return_invalid()
    {
        var (_, orgAdmin) = await _api.OrgWithAdminAsync();
        var (forbidden, _) = await Http.GetAsync<object>("/api/v1/admin/audit-logs", orgAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var (bad, badBody) = await Http.GetAsync<object>("/api/v1/admin/audit-logs?action=XOA_HET", await _api.AdminTokenAsync());
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("INVALID", badBody.ErrorCode);
    }
}
