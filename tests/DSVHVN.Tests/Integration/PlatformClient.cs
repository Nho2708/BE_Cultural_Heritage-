using System.Net;
using DSVHVN.Application.Accounts;
using DSVHVN.Application.Auth;
using DSVHVN.Application.Organizations;

namespace DSVHVN.Tests.Integration;

/// <summary>Các bước lặp lại của luồng nền tảng qua HTTP: đăng nhập, tạo trường, kích hoạt tài khoản bằng liên kết trong thư.</summary>
public sealed class PlatformClient(ApiFactory factory)
{
    public const string Password = "MatKhau2026";

    public HttpClient Http { get; } = factory.CreateClient();
    public ApiFactory Factory { get; } = factory;

    public static string Unique() => Guid.NewGuid().ToString("N")[..8];

    public async Task<string> LoginAsync(string emailOrUsername, string password = Password)
    {
        var (res, body) = await Http.PostAsync<LoginResultDto>("/api/v1/auth/login", new { emailOrUsername, password });
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"Đăng nhập {emailOrUsername}: {(int)res.StatusCode} {body.Message}");
        return body.Data!.AccessToken;
    }

    public Task<string> AdminTokenAsync() => LoginAsync(ApiFactory.AdminUsername, ApiFactory.AdminPassword);

    /// <summary>Đặt mật khẩu lần đầu bằng token trong thư mới nhất gửi tới <paramref name="email"/>.</summary>
    public async Task ActivateAsync(string email, string password = Password)
    {
        var token = Factory.Mail.LastTokenFor(email) ?? throw new InvalidOperationException($"Không có thư tới {email}");
        var (res, body) = await Http.PostAsync<object>("/api/v1/auth/set-password", new { token, newPassword = password });
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"Đặt mật khẩu {email}: {(int)res.StatusCode} {body.Message}");
    }

    public static object NewOrgBody(string suffix) => new
    {
        name = $"Trường THCS Nguyễn Du {suffix}",
        address = "25 Nguyễn Du, Quận 1, TP. Hồ Chí Minh",
        phone = "028 3829 0000",
        email = $"vanthu.{suffix}@nguyendu.edu.vn",
        orgAdmin = new
        {
            fullName = $"Lê Thị Thu {suffix}",
            username = $"thu.{suffix}",
            email = $"thu.{suffix}@nguyendu.edu.vn",
            phone = "0903 111 222",
        },
    };

    public async Task<OrganizationDetailDto> CreateOrgAsync(string adminToken, string suffix)
    {
        var (res, body) = await Http.PostAsync<OrganizationDetailDto>("/api/v1/admin/organizations", NewOrgBody(suffix), adminToken);
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"Tạo trường: {(int)res.StatusCode} {body.Message}");
        return body.Data!;
    }

    /// <summary>Trường mới + quản trị trường đã đặt mật khẩu và đăng nhập.</summary>
    public async Task<(OrganizationDetailDto Org, string OrgAdminToken)> OrgWithAdminAsync(string? suffix = null)
    {
        suffix ??= Unique();
        var org = await CreateOrgAsync(await AdminTokenAsync(), suffix);
        await ActivateAsync($"thu.{suffix}@nguyendu.edu.vn");
        return (org, await LoginAsync($"thu.{suffix}"));
    }

    public async Task<AccountDto> CreateTeacherAsync(string orgAdminToken, string username)
    {
        var (res, body) = await Http.PostAsync<AccountDto>("/api/v1/org/teachers", new
        {
            fullName = "Phạm Văn Bình " + username,
            username,
            email = username + "@nguyendu.edu.vn",
        }, orgAdminToken);
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"Tạo giáo viên {username}: {(int)res.StatusCode} {body.Message}");
        return body.Data!;
    }

    /// <summary>Giáo viên đã đặt mật khẩu và đăng nhập.</summary>
    public async Task<(AccountDto Teacher, string Token)> TeacherAsync(string orgAdminToken, string? username = null)
    {
        username ??= "gv." + Unique();
        var teacher = await CreateTeacherAsync(orgAdminToken, username);
        await ActivateAsync(username + "@nguyendu.edu.vn");
        return (teacher, await LoginAsync(username));
    }
}
