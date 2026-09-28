using DSVHVN.Application.Auth;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Unit;

/// <summary>Đăng nhập, đặt mật khẩu qua email và đổi mật khẩu ở tầng service: quy tắc mật khẩu, chống dò mật khẩu, liên kết đặt mật khẩu, nhật ký thao tác.</summary>
public sealed class AuthServiceTests : IDisposable
{
    private const string Password = ServiceHarness.Password;
    private const string Ip = "198.51.100.7";
    private readonly ServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private Task<LoginResultDto> Login(string identifier, string password = Password) =>
        _h.Service<AuthService, LoginResultDto>(s => s.LoginAsync(new LoginRequest(identifier, password), Ip, default));

    private Task<AppException> LoginFails(string identifier, string password = Password) =>
        Assert.ThrowsAsync<AppException>(() => Login(identifier, password));

    private Task Forgot(string email) =>
        _h.Service<AuthService>(s => s.ForgotPasswordAsync(new ForgotPasswordRequest(email), default));

    private Task SetPassword(string token, string password) =>
        _h.Service<AuthService>(s => s.SetPasswordAsync(new SetPasswordRequest(token, password), default));

    private async Task<User> TeacherAsync(string username = "giaovien.a")
    {
        var org = await _h.AddOrganizationAsync();
        return await _h.AddUserAsync(username, RoleCode.TEACHER, org.Id);
    }

    [Fact]
    public async Task Login_accepts_email_or_username_ignoring_case_and_records_LOGIN()
    {
        var user = await TeacherAsync();

        var byUsername = await Login("GiaoVien.A");
        var byEmail = await Login("  GIAOVIEN.A@example.vn ");

        Assert.Equal("Bearer", byUsername.TokenType);
        Assert.Equal(user.Id, byEmail.User.Id);
        Assert.Equal(RoleCode.TEACHER, byEmail.User.Role);
        Assert.Equal("Trường THCS Thử Nghiệm", byEmail.User.Organization!.Name);
        Assert.Equal(_h.Clock.GetUtcNow().UtcDateTime.AddHours(8), byEmail.ExpiresAt);   // access token 8 giờ

        var (lastLogin, logs) = await _h.QueryAsync(async db => (
            (await db.Users.SingleAsync(u => u.Id == user.Id)).LastLoginAt,
            await db.AuditLogs.Where(l => l.Action == AuditAction.LOGIN).ToListAsync()));
        Assert.Equal(_h.Clock.GetUtcNow().UtcDateTime, lastLogin);
        Assert.Equal(2, logs.Count);
        Assert.All(logs, l =>
        {
            Assert.Equal(user.Id, l.UserId);
            Assert.Equal(ActorRole.TEACHER, l.ActorRole);
            Assert.Equal((AuditTargets.Users, (long?)user.Id, Ip), (l.TargetType, l.TargetId, l.IpAddress));
        });
    }

    [Fact]
    public async Task Wrong_password_and_unknown_account_return_the_same_invalid_credentials()
    {
        await TeacherAsync();

        var wrong = await LoginFails("giaovien.a", "SaiMatKhau1");
        var unknown = await LoginFails("khongtontai");

        Assert.Equal((401, "INVALID_CREDENTIALS"), (wrong.StatusCode, wrong.AppMessage.Code));
        Assert.Equal(wrong.AppMessage, unknown.AppMessage);
        Assert.Equal("Email, tên đăng nhập hoặc mật khẩu không đúng.", wrong.Message);
    }

    [Fact]
    public async Task Five_failures_in_15_minutes_lock_login_for_15_minutes_and_log_it()
    {
        var user = await TeacherAsync();
        for (var i = 0; i < 2; i++) await LoginFails("giaovien.a", "SaiMatKhau1");
        // Xen kẽ email và username vẫn tính chung cho một tài khoản.
        for (var i = 0; i < 2; i++) await LoginFails("giaovien.a@example.vn", "SaiMatKhau1");

        var fifth = await LoginFails("giaovien.a", "SaiMatKhau1");
        Assert.Equal((403, "ACCOUNT_BLOCKED"), (fifth.StatusCode, fifth.AppMessage.Code));
        Assert.Equal("Tài khoản không đăng nhập được. Bạn đã nhập sai mật khẩu 5 lần liên tiếp. Vui lòng thử lại sau 15 phút.",
            fifth.Message);

        var stillLocked = await LoginFails("giaovien.a");                  // đúng mật khẩu vẫn bị chặn
        Assert.Equal("ACCOUNT_BLOCKED", stillLocked.AppMessage.Code);

        var lockedOut = await _h.QueryAsync(db => db.AuditLogs.SingleAsync(l => l.Action == AuditAction.LOGIN_LOCKED_OUT));
        Assert.Equal(user.Id, lockedOut.TargetId);

        _h.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(user.Id, (await Login("giaovien.a")).User.Id);
    }

    [Fact]
    public async Task Failures_older_than_15_minutes_do_not_count()
    {
        await TeacherAsync();
        for (var i = 0; i < 4; i++) await LoginFails("giaovien.a", "SaiMatKhau1");
        _h.Clock.Advance(TimeSpan.FromMinutes(16));

        var next = await LoginFails("giaovien.a", "SaiMatKhau1");
        Assert.Equal("INVALID_CREDENTIALS", next.AppMessage.Code);
    }

    [Theory]
    [InlineData(UserStatus.LOCKED, "Tài khoản đã bị khóa. Vui lòng liên hệ quản trị viên.")]
    [InlineData(UserStatus.INACTIVE, "Tài khoản đã ngừng sử dụng.")]
    public async Task Locked_or_inactive_accounts_cannot_sign_in(UserStatus status, string reason)
    {
        var org = await _h.AddOrganizationAsync();
        await _h.AddUserAsync("giaovien.b", RoleCode.TEACHER, org.Id, status: status);

        var ex = await LoginFails("giaovien.b");
        Assert.Equal((403, $"Tài khoản không đăng nhập được. {reason}"), (ex.StatusCode, ex.Message));
    }

    [Fact]
    public async Task Accounts_of_a_deactivated_organization_cannot_sign_in()
    {
        var user = await TeacherAsync();
        await _h.QueryAsync(async db =>
        {
            (await db.Organizations.SingleAsync(o => o.Id == user.OrganizationId)).DeletedAt = _h.Clock.GetUtcNow().UtcDateTime;
            return await db.SaveChangesAsync();
        });

        var ex = await LoginFails("giaovien.a");
        Assert.Equal("Tài khoản không đăng nhập được. Trường của bạn đã ngừng sử dụng nền tảng.", ex.Message);
    }

    [Fact]
    public async Task Soft_deleted_accounts_behave_as_unknown()
    {
        var user = await TeacherAsync();
        await _h.QueryAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == user.Id)).DeletedAt = _h.Clock.GetUtcNow().UtcDateTime;
            return await db.SaveChangesAsync();
        });

        Assert.Equal("INVALID_CREDENTIALS", (await LoginFails("giaovien.a")).AppMessage.Code);
    }

    [Fact]
    public async Task Forgot_password_sends_a_30_minute_link_and_replaces_older_links()
    {
        await TeacherAsync();

        await Forgot("GiaoVien.A@example.vn");
        var first = _h.Mail.LastTokenFor("giaovien.a@example.vn")!;
        await Forgot("giaovien.a@example.vn");
        var second = _h.Mail.LastTokenFor("giaovien.a@example.vn")!;
        Assert.NotEqual(first, second);
        Assert.Contains("30 phút", _h.Mail.LastTo("giaovien.a@example.vn")!.Value.Body);

        var stale = await Assert.ThrowsAsync<AppException>(() => SetPassword(first, "MatKhauMoi2026"));
        Assert.Equal((422, "PASSWORD_LINK_INVALID"), (stale.StatusCode, stale.AppMessage.Code));

        await SetPassword(second, "MatKhauMoi2026");
        Assert.Equal("INVALID_CREDENTIALS", (await LoginFails("giaovien.a")).AppMessage.Code);
        Assert.NotNull(await Login("giaovien.a", "MatKhauMoi2026"));

        var reused = await Assert.ThrowsAsync<AppException>(() => SetPassword(second, "MatKhauKhac2026"));
        Assert.Equal("PASSWORD_LINK_INVALID", reused.AppMessage.Code);
    }

    [Fact]
    public async Task Forgot_password_link_expires_after_30_minutes()
    {
        await TeacherAsync();
        await Forgot("giaovien.a@example.vn");
        var token = _h.Mail.LastTokenFor("giaovien.a@example.vn")!;

        _h.Clock.Advance(TimeSpan.FromMinutes(31));
        var ex = await Assert.ThrowsAsync<AppException>(() => SetPassword(token, "MatKhauMoi2026"));
        Assert.Equal("Liên kết đã hết hạn hoặc không còn dùng được. Vui lòng yêu cầu gửi liên kết mới.", ex.Message);
    }

    [Fact]
    public async Task Forgot_password_is_silent_for_unknown_or_locked_accounts_and_limited_to_3_per_hour()
    {
        var org = await _h.AddOrganizationAsync();
        await _h.AddUserAsync("giaovien.a", RoleCode.TEACHER, org.Id);
        await _h.AddUserAsync("bi.khoa", RoleCode.TEACHER, org.Id, status: UserStatus.LOCKED);

        await Forgot("khongai@example.vn");
        await Forgot("bi.khoa@example.vn");
        Assert.Equal(0, _h.Mail.CountTo("khongai@example.vn") + _h.Mail.CountTo("bi.khoa@example.vn"));

        for (var i = 0; i < 4; i++) await Forgot("giaovien.a@example.vn");
        Assert.Equal(3, _h.Mail.CountTo("giaovien.a@example.vn"));

        _h.Clock.Advance(TimeSpan.FromHours(1));
        await Forgot("giaovien.a@example.vn");
        Assert.Equal(4, _h.Mail.CountTo("giaovien.a@example.vn"));
    }

    [Fact]
    public async Task Check_token_returns_the_username_and_expiry()
    {
        await TeacherAsync();
        await Forgot("giaovien.a@example.vn");
        var token = _h.Mail.LastTokenFor("giaovien.a@example.vn")!;

        var info = await _h.Service<AuthService, PasswordTokenInfoDto>(s => s.CheckPasswordTokenAsync(new PasswordTokenRequest(token), default));
        Assert.Equal("giaovien.a", info.Username);
        Assert.Equal(_h.Clock.GetUtcNow().UtcDateTime.AddMinutes(30), info.ExpiresAt);

        var bad = await Assert.ThrowsAsync<AppException>(() =>
            _h.Service<AuthService, PasswordTokenInfoDto>(s => s.CheckPasswordTokenAsync(new PasswordTokenRequest("khong-phai-token"), default)));
        Assert.Equal("PASSWORD_LINK_INVALID", bad.AppMessage.Code);
    }

    [Fact]
    public async Task Weak_new_password_is_rejected_with_password_policy()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => SetPassword("token-bat-ky", "matkhau"));
        Assert.Equal((400, "PASSWORD_POLICY"), (ex.StatusCode, ex.AppMessage.Code));
        Assert.Equal("Mật khẩu phải dài 8-64 ký tự, có chữ hoa, chữ thường và chữ số.", ex.Errors!["newPassword"]);
    }

    [Fact]
    public async Task Change_password_requires_the_current_password_and_changes_the_security_stamp()
    {
        var user = await TeacherAsync();
        var actor = new Actor(user.Id, RoleCode.TEACHER, user.OrganizationId, Ip);

        var wrong = await Assert.ThrowsAsync<AppException>(() => _h.Service<AuthService, AccessTokenDto>(s =>
            s.ChangePasswordAsync(actor, new ChangePasswordRequest("SaiMatKhau1", "MatKhauMoi2026"), default)));
        Assert.Equal((400, "INVALID"), (wrong.StatusCode, wrong.AppMessage.Code));
        Assert.True(wrong.Errors!.ContainsKey("currentPassword"));

        var before = SecurityStamps.From(user.PasswordHash);
        var token = await _h.Service<AuthService, AccessTokenDto>(s =>
            s.ChangePasswordAsync(actor, new ChangePasswordRequest(Password, "MatKhauMoi2026"), default));
        var after = await _h.QueryAsync(async db => SecurityStamps.From((await db.Users.SingleAsync(u => u.Id == user.Id)).PasswordHash));

        Assert.False(string.IsNullOrEmpty(token.AccessToken));
        Assert.NotEqual(before, after);
        Assert.NotNull(await Login("giaovien.a", "MatKhauMoi2026"));
    }

    [Fact]
    public async Task Student_accounts_cannot_sign_in_to_the_admin_web_and_get_the_same_invalid_credentials()
    {
        var teacher = await TeacherAsync("giaovien.lan");
        // Mã viết thường vì SQLite so chuỗi phân biệt hoa thường (SQL Server không phân biệt, kiểm ở lớp kiểm thử CSDL tạm);
        // đúng mật khẩu vẫn bị từ chối, nghĩa là bị chặn theo vai trò chứ không phải do không tìm thấy.
        var (student, _) = await _h.AddStudentAsync(teacher.OrganizationId!.Value, teacher.Id, "hs8k2qx7");

        var byCode = await LoginFails("hs8k2qx7");
        var unknown = await LoginFails("khongtontai");

        Assert.Equal((401, "INVALID_CREDENTIALS"), (byCode.StatusCode, byCode.AppMessage.Code));
        Assert.Equal(unknown.AppMessage, byCode.AppMessage);
        var logins = await _h.QueryAsync(db => db.AuditLogs.CountAsync(l => l.UserId == student.Id));
        Assert.Equal(0, logins);
    }

    [Fact]
    public async Task Change_password_rejects_the_same_password_and_clears_the_forced_change_flag()
    {
        var user = await TeacherAsync();
        await _h.QueryAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == user.Id)).MustChangePassword = true;
            return await db.SaveChangesAsync();
        });
        var actor = new Actor(user.Id, RoleCode.TEACHER, user.OrganizationId, Ip);

        var profile = await Login("giaovien.a");
        Assert.True(profile.User.MustChangePassword);

        var same = await Assert.ThrowsAsync<AppException>(() => _h.Service<AuthService, AccessTokenDto>(s =>
            s.ChangePasswordAsync(actor, new ChangePasswordRequest(Password, Password), default)));
        Assert.Equal((400, "SAME_PASSWORD"), (same.StatusCode, same.AppMessage.Code));
        Assert.Equal("Mật khẩu mới phải khác mật khẩu cũ.", same.Errors!["newPassword"]);

        await _h.Service<AuthService, AccessTokenDto>(s =>
            s.ChangePasswordAsync(actor, new ChangePasswordRequest(Password, "MatKhauMoi2026"), default));
        Assert.False((await Login("giaovien.a", "MatKhauMoi2026")).User.MustChangePassword);
    }
}
