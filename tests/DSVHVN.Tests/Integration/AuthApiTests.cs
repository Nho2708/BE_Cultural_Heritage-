using System.Net;
using DSVHVN.Application.Auth;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Tests.Integration;

/// <summary>Đăng nhập, đặt mật khẩu và hồ sơ qua HTTP: route, mã trạng thái, khuôn phản hồi chung và thông điệp tiếng Việt.</summary>
public sealed class AuthApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly PlatformClient _api = new(factory);
    private HttpClient Http => _api.Http;

    [Fact]
    public async Task Admin_signs_in_with_username_or_email_and_reads_the_profile()
    {
        var (res, body) = await Http.PostAsync<LoginResultDto>("/api/v1/auth/login",
            new { emailOrUsername = "ADMIN.TEST", password = ApiFactory.AdminPassword });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(body.Success);
        Assert.Equal("Đăng nhập thành công.", body.Message);
        Assert.Equal("Bearer", body.Data!.TokenType);
        Assert.Equal((RoleCode.ADMIN, "Quản trị hệ thống"), (body.Data.User.Role, body.Data.User.RoleName));
        Assert.Null(body.Data.User.Organization);
        Assert.False(string.IsNullOrEmpty(body.TraceId));

        var byEmail = await _api.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var (me, meBody) = await Http.GetAsync<ProfileDto>("/api/v1/me", byEmail);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(("admin.test", "Quản trị kiểm thử"), (meBody.Data!.Username, meBody.Data.FullName));
    }

    [Fact]
    public async Task Wrong_password_returns_401_invalid_credentials_and_empty_fields_return_required()
    {
        var (wrong, wrongBody) = await Http.PostAsync<object>("/api/v1/auth/login",
            new { emailOrUsername = "admin.test", password = "SaiMatKhau1" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(("INVALID_CREDENTIALS", "Email, tên đăng nhập hoặc mật khẩu không đúng."), (wrongBody.ErrorCode, wrongBody.Message));

        var (empty, emptyBody) = await Http.PostAsync<object>("/api/v1/auth/login", new { emailOrUsername = " ", password = "" });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("REQUIRED", emptyBody.ErrorCode);
        Assert.Equal("Vui lòng nhập email hoặc tên đăng nhập.", emptyBody.Errors!["emailOrUsername"]);
        Assert.Equal("Vui lòng nhập mật khẩu.", emptyBody.Errors["password"]);
    }

    [Fact]
    public async Task Malformed_json_returns_400_invalid_envelope()
    {
        using var content = new StringContent("{\"emailOrUsername\": ", System.Text.Encoding.UTF8, "application/json");
        var res = await Http.PostAsync("/api/v1/auth/login", content);
        var body = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<Envelope<object>>(res.Content, HttpTestExtensions.Json);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("INVALID", body!.ErrorCode);
    }

    [Fact]
    public async Task Protected_endpoint_without_token_returns_401_forbidden()
    {
        var (res, body) = await Http.GetAsync<object>("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(("FORBIDDEN", "Bạn không có quyền thực hiện thao tác này."), (body.ErrorCode, body.Message));
    }

    [Fact]
    public async Task New_account_checks_link_sets_password_once_then_signs_in()
    {
        var suffix = PlatformClient.Unique();
        await _api.CreateOrgAsync(await _api.AdminTokenAsync(), suffix);
        var email = $"thu.{suffix}@nguyendu.edu.vn";
        var token = factory.Mail.LastTokenFor(email)!;

        var (check, checkBody) = await Http.PostAsync<PasswordTokenInfoDto>("/api/v1/auth/password-token/check", new { token });
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        Assert.Equal($"thu.{suffix}", checkBody.Data!.Username);

        var (set, setBody) = await Http.PostAsync<object>("/api/v1/auth/set-password", new { token, newPassword = PlatformClient.Password });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("Đặt mật khẩu thành công.", setBody.Message);

        var (reuse, reuseBody) = await Http.PostAsync<object>("/api/v1/auth/set-password", new { token, newPassword = "MatKhauKhac2026" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reuse.StatusCode);
        Assert.Equal("PASSWORD_LINK_INVALID", reuseBody.ErrorCode);

        var orgAdmin = await _api.LoginAsync(email);
        var (me, meBody) = await Http.GetAsync<ProfileDto>("/api/v1/me", orgAdmin);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(RoleCode.ORG_ADMIN, meBody.Data!.Role);
        Assert.Equal($"Trường THCS Nguyễn Du {suffix}", meBody.Data.Organization!.Name);
    }

    [Fact]
    public async Task Forgot_password_always_returns_the_same_message()
    {
        foreach (var email in new[] { ApiFactory.AdminEmail, "khongai@example.vn" })
        {
            var (res, body) = await Http.PostAsync<object>("/api/v1/auth/forgot-password", new { email });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("Nếu email này thuộc một tài khoản, liên kết đặt lại mật khẩu sẽ được gửi tới hộp thư. " +
                         "Liên kết có hiệu lực 30 phút.", body.Message);
        }
        Assert.NotNull(factory.Mail.LastTokenFor(ApiFactory.AdminEmail));
        Assert.Null(factory.Mail.LastTokenFor("khongai@example.vn"));
    }

    [Fact]
    public async Task Changing_password_invalidates_old_tokens_and_returns_a_new_one()
    {
        var (_, orgAdminToken) = await _api.OrgWithAdminAsync();
        var (teacher, oldToken) = await _api.TeacherAsync(orgAdminToken);

        var (res, body) = await Http.PutAsync<AccessTokenDto>("/api/v1/me/password",
            new { currentPassword = PlatformClient.Password, newPassword = "MatKhauMoi2026" }, oldToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Đổi mật khẩu thành công.", body.Message);

        var (old, _) = await Http.GetAsync<object>("/api/v1/me", oldToken);
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        var (fresh, freshBody) = await Http.GetAsync<ProfileDto>("/api/v1/me", body.Data!.AccessToken);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(teacher.Id, freshBody.Data!.Id);
    }

    [Fact]
    public async Task Profile_update_keeps_vietnamese_text_and_validates_phone_and_avatar()
    {
        var (_, orgAdminToken) = await _api.OrgWithAdminAsync();

        var (ok, okBody) = await Http.PutAsync<ProfileDto>("/api/v1/me",
            new { fullName = "Nguyễn Thị Ánh Tuyết", phone = "+84 912 345 678", avatarUrl = "https://anh.example.vn/tuyet.png" }, orgAdminToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Lưu hồ sơ thành công.", okBody.Message);
        Assert.Equal(("Nguyễn Thị Ánh Tuyết", "+84 912 345 678"), (okBody.Data!.FullName, okBody.Data.Phone));

        var (bad, badBody) = await Http.PutAsync<object>("/api/v1/me",
            new { fullName = "A", phone = "abc", avatarUrl = "javascript:alert(1)" }, orgAdminToken);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("INVALID", badBody.ErrorCode);
        Assert.True(badBody.Errors!.ContainsKey("phone"));
        Assert.True(badBody.Errors.ContainsKey("avatarUrl"));
    }
}
