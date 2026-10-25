using System.IdentityModel.Tokens.Jwt;
using System.Net;
using DSVHVN.Application.StudentAuth;
using DSVHVN.Application.Students;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Đăng nhập bằng mã học sinh, buộc đổi mật khẩu lần đầu, đổi mật khẩu, xem lớp của em, và mọi cách học sinh mất quyền (vô hiệu, xóa, ngừng trường)
/// có hiệu lực ngay ở yêu cầu kế tiếp. Chạy cả Api thật với JWT và kiểm tài khoản mỗi yêu cầu.
/// </summary>
public sealed class StudentLoginApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly ClassroomClient _c = new(factory);
    private HttpClient Http => _c.Http;

    private async Task<(ClassroomClient.School School, long ClassId, LoginSheetEntryDto Entry)> StudentAsync(int grade = 8)
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken, "8A1", grade);
        var created = await _c.AddStudentAsync(school.TeacherToken, schoolClass.Id, "Nguyễn Văn An");
        return (school, schoolClass.Id, created.Sheet.Entries.Single());
    }

    [Fact]
    public async Task First_login_forces_password_change_then_student_enters_the_learning_area()
    {
        var (school, classId, entry) = await StudentAsync(grade: 8);

        // Đăng nhập với mã gõ thường, có khoảng trắng và dấu gạch: vẫn đúng. Token kèm cấp học của lớp (khối 8 là cấp 2).
        var typed = entry.StudentCode!.ToLowerInvariant().Insert(2, " ").Insert(5, "-");
        var (res, body) = await _c.StudentLoginRawAsync(typed, entry.TemporaryPassword);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Đăng nhập thành công.", body.Message);
        var first = body.Data!;
        Assert.Equal("Bearer", first.TokenType);
        Assert.True(first.Student.MustChangePassword);
        Assert.Equal((RoleCode.STUDENT, entry.StudentCode, "Nguyễn Văn An", 2, "Cấp 2"),
            (first.Student.Role, first.Student.StudentCode, first.Student.FullName, first.Student.SchoolLevel, first.Student.SchoolLevelName));
        Assert.Equal((classId, "8A1", 8, 2), (first.Student.Class.Id, first.Student.Class.Name, first.Student.Class.Grade, first.Student.Class.SchoolLevel));
        Assert.Equal(school.Teacher.FullName, first.Student.Class.TeacherName);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(first.AccessToken);
        Assert.Equal(("STUDENT", "2"), (jwt.Claims.Single(c => c.Type == "role").Value, jwt.Claims.Single(c => c.Type == "lvl").Value));
        Assert.Equal(first.ExpiresAt, jwt.ValidTo, TimeSpan.FromSeconds(1));
        Assert.InRange((first.ExpiresAt - DateTime.UtcNow).TotalHours, 7.9, 8.1);

        // Khi bị buộc đổi mật khẩu: xem thông tin của em và đổi mật khẩu được, mọi thứ khác 403 kèm lời nhắc đổi mật khẩu.
        var (me, meBody) = await Http.GetAsync<StudentProfileDto>("/api/v1/student/me", first.AccessToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.True(meBody.Data!.MustChangePassword);
        var (cls, clsBody) = await Http.GetAsync<object>("/api/v1/student/class", first.AccessToken);
        Assert.Equal((HttpStatusCode.Forbidden, "PASSWORD_CHANGE_REQUIRED"), (cls.StatusCode, clsBody.ErrorCode));
        Assert.Equal("Em hãy đặt mật khẩu mới của riêng em để bắt đầu học. Mật khẩu cần 8-64 ký tự, có chữ hoa, chữ thường và chữ số.", clsBody.Message);
        var (staff, staffBody) = await Http.GetAsync<object>("/api/v1/me", first.AccessToken);
        Assert.Equal((HttpStatusCode.Forbidden, "FORBIDDEN"), (staff.StatusCode, staffBody.ErrorCode));

        // Mật khẩu mới: sai quy tắc, hai lần không giống, trùng mật khẩu tạm.
        var change = (string current, string next, string confirm) =>
            Http.PutAsync<StudentSessionDto>("/api/v1/student/me/password",
                new { currentPassword = current, newPassword = next, confirmNewPassword = confirm }, first.AccessToken);
        var (weak, weakBody) = await change("", "yeumatkhau", "yeumatkhau");
        Assert.Equal((HttpStatusCode.BadRequest, "PASSWORD_POLICY"), (weak.StatusCode, weakBody.ErrorCode));
        var (mismatch, mismatchBody) = await change("", "MatKhauMoi2026", "MatKhauMoi2027");
        Assert.Equal((HttpStatusCode.BadRequest, "PASSWORD_MISMATCH"), (mismatch.StatusCode, mismatchBody.ErrorCode));
        Assert.Equal("Hai lần nhập mật khẩu chưa giống nhau.", mismatchBody.Errors!["confirmNewPassword"]);
        var (same, sameBody) = await change("", entry.TemporaryPassword, entry.TemporaryPassword);
        Assert.Equal((HttpStatusCode.BadRequest, "SAME_PASSWORD"), (same.StatusCode, sameBody.ErrorCode));
        Assert.Equal("Mật khẩu mới phải khác mật khẩu cũ.", sameBody.Message);

        // Đổi được, không cần mật khẩu hiện tại; token mới vào thẳng khu học, token cũ hết dùng.
        var (ok, okBody) = await change("", "MatKhauMoi2026", "MatKhauMoi2026");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.False(okBody.Data!.Student.MustChangePassword);
        var token = okBody.Data.AccessToken;
        var (oldMe, _) = await Http.GetAsync<object>("/api/v1/student/me", first.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, oldMe.StatusCode);
        var (classOk, classBody) = await Http.GetAsync<StudentClassDto>("/api/v1/student/class", token);
        Assert.Equal(HttpStatusCode.OK, classOk.StatusCode);
        Assert.Equal(("8A1", "2026-2027", "Cấp 2"), (classBody.Data!.Name, classBody.Data.SchoolYear, classBody.Data.SchoolLevelName));

        // Lần sau đăng nhập bằng mật khẩu mới; mật khẩu tạm không còn dùng được.
        var again = await _c.StudentLoginAsync(entry.StudentCode!, "MatKhauMoi2026");
        Assert.False(again.Student.MustChangePassword);
        var (temp, _) = await _c.StudentLoginRawAsync(entry.StudentCode!, entry.TemporaryPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, temp.StatusCode);

        // last_login_at được ghi; không ghi nhật ký đăng nhập thành công của học sinh.
        var (student, loginLogs) = await factory.WithDbAsync(async db => (
            await db.Students.Include(s => s.User).SingleAsync(s => s.StudentCode == entry.StudentCode),
            await db.AuditLogs.CountAsync(l => l.Action == AuditAction.LOGIN && l.ActorRole == ActorRole.STUDENT)));
        Assert.NotNull(student.User!.LastLoginAt);
        Assert.Equal(0, loginLogs);
    }

    [Fact]
    public async Task Voluntary_password_change_needs_the_current_password()
    {
        var (_, _, entry) = await StudentAsync();
        var token = await _c.ReadyStudentTokenAsync(entry, "MatKhauMoi2026");

        var change = (string? current, string next) => Http.PutAsync<StudentSessionDto>("/api/v1/student/me/password",
            new { currentPassword = current, newPassword = next, confirmNewPassword = next }, token);
        var (missing, missingBody) = await change(null, "MatKhauKhac2026");
        Assert.Equal((HttpStatusCode.BadRequest, "REQUIRED"), (missing.StatusCode, missingBody.ErrorCode));
        var (wrong, wrongBody) = await change("KhongDung123", "MatKhauKhac2026");
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID"), (wrong.StatusCode, wrongBody.ErrorCode));
        Assert.Contains("currentPassword", wrongBody.Errors!.Keys);
        var (same, sameBody) = await change("MatKhauMoi2026", "MatKhauMoi2026");
        Assert.Equal((HttpStatusCode.BadRequest, "SAME_PASSWORD"), (same.StatusCode, sameBody.ErrorCode));

        var (ok, okBody) = await change("MatKhauMoi2026", "MatKhauKhac2026");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var (oldSession, _) = await Http.GetAsync<object>("/api/v1/student/class", token);
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);   // phiên trên thiết bị khác phải đăng nhập lại
        var (newSession, _) = await Http.GetAsync<object>("/api/v1/student/class", okBody.Data!.AccessToken);
        Assert.Equal(HttpStatusCode.OK, newSession.StatusCode);
    }

    [Fact]
    public async Task Wrong_code_wrong_password_and_adult_accounts_all_get_the_same_message()
    {
        var (school, _, entry) = await StudentAsync();
        var expected = "Mã học sinh hoặc mật khẩu chưa đúng. Em hãy kiểm tra lại phiếu đăng nhập.";

        var attempts = new[]
        {
            (entry.StudentCode!, "SaiMatKhau1"),
            ("HSZZZZZZ", entry.TemporaryPassword),
            (school.Teacher.Username!, PlatformClient.Password),               // giáo viên đăng nhập nhầm ở khu học
            (school.Teacher.Email!, PlatformClient.Password),
        };
        foreach (var (code, password) in attempts)
        {
            var (res, body) = await _c.StudentLoginRawAsync(code, password);
            Assert.Equal((HttpStatusCode.Unauthorized, "STUDENT_INVALID_CREDENTIALS", expected), (res.StatusCode, body.ErrorCode, body.Message));
        }

        // Ngược lại: học sinh đăng nhập ở web quản trị nhận đúng thông điệp sai thông tin của web quản trị.
        var (staffLogin, staffBody) = await Http.PostAsync<object>("/api/v1/auth/login",
            new { emailOrUsername = entry.StudentCode, password = entry.TemporaryPassword });
        Assert.Equal((HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS"), (staffLogin.StatusCode, staffBody.ErrorCode));

        var (empty, emptyBody) = await _c.StudentLoginRawAsync("", "");
        Assert.Equal((HttpStatusCode.BadRequest, "REQUIRED"), (empty.StatusCode, emptyBody.ErrorCode));
        Assert.Contains("studentCode", emptyBody.Errors!.Keys);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_login_and_write_an_audit_entry_without_changing_the_account()
    {
        var (_, _, entry) = await StudentAsync();

        for (var i = 1; i <= 4; i++)
        {
            var (res, _) = await _c.StudentLoginRawAsync(entry.StudentCode!, "SaiMatKhau" + i);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
        var (fifth, fifthBody) = await _c.StudentLoginRawAsync(entry.StudentCode!, "SaiMatKhau5");
        Assert.Equal((HttpStatusCode.Forbidden, "STUDENT_ACCOUNT_BLOCKED"), (fifth.StatusCode, fifthBody.ErrorCode));
        Assert.Equal("Tài khoản của em tạm thời chưa đăng nhập được. Em đã nhập sai nhiều lần. Em hãy thử lại sau 15 phút hoặc nhờ thầy cô đặt lại mật khẩu.",
            fifthBody.Message);

        // Đang tạm khóa thì đúng mật khẩu cũng không vào được.
        var (correct, correctBody) = await _c.StudentLoginRawAsync(entry.StudentCode!, entry.TemporaryPassword);
        Assert.Equal((HttpStatusCode.Forbidden, "STUDENT_ACCOUNT_BLOCKED"), (correct.StatusCode, correctBody.ErrorCode));

        var (user, logs) = await factory.WithDbAsync(async db => (
            await db.Users.SingleAsync(u => u.Username == entry.StudentCode),
            await db.AuditLogs.Where(l => l.Action == AuditAction.LOGIN_LOCKED_OUT && l.TargetId == db.Users.Single(x => x.Username == entry.StudentCode).Id).ToListAsync()));
        Assert.Equal(UserStatus.ACTIVE, user.Status);   // tạm khóa không đổi trạng thái tài khoản
        var log = Assert.Single(logs);
        Assert.Equal((ActorRole.STUDENT, AuditTargets.Users), (log.ActorRole, log.TargetType));
    }

    [Fact]
    public async Task Deactivated_or_deleted_student_loses_access_at_once_and_reactivation_restores_it()
    {
        var (school, classId, entry) = await StudentAsync();
        var token = await _c.ReadyStudentTokenAsync(entry, "MatKhauMoi2026");
        var id = (await factory.WithDbAsync(db => db.Students.SingleAsync(s => s.StudentCode == entry.StudentCode))).Id;
        var url = $"/api/v1/teacher/classes/{classId}/students/{id}";
        var askTeacher = "Tài khoản của em tạm thời chưa đăng nhập được. Em hãy báo thầy cô phụ trách lớp.";

        var (ok, _) = await Http.GetAsync<object>("/api/v1/student/class", token);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        await Http.PostAsync<object>(url + "/deactivate", null, school.TeacherToken);
        var (blocked, blockedBody) = await Http.GetAsync<object>("/api/v1/student/class", token);   // cùng token, yêu cầu kế tiếp
        Assert.Equal((HttpStatusCode.Unauthorized, "STUDENT_ACCOUNT_BLOCKED", askTeacher), (blocked.StatusCode, blockedBody.ErrorCode, blockedBody.Message));
        var (login, loginBody) = await _c.StudentLoginRawAsync(entry.StudentCode!, "MatKhauMoi2026");
        Assert.Equal((HttpStatusCode.Forbidden, "STUDENT_ACCOUNT_BLOCKED", askTeacher), (login.StatusCode, loginBody.ErrorCode, loginBody.Message));

        await Http.PostAsync<object>(url + "/reactivate", null, school.TeacherToken);
        var (back, _) = await Http.GetAsync<object>("/api/v1/student/class", token);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);   // vô hiệu rồi mở lại: kết quả và phiên cũ vẫn dùng được

        await Http.DeleteAsync<object>(url, school.TeacherToken);
        var (gone, _) = await Http.GetAsync<object>("/api/v1/student/class", token);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
        var (loginGone, loginGoneBody) = await _c.StudentLoginRawAsync(entry.StudentCode!, "MatKhauMoi2026");
        Assert.Equal((HttpStatusCode.Unauthorized, "STUDENT_INVALID_CREDENTIALS"), (loginGone.StatusCode, loginGoneBody.ErrorCode));
    }

    [Fact]
    public async Task Deactivated_school_blocks_students_but_a_locked_or_deleted_teacher_does_not()
    {
        var (school, _, entry) = await StudentAsync();
        var token = await _c.ReadyStudentTokenAsync(entry, "MatKhauMoi2026");

        // Giáo viên bị khóa rồi xóa mềm: học sinh vẫn đăng nhập và học.
        await Http.PostAsync<object>($"/api/v1/org/teachers/{school.Teacher.Id}/lock", null, school.OrgAdminToken);
        var (stillOk, _) = await Http.GetAsync<object>("/api/v1/student/class", token);
        Assert.Equal(HttpStatusCode.OK, stillOk.StatusCode);
        await Http.DeleteAsync<object>($"/api/v1/org/teachers/{school.Teacher.Id}", school.OrgAdminToken);
        await _c.StudentLoginAsync(entry.StudentCode!, "MatKhauMoi2026");
        var (profile, profileBody) = await Http.GetAsync<StudentProfileDto>("/api/v1/student/me", token);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.Equal("Nguyễn Văn An", profileBody.Data!.FullName);

        // Quản trị hệ thống ngừng trường: không ai của trường đăng nhập được, kể cả học sinh.
        var adminToken = await _c.Platform.AdminTokenAsync();
        var (stop, _) = await Http.DeleteAsync<object>($"/api/v1/admin/organizations/{school.OrgId}", adminToken);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        var (blocked, blockedBody) = await Http.GetAsync<object>("/api/v1/student/class", token);
        Assert.Equal((HttpStatusCode.Unauthorized, "STUDENT_ACCOUNT_BLOCKED"), (blocked.StatusCode, blockedBody.ErrorCode));
        var (login, loginBody) = await _c.StudentLoginRawAsync(entry.StudentCode!, "MatKhauMoi2026");
        Assert.Equal((HttpStatusCode.Forbidden, "STUDENT_ACCOUNT_BLOCKED"), (login.StatusCode, loginBody.ErrorCode));
    }

    [Fact]
    public async Task Changing_the_class_grade_shows_the_new_school_level_on_the_next_profile_load()
    {
        var (school, classId, entry) = await StudentAsync(grade: 5);
        var token = await _c.ReadyStudentTokenAsync(entry);

        var (before, beforeBody) = await Http.GetAsync<StudentProfileDto>("/api/v1/student/me", token);
        Assert.Equal((HttpStatusCode.OK, 1, 5), (before.StatusCode, beforeBody.Data!.SchoolLevel, beforeBody.Data.Class.Grade));
        await Http.PutAsync<object>($"/api/v1/teacher/classes/{classId}", new { name = "8A1", grade = 11, schoolYear = "2026-2027" }, school.TeacherToken);
        var (after, afterBody) = await Http.GetAsync<StudentProfileDto>("/api/v1/student/me", token);
        Assert.Equal((HttpStatusCode.OK, 3, "Cấp 3"), (after.StatusCode, afterBody.Data!.SchoolLevel, afterBody.Data.SchoolLevelName));
        var fresh = await _c.StudentLoginAsync(entry.StudentCode!, "MatKhauMoi2026");
        Assert.Equal(3, fresh.Student.SchoolLevel);
    }

    [Fact]
    public async Task Student_endpoints_reject_other_roles_and_missing_tokens()
    {
        var (school, _, entry) = await StudentAsync();
        var student = await _c.StudentLoginAsync(entry.StudentCode!, entry.TemporaryPassword);

        foreach (var token in new[] { school.TeacherToken, school.OrgAdminToken, await _c.Platform.AdminTokenAsync() })
        {
            foreach (var url in new[] { "/api/v1/student/me", "/api/v1/student/class" })
            {
                var (res, body) = await Http.GetAsync<object>(url, token);
                Assert.Equal((HttpStatusCode.Forbidden, "FORBIDDEN"), (res.StatusCode, body.ErrorCode));
            }
        }
        var (none, _) = await Http.GetAsync<object>("/api/v1/student/me");
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);

        // Học sinh không vào được khu giáo viên, quản trị trường, quản trị hệ thống.
        foreach (var url in new[] { "/api/v1/teacher/classes", "/api/v1/org", "/api/v1/org/school-summary", "/api/v1/admin/organizations", "/api/v1/me" })
        {
            var (res, _) = await Http.GetAsync<object>(url, student.AccessToken);
            Assert.True(res.StatusCode == HttpStatusCode.Forbidden, url);
        }
    }
}
