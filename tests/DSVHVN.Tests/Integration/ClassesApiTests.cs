using System.Net;
using DSVHVN.Application.Classes;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Quizzes;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>Quản lý lớp qua HTTP: tạo, sửa, xóa mềm lớp của giáo viên; khối lớp 1-12, năm học, cấp học; phân quyền theo vai trò và trường.</summary>
public sealed class ClassesApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly ClassroomClient _c = new(factory);
    private HttpClient Http => _c.Http;

    [Fact]
    public async Task Teacher_creates_lists_reads_and_updates_own_class_with_school_level()
    {
        var school = await _c.SchoolAsync();

        var (res, body) = await Http.PostAsync<ClassDto>("/api/v1/teacher/classes",
            new { name = "  8A1 ", grade = 8, schoolYear = "2026-2027" }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal("Tạo lớp thành công.", body.Message);
        var created = body.Data!;
        Assert.Equal(("8A1", 8, 2, "Cấp 2", "2026-2027", 0, 60), (created.Name, created.Grade, created.SchoolLevel,
            created.SchoolLevelName, created.SchoolYear, created.ActiveStudentCount, created.MaxStudents));

        await _c.CreateClassAsync(school.TeacherToken, "1B", 1);
        await _c.CreateClassAsync(school.TeacherToken, "12C", 12, "2025-2026");

        var (list, listBody) = await Http.GetAsync<ClassPageDto>("/api/v1/teacher/classes", school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(["1B", "8A1", "12C"], listBody.Data!.Items.Select(c => c.Name));   // năm học mới trước, rồi khối, rồi tên
        Assert.Equal([1, 2, 3], listBody.Data.Items.Select(c => c.SchoolLevel));

        var (filtered, filteredBody) = await Http.GetAsync<ClassPageDto>("/api/v1/teacher/classes?schoolYear=2025-2026&keyword=12", school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        Assert.Equal("12C", Assert.Single(filteredBody.Data!.Items).Name);

        var (put, putBody) = await Http.PutAsync<ClassDto>($"/api/v1/teacher/classes/{created.Id}",
            new { name = "8A2", grade = 9, schoolYear = "2026-2027" }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(("8A2", 9, 2), (putBody.Data!.Name, putBody.Data.Grade, putBody.Data.SchoolLevel));

        var (get, getBody) = await Http.GetAsync<ClassDto>($"/api/v1/teacher/classes/{created.Id}", school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("8A2", getBody.Data!.Name);

        // Lớp thuộc trường và giáo viên tạo, không đổi người phụ trách.
        var row = await factory.WithDbAsync(db => db.Classes.SingleAsync(c => c.Id == created.Id));
        Assert.Equal((school.OrgId, school.Teacher.Id), (row.OrganizationId, row.TeacherId));
    }

    [Theory]
    [InlineData("", 8, "2026-2027", "name", "REQUIRED")]
    [InlineData("8A1", null, "2026-2027", "grade", "REQUIRED")]
    [InlineData("8A1", 0, "2026-2027", "grade", "INVALID")]
    [InlineData("8A1", 13, "2026-2027", "grade", "INVALID")]
    [InlineData("8A1", 8, "", "schoolYear", "REQUIRED")]
    [InlineData("8A1", 8, "2026", "schoolYear", "INVALID")]
    [InlineData("8A1", 8, "2026-2028", "schoolYear", "INVALID")]
    [InlineData("8A1", 8, "2027-2026", "schoolYear", "INVALID")]
    public async Task Class_form_rejects_invalid_values(string name, int? grade, string schoolYear, string field, string code)
    {
        var school = await _c.SchoolAsync();
        var (res, body) = await Http.PostAsync<object>("/api/v1/teacher/classes", new { name, grade, schoolYear }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(code, body.ErrorCode);
        Assert.Contains(field, body.Errors!.Keys);
    }

    [Fact]
    public async Task Class_name_is_unique_per_teacher_and_school_year_only()
    {
        var school = await _c.SchoolAsync();
        var (otherTeacher, otherToken) = await _c.AnotherTeacherAsync(school);
        Assert.NotEqual(school.Teacher.Id, otherTeacher.Id);

        var first = await _c.CreateClassAsync(school.TeacherToken, "8A1");
        var (dup, dupBody) = await Http.PostAsync<object>("/api/v1/teacher/classes",
            new { name = "8a1", grade = 8, schoolYear = "2026-2027" }, school.TeacherToken);
        Assert.Equal((HttpStatusCode.Conflict, "DUPLICATE"), (dup.StatusCode, dupBody.ErrorCode));
        Assert.Equal("Tên lớp \"8a1\" đã được sử dụng cho một tài khoản khác.", dupBody.Message);

        await _c.CreateClassAsync(school.TeacherToken, "8A1", 8, "2027-2028");   // năm học khác
        await _c.CreateClassAsync(otherToken, "8A1");                               // giáo viên khác
        var (self, _) = await Http.PutAsync<ClassDto>($"/api/v1/teacher/classes/{first.Id}",
            new { name = "8A1", grade = 8, schoolYear = "2026-2027" }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, self.StatusCode);   // giữ nguyên tên của chính lớp này không tính là trùng
    }

    [Fact]
    public async Task Teacher_only_sees_own_classes_other_teacher_and_other_school_get_404()
    {
        var a = await _c.SchoolAsync();
        var b = await _c.SchoolAsync();
        var (_, sameSchoolToken) = await _c.AnotherTeacherAsync(a);
        var mine = await _c.CreateClassAsync(a.TeacherToken, "8A1");

        foreach (var token in new[] { sameSchoolToken, b.TeacherToken })
        {
            var (get, getBody) = await Http.GetAsync<object>($"/api/v1/teacher/classes/{mine.Id}", token);
            var (put, _) = await Http.PutAsync<object>($"/api/v1/teacher/classes/{mine.Id}",
                new { name = "X", grade = 8, schoolYear = "2026-2027" }, token);
            var (del, _) = await Http.DeleteAsync<object>($"/api/v1/teacher/classes/{mine.Id}", token);
            var (list, listBody) = await Http.GetAsync<ClassPageDto>("/api/v1/teacher/classes", token);
            Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound),
                (get.StatusCode, put.StatusCode, del.StatusCode));
            Assert.Equal("Không thể xem lớp. Không tìm thấy lớp của bạn.", getBody.Message);
            Assert.Empty(listBody.Data!.Items);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        }
    }

    [Fact]
    public async Task Org_admin_and_admin_cannot_use_teacher_endpoints_and_org_admin_sees_only_counts()
    {
        var school = await _c.SchoolAsync();
        var adminToken = await _c.Platform.AdminTokenAsync();
        var @class = await _c.CreateClassAsync(school.TeacherToken, "8A1", 8);
        await _c.CreateClassAsync(school.TeacherToken, "3B", 3);
        await _c.AddStudentAsync(school.TeacherToken, @class.Id, "Trần Thị Bé");
        await _c.AddStudentAsync(school.TeacherToken, @class.Id, "Lê Văn Cường");

        foreach (var token in new[] { school.OrgAdminToken, adminToken })
        {
            foreach (var url in new[] { "/api/v1/teacher/classes", $"/api/v1/teacher/classes/{@class.Id}",
                         $"/api/v1/teacher/classes/{@class.Id}/students" })
            {
                var (res, body) = await Http.GetAsync<object>(url, token);
                Assert.True(res.StatusCode == HttpStatusCode.Forbidden, url);
                Assert.Equal("FORBIDDEN", body.ErrorCode);
            }
        }

        var (summary, summaryBody) = await Http.GetAsync<SchoolSummaryDto>("/api/v1/org/school-summary", school.OrgAdminToken);
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        Assert.Equal((2, 2), (summaryBody.Data!.ClassCount, summaryBody.Data.ActiveStudentCount));
        Assert.Equal([(1, 1, 0), (2, 1, 2), (3, 0, 0)],
            summaryBody.Data.Levels.Select(l => (l.SchoolLevel, l.ClassCount, l.ActiveStudentCount)));
        var (teacherCannot, _) = await Http.GetAsync<object>("/api/v1/org/school-summary", school.TeacherToken);
        Assert.Equal(HttpStatusCode.Forbidden, teacherCannot.StatusCode);
    }

    [Fact]
    public async Task Changing_grade_to_another_level_needs_confirmation_only_when_class_has_lessons()
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken, "5A", 5);

        // Chưa có bài học: đổi sang cấp khác không cần xác nhận.
        var (free, _) = await Http.PutAsync<ClassDto>($"/api/v1/teacher/classes/{schoolClass.Id}",
            new { name = "5A", grade = 6, schoolYear = "2026-2027" }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, free.StatusCode);

        await factory.WithDbAsync(async db =>
        {
            db.Lessons.Add(new Lesson { TeacherId = school.Teacher.Id, ClassId = schoolClass.Id, Title = "Hoàng thành Thăng Long" });
            return await db.SaveChangesAsync();
        });

        var body = new { name = "5A", grade = 10, schoolYear = "2026-2027" };
        var (blocked, blockedBody) = await Http.PutAsync<object>($"/api/v1/teacher/classes/{schoolClass.Id}", body, school.TeacherToken);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "CLASS_LEVEL_CHANGE_NEEDS_CONFIRMATION"), (blocked.StatusCode, blockedBody.ErrorCode));
        Assert.Equal("Lớp 5A sẽ chuyển từ cấp 2 sang cấp 3. Giao diện học của học sinh đổi theo cấp mới; bài học và bài kiểm tra đã có giữ nguyên nội dung. Bạn có chắc muốn đổi?",
            blockedBody.Message);

        // Đổi trong cùng cấp không cần xác nhận; có xác nhận thì đổi được sang cấp khác.
        var (sameLevel, _) = await Http.PutAsync<ClassDto>($"/api/v1/teacher/classes/{schoolClass.Id}",
            new { name = "5A", grade = 7, schoolYear = "2026-2027" }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, sameLevel.StatusCode);
        var (confirmed, confirmedBody) = await Http.PutAsync<ClassDto>($"/api/v1/teacher/classes/{schoolClass.Id}",
            new { name = "5A", grade = 10, schoolYear = "2026-2027", confirmLevelChange = true }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal((10, 3, 1), (confirmedBody.Data!.Grade, confirmedBody.Data.SchoolLevel, confirmedBody.Data.LessonCount));
    }

    [Fact]
    public async Task Deleting_a_class_deactivates_its_students_and_is_blocked_by_an_open_quiz()
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken);
        var added = await _c.AddStudentAsync(school.TeacherToken, schoolClass.Id);
        var entry = added.Sheet.Entries.Single();
        var studentToken = await _c.ReadyStudentTokenAsync(entry);

        var lessonId = await factory.WithDbAsync(async db =>
        {
            var lesson = new Lesson { TeacherId = school.Teacher.Id, ClassId = schoolClass.Id, Title = "Bài 1", Status = ContentStatus.PUBLISHED };
            db.Lessons.Add(lesson);
            await db.SaveChangesAsync();
            db.Quizzes.Add(new Quiz { LessonId = lesson.Id, CreatedBy = school.Teacher.Id, Name = "Kiểm tra 15 phút", Status = QuizStatus.PUBLISHED });
            await db.SaveChangesAsync();
            return lesson.Id;
        });

        var (blocked, blockedBody) = await Http.DeleteAsync<object>($"/api/v1/teacher/classes/{schoolClass.Id}", school.TeacherToken);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "BLOCKED"), (blocked.StatusCode, blockedBody.ErrorCode));
        Assert.Equal("Không thể xóa lớp. Lớp còn bài kiểm tra đang mở. Hãy đóng bài kiểm tra trước.", blockedBody.Message);

        // Đóng quiz thì xóa được; học sinh bị vô hiệu cùng giao dịch và mất quyền ngay.
        await factory.WithDbAsync(async db =>
        {
            (await db.Quizzes.SingleAsync(q => q.LessonId == lessonId)).Status = QuizStatus.CLOSED;
            return await db.SaveChangesAsync();
        });
        var (deleted, _) = await Http.DeleteAsync<object>($"/api/v1/teacher/classes/{schoolClass.Id}", school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        var (me, _) = await Http.GetAsync<object>("/api/v1/student/me", studentToken);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        var (login, loginBody) = await _c.StudentLoginRawAsync(entry.StudentCode!, "MatKhauMoi2026");
        Assert.Equal((HttpStatusCode.Forbidden, "STUDENT_ACCOUNT_BLOCKED"), (login.StatusCode, loginBody.ErrorCode));

        var (get, _) = await Http.GetAsync<object>($"/api/v1/teacher/classes/{schoolClass.Id}", school.TeacherToken);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        var (list, listBody) = await Http.GetAsync<ClassPageDto>("/api/v1/teacher/classes", school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Empty(listBody.Data!.Items);

        var (student, logs) = await factory.WithDbAsync(async db => (
            await db.Students.SingleAsync(s => s.StudentCode == entry.StudentCode),
            await db.AuditLogs.Where(l => l.Action == AuditAction.STUDENT_DEACTIVATED).ToListAsync()));
        Assert.False(student.IsActive);
        Assert.Null(student.DeletedAt);   // kết quả cũ giữ nguyên: chỉ vô hiệu, không xóa học sinh
        Assert.Contains(logs, l => l.TargetType == AuditTargets.Classes && l.TargetId == schoolClass.Id && l.UserId == school.Teacher.Id);
    }

    [Fact]
    public async Task Class_endpoints_require_a_token()
    {
        var (res, body) = await Http.GetAsync<object>("/api/v1/teacher/classes");
        Assert.Equal((HttpStatusCode.Unauthorized, "FORBIDDEN"), (res.StatusCode, body.ErrorCode));
    }
}
