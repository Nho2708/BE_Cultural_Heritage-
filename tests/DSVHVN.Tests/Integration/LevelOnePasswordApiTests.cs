using System.Net;
using DSVHVN.Application.StudentAuth;
using DSVHVN.Application.Students;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Đăng nhập của lớp cấp 1: mật khẩu đơn giản 6 chữ số khi thêm em hoặc đặt lại, không bắt đổi mật khẩu, quy tắc mật khẩu nhẹ (6-64 ký tự) khi em tự đổi,
/// phiếu đăng nhập không gọi là mật khẩu tạm; lớp cấp 2 và 3 giữ mật khẩu tạm và quy tắc cũ.
/// </summary>
public sealed class LevelOnePasswordApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly ClassroomClient _c = new(factory);
    private HttpClient Http => _c.Http;

    private static string Url(long classId, string tail = "") => $"/api/v1/teacher/classes/{classId}/students{tail}";

    private Task SetMustChangeAsync(string studentCode, bool value) =>
        factory.WithDbAsync(async db =>
        {
            var student = await db.Students.Include(s => s.User).SingleAsync(s => s.StudentCode == studentCode);
            student.User!.MustChangePassword = value;
            return await db.SaveChangesAsync();
        });

    [Fact]
    public async Task A_level_one_student_gets_a_six_digit_password_that_works_at_once_and_the_sheet_does_not_call_it_temporary()
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken, "3A1", 3);
        var created = await _c.AddStudentAsync(school.TeacherToken, schoolClass.Id, "Nguyễn Bé An");

        var sheet = created.Sheet;
        Assert.False(sheet.PasswordIsTemporary);
        Assert.Equal("Em dùng mật khẩu này mỗi lần đăng nhập. Quên mật khẩu thì em nhờ thầy cô đặt lại.", sheet.FirstLoginReminder);
        Assert.Equal("Mật khẩu chỉ hiện một lần. Hãy in hoặc tải phiếu đăng nhập trước khi rời trang; nếu mất phiếu, dùng \"Đặt lại mật khẩu\".", sheet.Notice);
        Assert.Equal("Đăng nhập bằng mã học sinh và mật khẩu ghi trên phiếu.", sheet.Instructions[^1]);
        Assert.DoesNotContain(sheet.Instructions, line => line.Contains("tạm"));
        var entry = Assert.Single(sheet.Entries);
        Assert.Matches("^[0-9]{6}$", entry.TemporaryPassword);

        // Đăng nhập được ngay, không bị buộc đổi mật khẩu: vào thẳng khu học.
        var session = await _c.StudentLoginAsync(entry.StudentCode!, entry.TemporaryPassword);
        Assert.False(session.Student.MustChangePassword);
        Assert.Equal(1, session.Student.SchoolLevel);
        var (cls, _) = await Http.GetAsync<object>("/api/v1/student/class", session.AccessToken);
        Assert.Equal(HttpStatusCode.OK, cls.StatusCode);
        var (home, _) = await Http.GetAsync<object>("/api/v1/student/home", session.AccessToken);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var stored = await factory.WithDbAsync(db => db.Students.Include(s => s.User).SingleAsync(s => s.StudentCode == entry.StudentCode));
        Assert.False(stored.User!.MustChangePassword);
        Assert.NotEqual(entry.TemporaryPassword, stored.User.PasswordHash);   // chỉ lưu bản băm
    }

    [Fact]
    public async Task Importing_a_list_into_a_level_one_class_gives_every_student_a_six_digit_password_and_the_other_levels_keep_temporary_passwords()
    {
        var school = await _c.SchoolAsync();
        var young = await _c.CreateClassAsync(school.TeacherToken, "5A1", 5);
        var imported = await _c.ImportAsync(school.TeacherToken, young.Id, ["An", "Bình", "Chi"]);
        Assert.False(imported.Created.Sheet.PasswordIsTemporary);
        Assert.All(imported.Created.Sheet.Entries, e => Assert.Matches("^[0-9]{6}$", e.TemporaryPassword));
        foreach (var e in imported.Created.Sheet.Entries) Assert.False((await _c.StudentLoginAsync(e.StudentCode!, e.TemporaryPassword)).Student.MustChangePassword);

        foreach (var grade in new[] { 6, 9, 10, 12 })
        {
            var older = await _c.CreateClassAsync(school.TeacherToken, $"Lớp {grade}", grade);
            var sheet = (await _c.AddStudentAsync(school.TeacherToken, older.Id, "Trần Văn Lớn")).Sheet;
            Assert.True(sheet.PasswordIsTemporary);
            Assert.Equal("Em sẽ phải đổi mật khẩu ở lần đăng nhập đầu tiên.", sheet.FirstLoginReminder);
            Assert.StartsWith("Mật khẩu tạm chỉ hiện một lần.", sheet.Notice);
            Assert.Equal("Đăng nhập bằng mã học sinh và mật khẩu tạm ghi trên phiếu.", sheet.Instructions[^1]);
            var entry = Assert.Single(sheet.Entries);
            Assert.Matches(@"^[A-HJ-NP-Z2-9a-km-z]{8}$", entry.TemporaryPassword);
            Assert.True((await _c.StudentLoginAsync(entry.StudentCode!, entry.TemporaryPassword)).Student.MustChangePassword);
        }
    }

    [Fact]
    public async Task Resetting_a_level_one_student_gives_a_new_six_digit_password_and_clears_the_forced_change_of_an_old_account()
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken, "2A1", 2);
        var created = await _c.AddStudentAsync(school.TeacherToken, schoolClass.Id, "Lê Bé Bông");
        var old = created.Sheet.Entries.Single();
        var oldToken = (await _c.StudentLoginAsync(old.StudentCode!, old.TemporaryPassword)).AccessToken;

        // Tài khoản cấp 1 làm từ trước đang bị buộc đổi mật khẩu.
        await SetMustChangeAsync(old.StudentCode!, true);

        var (res, body) = await Http.PostAsync<LoginSheetDto>(Url(schoolClass.Id, "/reset-password"), new { studentIds = created.Students.Select(s => s.Id).ToList() }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var sheet = body.Data!;
        Assert.False(sheet.PasswordIsTemporary);
        Assert.Equal("Em dùng mật khẩu này mỗi lần đăng nhập. Quên mật khẩu thì em nhờ thầy cô đặt lại.", sheet.FirstLoginReminder);
        var fresh = Assert.Single(sheet.Entries);
        Assert.Matches("^[0-9]{6}$", fresh.TemporaryPassword);

        var (me, _) = await Http.GetAsync<object>("/api/v1/student/me", oldToken);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);   // mật khẩu đổi thì phiên cũ hết dùng
        var session = await _c.StudentLoginAsync(fresh.StudentCode!, fresh.TemporaryPassword);
        Assert.False(session.Student.MustChangePassword);

        // Lớp cấp 2: đặt lại vẫn là mật khẩu tạm và buộc đổi.
        var middle = await _c.CreateClassAsync(school.TeacherToken, "7A1", 7);
        var student = (await _c.AddStudentAsync(school.TeacherToken, middle.Id, "Phạm Văn Trung")).Students.Single();
        var (res2, body2) = await Http.PostAsync<LoginSheetDto>(Url(middle.Id, "/reset-password"), new { studentIds = new[] { student.Id } }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        Assert.True(body2.Data!.PasswordIsTemporary);
        var entry2 = Assert.Single(body2.Data.Entries);
        Assert.Matches(@"^[A-HJ-NP-Z2-9a-km-z]{8}$", entry2.TemporaryPassword);
        Assert.True((await _c.StudentLoginAsync(entry2.StudentCode!, entry2.TemporaryPassword)).Student.MustChangePassword);
    }

    [Fact]
    public async Task A_level_one_student_changes_the_password_with_only_the_length_rule_and_the_other_levels_keep_the_full_rule()
    {
        var school = await _c.SchoolAsync();
        var young = await _c.CreateClassAsync(school.TeacherToken, "4A1", 4);
        var entry = (await _c.AddStudentAsync(school.TeacherToken, young.Id, "Đỗ Bé Mai")).Sheet.Entries.Single();
        var session = await _c.StudentLoginAsync(entry.StudentCode!, entry.TemporaryPassword);

        Task<(HttpResponseMessage Response, Envelope<StudentSessionDto> Body)> Change(string token, string? current, string next) =>
            Http.PutAsync<StudentSessionDto>("/api/v1/student/me/password",
                new { currentPassword = current, newPassword = next, confirmNewPassword = next }, token);

        // Tự đổi: ngắn hơn 6 ký tự bị từ chối với câu riêng của cấp 1; dài 6 ký tự không cần chữ hoa, thường, số.
        var (tooShort, tooShortBody) = await Change(session.AccessToken, entry.TemporaryPassword, "12345");
        Assert.Equal((HttpStatusCode.BadRequest, "PASSWORD_POLICY"), (tooShort.StatusCode, tooShortBody.ErrorCode));
        Assert.Equal("Mật khẩu dài 6-64 ký tự.", tooShortBody.Message);
        Assert.Equal("Mật khẩu dài 6-64 ký tự.", tooShortBody.Errors!["newPassword"]);
        var (tooLong, tooLongBody) = await Change(session.AccessToken, entry.TemporaryPassword, new string('a', 65));
        Assert.Equal((HttpStatusCode.BadRequest, "PASSWORD_POLICY"), (tooLong.StatusCode, tooLongBody.ErrorCode));
        var (same, sameBody) = await Change(session.AccessToken, entry.TemporaryPassword, entry.TemporaryPassword);
        Assert.Equal((HttpStatusCode.BadRequest, "SAME_PASSWORD"), (same.StatusCode, sameBody.ErrorCode));
        var (ok, okBody) = await Change(session.AccessToken, entry.TemporaryPassword, "abcdef");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.False(okBody.Data!.Student.MustChangePassword);
        Assert.NotNull(await _c.StudentLoginAsync(entry.StudentCode!, "abcdef"));

        // Tài khoản cấp 1 cũ đang bị buộc đổi: đổi ở chế độ bắt buộc cũng chỉ cần 6-64 ký tự, vẫn phải khác mật khẩu hiện tại.
        await SetMustChangeAsync(entry.StudentCode!, true);
        var forced = await _c.StudentLoginAsync(entry.StudentCode!, "abcdef");
        Assert.True(forced.Student.MustChangePassword);
        var (forcedShort, forcedShortBody) = await Change(forced.AccessToken, null, "abc");
        Assert.Equal("Mật khẩu dài 6-64 ký tự.", forcedShortBody.Message);
        Assert.Equal(HttpStatusCode.BadRequest, forcedShort.StatusCode);
        var (forcedSame, forcedSameBody) = await Change(forced.AccessToken, null, "abcdef");
        Assert.Equal((HttpStatusCode.BadRequest, "SAME_PASSWORD"), (forcedSame.StatusCode, forcedSameBody.ErrorCode));
        var (forcedOk, _) = await Change(forced.AccessToken, null, "654321");
        Assert.Equal(HttpStatusCode.OK, forcedOk.StatusCode);

        // Lớp cấp 2 và 3: vẫn 8-64 ký tự có chữ hoa, chữ thường và chữ số.
        foreach (var grade in new[] { 8, 11 })
        {
            var older = await _c.CreateClassAsync(school.TeacherToken, $"Lớp {grade}", grade);
            var e = (await _c.AddStudentAsync(school.TeacherToken, older.Id, "Vũ Văn Lớn")).Sheet.Entries.Single();
            var s = await _c.StudentLoginAsync(e.StudentCode!, e.TemporaryPassword);
            var (weak, weakBody) = await Change(s.AccessToken, null, "abcdef12");
            Assert.Equal((HttpStatusCode.BadRequest, "PASSWORD_POLICY"), (weak.StatusCode, weakBody.ErrorCode));
            Assert.Equal("Mật khẩu phải dài 8-64 ký tự, có chữ hoa, chữ thường và chữ số.", weakBody.Message);
            var (six, sixBody) = await Change(s.AccessToken, null, "123456");
            Assert.Equal("Mật khẩu phải dài 8-64 ký tự, có chữ hoa, chữ thường và chữ số.", sixBody.Message);
            Assert.Equal(HttpStatusCode.BadRequest, six.StatusCode);
            var (good, _) = await Change(s.AccessToken, null, "MatKhauMoi2026");
            Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        }
    }
}
