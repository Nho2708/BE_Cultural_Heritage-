using System.Net;
using DSVHVN.Application.Classes;
using DSVHVN.Application.Students;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Lớp và học sinh trên SQL Server LocalDB (database tạm tạo bằng migration, tự xóa khi xong): sĩ số 60 giữ đúng khi nhiều yêu cầu cùng lúc,
/// mã học sinh không trùng và không cấp lại, tên lớp không phân biệt hoa thường theo collation, vô hiệu có hiệu lực ngay.
/// </summary>
public sealed class StudentSqlServerTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>
{
    private readonly ClassroomClient _c = new(factory);
    private HttpClient Http => _c.Http;

    [LocalDbFact]
    public async Task Concurrent_imports_into_one_class_never_exceed_60_active_students()
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken);

        // Hai lần nhập 40 dòng cùng lúc vào lớp trống: khóa dòng lớp làm hai giao dịch chạy tuần tự, tổng đúng 60.
        string[] Names(string prefix) => Enumerable.Range(1, 40).Select(i => $"{prefix} {i}").ToArray();
        var results = await Task.WhenAll(
            Http.PostAsync<ImportResultDto>($"/api/v1/teacher/classes/{schoolClass.Id}/students/import",
                new { rows = Names("Em A").Select((n, i) => new { rowNumber = i + 1, fullName = n }) }, school.TeacherToken),
            Http.PostAsync<ImportResultDto>($"/api/v1/teacher/classes/{schoolClass.Id}/students/import",
                new { rows = Names("Em B").Select((n, i) => new { rowNumber = i + 1, fullName = n }) }, school.TeacherToken));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Response.StatusCode));
        Assert.Equal(60, results.Sum(r => r.Body.Data!.AddedCount));
        Assert.Equal([20, 40], results.Select(r => r.Body.Data!.AddedCount).Order());
        Assert.Equal(60, await factory.WithDbAsync(db => db.Students.CountAsync(s => s.ClassId == schoolClass.Id && s.IsActive)));

        var codes = await factory.WithDbAsync(db => db.Students.Select(s => s.StudentCode).ToListAsync());
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [LocalDbFact]
    public async Task Concurrent_single_adds_into_a_nearly_full_class_stop_at_60()
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken);
        await _c.ImportAsync(school.TeacherToken, schoolClass.Id, Enumerable.Range(1, 50).Select(i => "Em " + i));

        var results = await Task.WhenAll(Enumerable.Range(1, 16).Select(i => Http.PostAsync<StudentsCreatedDto>(
            $"/api/v1/teacher/classes/{schoolClass.Id}/students", new { fullName = "Em thêm " + i }, school.TeacherToken)));

        Assert.Equal(10, results.Count(r => r.Response.StatusCode == HttpStatusCode.Created));
        Assert.Equal(6, results.Count(r => r.Response.StatusCode == HttpStatusCode.UnprocessableEntity));
        Assert.Equal(60, await factory.WithDbAsync(db => db.Students.CountAsync(s => s.ClassId == schoolClass.Id && s.IsActive)));
    }

    [LocalDbFact]
    public async Task Student_flow_works_on_the_migrated_schema_and_codes_are_never_reissued()
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken, "8A1", 8);
        var entry = (await _c.AddStudentAsync(school.TeacherToken, schoolClass.Id, "Nguyễn Văn Đạt")).Sheet.Entries.Single();

        var token = await _c.ReadyStudentTokenAsync(entry);
        var (me, meBody) = await Http.GetAsync<DSVHVN.Application.StudentAuth.StudentProfileDto>("/api/v1/student/me", token);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(("Nguyễn Văn Đạt", 2), (meBody.Data!.FullName, meBody.Data.SchoolLevel));

        // Đăng nhập gõ thường có khoảng trắng; collation CI cũng khớp với mã đã chuẩn hóa.
        await _c.StudentLoginAsync(entry.StudentCode!.ToLowerInvariant().Insert(2, " "), "MatKhauMoi2026");

        // Xóa mềm: học sinh, tài khoản ẩn đi nhưng mã vẫn nằm trong students nên ràng buộc không trùng (không lọc deleted_at) giữ mã.
        var id = (await factory.WithDbAsync(db => db.Students.SingleAsync(s => s.StudentCode == entry.StudentCode))).Id;
        var (del, _) = await Http.DeleteAsync<object>($"/api/v1/teacher/classes/{schoolClass.Id}/students/{id}", school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var (gone, _) = await Http.GetAsync<object>("/api/v1/student/me", token);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);

        await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            var user = new User { OrganizationId = school.OrgId, RoleId = Roles.StudentId, Username = "TAI.SU.DUNG", FullName = "Em khác", Status = Domain.Enums.UserStatus.ACTIVE };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            db.Students.Add(new Student { ClassId = schoolClass.Id, UserId = user.Id, StudentCode = entry.StudentCode, FullName = "Em khác" });
            return await db.SaveChangesAsync();
        }));

        // Tên đăng nhập của học sinh đã xóa được dùng lại bởi tài khoản người lớn (unique có lọc deleted_at).
        await factory.WithDbAsync(async db =>
        {
            db.Users.Add(new User { OrganizationId = school.OrgId, RoleId = Roles.TeacherId, Username = entry.StudentCode, Email = "dung.lai@nguyendu.edu.vn", FullName = "Dùng lại" });
            return await db.SaveChangesAsync();
        });
    }

    [LocalDbFact]
    public async Task Class_name_uniqueness_and_search_follow_the_database_collation()
    {
        var school = await _c.SchoolAsync();
        await _c.CreateClassAsync(school.TeacherToken, "8A1");
        var (dup, body) = await Http.PostAsync<object>("/api/v1/teacher/classes",
            new { name = "8a1", grade = 8, schoolYear = "2026-2027" }, school.TeacherToken);
        Assert.Equal((HttpStatusCode.Conflict, "DUPLICATE"), (dup.StatusCode, body.ErrorCode));

        var schoolClass = (await _c.CreateClassAsync(school.TeacherToken, "9B", 9));
        await _c.ImportAsync(school.TeacherToken, schoolClass.Id, ["Nguyễn Thị Hằng", "Lê Văn Hùng"]);
        var (_, byName) = await Http.GetAsync<StudentPageDto>($"/api/v1/teacher/classes/{schoolClass.Id}/students?keyword=nguyễn thị", school.TeacherToken);
        Assert.Equal("Nguyễn Thị Hằng", Assert.Single(byName.Data!.Items).FullName);
        var (_, classes) = await Http.GetAsync<ClassPageDto>("/api/v1/teacher/classes?keyword=9b", school.TeacherToken);
        Assert.Equal("9B", Assert.Single(classes.Data!.Items).Name);
    }
}
