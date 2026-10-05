using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using DSVHVN.Application.Common;
using DSVHVN.Application.Students;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Quizzes;
using DSVHVN.Domain.Lessons;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Học sinh của lớp qua HTTP: thêm từng em (tạo tài khoản và phiếu đăng nhập), nhập danh sách từ văn bản, .csv, .xlsx với bước xem trước,
/// sĩ số 60, đặt lại mật khẩu, vô hiệu, mở lại, xóa mềm; nhật ký không chứa mật khẩu; tách dữ liệu giữa giáo viên.
/// </summary>
public sealed class StudentsApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly ClassroomClient _c = new(factory);
    private HttpClient Http => _c.Http;

    private static string Url(long classId, string tail = "") => $"/api/v1/teacher/classes/{classId}/students{tail}";

    [Fact]
    public async Task Adding_a_student_creates_account_code_and_one_time_login_sheet()
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken, "8A1", 8);

        var (res, body) = await Http.PostAsync<StudentsCreatedDto>(Url(schoolClass.Id), new { fullName = "  Nguyễn   Văn An " }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal("Thêm học sinh thành công.", body.Message);
        Assert.True(res.Headers.CacheControl?.NoStore);   // phản hồi có mật khẩu tạm không được lưu đệm
        var student = Assert.Single(body.Data!.Students);
        var entry = Assert.Single(body.Data.Sheet.Entries);

        Assert.Matches("^HS[2-9A-HJ-NP-Z]{6}$", student.StudentCode!);
        Assert.Equal("Nguyễn Văn An", student.FullName);
        Assert.True(student.IsActive);
        Assert.False(student.PasswordChanged);
        Assert.Null(student.LastLoginAt);

        // Phiếu: họ tên, lớp, mã, mật khẩu tạm; cùng các câu chung cho mọi phiếu.
        Assert.Equal((student.Id, "Nguyễn Văn An", "8A1", student.StudentCode), (entry.StudentId, entry.FullName, entry.ClassName, entry.StudentCode));
        Assert.Matches(@"^[A-HJ-NP-Z2-9a-km-z]{8}$", entry.TemporaryPassword);   // bỏ 0 O o 1 I l
        Assert.Equal("http://localhost:5174/dang-nhap", body.Data.Sheet.LoginUrl);
        Assert.Equal("Em sẽ phải đổi mật khẩu ở lần đăng nhập đầu tiên.", body.Data.Sheet.FirstLoginReminder);
        Assert.Contains("chọn chế độ \"Học sinh\"", string.Join(' ', body.Data.Sheet.Instructions));
        Assert.StartsWith("Mật khẩu tạm chỉ hiện một lần.", body.Data.Sheet.Notice);

        // CSDL: tài khoản vai trò học sinh, tên đăng nhập là mã, không email, buộc đổi mật khẩu; chỉ lưu băm.
        var (user, row) = await factory.WithDbAsync(async db =>
        {
            var s = await db.Students.Include(x => x.User).SingleAsync(x => x.Id == student.Id);
            return (s.User!, s);
        });
        Assert.Equal((Roles.StudentId, student.StudentCode, null, true, UserStatus.ACTIVE, school.OrgId),
            (user.RoleId, user.Username, user.Email, user.MustChangePassword, user.Status, user.OrganizationId));
        Assert.Equal(schoolClass.Id, row.ClassId);
        Assert.DoesNotContain(entry.TemporaryPassword, user.PasswordHash!);

        // Nhật ký một dòng, đối tượng là lớp, có số học sinh, không có mật khẩu.
        var log = await factory.WithDbAsync(db => db.AuditLogs.SingleAsync(l => l.Action == AuditAction.STUDENT_ACCOUNTS_CREATED && l.TargetId == schoolClass.Id));
        Assert.Equal((AuditTargets.Classes, ActorRole.TEACHER, school.Teacher.Id), (log.TargetType, log.ActorRole, log.UserId));
        Assert.Contains("\"count\":1", log.NewValue);
        Assert.DoesNotContain(entry.TemporaryPassword, log.NewValue ?? "");

        // Danh sách của lớp: mật khẩu tạm không còn xem lại được.
        var (list, listBody) = await Http.GetAsync<StudentPageDto>(Url(schoolClass.Id), school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal((1, 1, 60), (listBody.Data!.Items.Count, listBody.Data.ActiveCount, listBody.Data.MaxStudents));
        Assert.DoesNotContain(entry.TemporaryPassword, System.Text.Json.JsonSerializer.Serialize(listBody.Data, HttpTestExtensions.Json));
    }

    [Fact]
    public async Task Students_with_the_same_name_are_allowed_and_told_apart_by_code()
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken);
        var a = (await _c.AddStudentAsync(school.TeacherToken, schoolClass.Id, "Nguyễn Văn An")).Students.Single();
        var b = (await _c.AddStudentAsync(school.TeacherToken, schoolClass.Id, "Nguyễn Văn An")).Students.Single();
        Assert.NotEqual(a.StudentCode, b.StudentCode);

        var (_, byCode) = await Http.GetAsync<StudentPageDto>(Url(schoolClass.Id, $"?keyword={b.StudentCode!.ToLowerInvariant()}"), school.TeacherToken);
        Assert.Equal(b.Id, Assert.Single(byCode.Data!.Items).Id);
        var (_, byName) = await Http.GetAsync<StudentPageDto>(Url(schoolClass.Id, "?keyword=Văn An"), school.TeacherToken);
        Assert.Equal(2, byName.Data!.TotalItems);
    }

    [Fact]
    public async Task Add_student_validates_name()
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken);
        foreach (var name in new[] { "", "   ", new string('A', 256) })
        {
            var (res, body) = await Http.PostAsync<object>(Url(schoolClass.Id), new { fullName = name }, school.TeacherToken);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Contains("fullName", body.Errors!.Keys);
        }
    }

    [Fact]
    public async Task Import_preview_reports_rows_and_import_saves_valid_rows_in_one_transaction()
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken, "7B", 7);
        await _c.AddStudentAsync(school.TeacherToken, schoolClass.Id, "Lê Văn Cường");

        var text = string.Join("\n", "Họ và tên", "Trần Thị Bé", "", "Lê Văn Cường", new string('Q', 256), "Phạm Minh Đức", "Phạm Minh Đức", "");
        var (res, body) = await _c.PreviewFormAsync<ImportPreviewDto>(school.TeacherToken, schoolClass.Id, text);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var preview = body.Data!;
        Assert.Equal((6, 4, 2, 59, 60), (preview.TotalRows, preview.ValidCount, preview.ErrorCount, preview.RemainingCapacity, preview.MaxStudents));
        Assert.Equal([2, 3, 4, 5, 6, 7], preview.Rows.Select(r => r.RowNumber));   // số dòng tính cả dòng tiêu đề
        Assert.Equal(["Họ tên đang trống."], preview.Rows[1].Errors);
        Assert.Equal(["Họ tên dài quá 255 ký tự."], preview.Rows[3].Errors);
        Assert.Equal(StudentImportService.DuplicateNameWarning, Assert.Single(preview.Rows[2].Warnings));   // trùng học sinh đã có
        Assert.Equal(StudentImportService.DuplicateNameWarning, Assert.Single(preview.Rows[5].Warnings));   // trùng dòng trên
        Assert.Equal(2, preview.WarningCount);

        // Xem trước chưa lưu gì.
        Assert.Equal(1, await factory.WithDbAsync(db => db.Students.CountAsync(s => s.ClassId == schoolClass.Id)));

        var rows = preview.Rows.Select(r => new { rowNumber = r.RowNumber, fullName = r.FullName }).ToList();
        var (imp, impBody) = await Http.PostAsync<ImportResultDto>(Url(schoolClass.Id, "/import"), new { rows }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.Created, imp.StatusCode);
        var result = impBody.Data!;
        Assert.Equal((4, 2), (result.AddedCount, result.SkippedCount));
        Assert.Equal("Đã thêm 4 học sinh vào lớp 7B. 2 dòng bị bỏ qua: dòng 3 (Họ tên đang trống); dòng 5 (Họ tên dài quá 255 ký tự).", impBody.Message);
        Assert.Equal(impBody.Message, result.Message);
        Assert.Equal(4, result.Created.Sheet.Entries.Count);
        Assert.Equal(4, result.Created.Sheet.Entries.Select(e => e.StudentCode).Distinct().Count());
        Assert.All(result.Created.Sheet.Entries, e => Assert.Equal("7B", e.ClassName));

        // Một dòng nhật ký cho cả lần nhập, kèm số học sinh.
        var logs = await factory.WithDbAsync(db => db.AuditLogs
            .Where(l => l.Action == AuditAction.STUDENT_ACCOUNTS_CREATED && l.TargetId == schoolClass.Id).ToListAsync());
        Assert.Equal(2, logs.Count);   // một cho em thêm riêng, một cho lần nhập
        Assert.Contains(logs, l => l.NewValue!.Contains("\"count\":4"));
        Assert.Equal(5, await factory.WithDbAsync(db => db.Students.CountAsync(s => s.ClassId == schoolClass.Id)));
    }

    [Fact]
    public async Task Import_from_csv_and_xlsx_files_and_template_downloads()
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken);

        // .csv UTF-8 có BOM do Excel xuất ra, dấu chấm phẩy và ô đặt trong dấu nháy.
        var csv = "﻿Họ và tên;Ghi chú\r\n\"Đỗ Thị Hạnh\";x\r\nVũ Văn Khánh;y\r\n";
        var (csvRes, csvBody) = await _c.PreviewFormAsync<ImportPreviewDto>(school.TeacherToken, schoolClass.Id,
            file: ("ds.csv", Encoding.UTF8.GetBytes(csv)));
        Assert.Equal(HttpStatusCode.OK, csvRes.StatusCode);
        Assert.Equal(["Đỗ Thị Hạnh", "Vũ Văn Khánh"], csvBody.Data!.Rows.Select(r => r.FullName));
        Assert.Equal([2, 3], csvBody.Data.Rows.Select(r => r.RowNumber));

        // .xlsx: cột đầu của bảng tính đầu tiên.
        using var stream = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("Sheet1");
            ws.Cell(1, 1).Value = "Họ và tên";
            ws.Cell(2, 1).Value = "Bùi Thị Lan";
            ws.Cell(3, 1).Value = "Ngô Văn Mạnh";
            ws.Cell(2, 2).Value = "bỏ qua";
            wb.SaveAs(stream);
        }
        var (xlsxRes, xlsxBody) = await _c.PreviewFormAsync<ImportPreviewDto>(school.TeacherToken, schoolClass.Id,
            file: ("ds.xlsx", stream.ToArray()));
        Assert.Equal(HttpStatusCode.OK, xlsxRes.StatusCode);
        Assert.Equal(["Bùi Thị Lan", "Ngô Văn Mạnh"], xlsxBody.Data!.Rows.Select(r => r.FullName));

        // Tệp mẫu tải về đọc lại được và chưa có học sinh nào.
        foreach (var (format, expectedType) in new[] { ("xlsx", "spreadsheetml.sheet"), ("csv", "text/csv") })
        {
            var response = await Http.SendAsync(Auth(HttpMethod.Get, Url(schoolClass.Id, $"/import/template?format={format}"), school.TeacherToken));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(expectedType, response.Content.Headers.ContentType!.ToString());
            Assert.Equal("mau-danh-sach-hoc-sinh." + format, response.Content.Headers.ContentDisposition!.FileName);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            var (back, backBody) = await _c.PreviewFormAsync<object>(school.TeacherToken, schoolClass.Id, file: ("mau." + format, bytes));
            Assert.Equal(HttpStatusCode.BadRequest, back.StatusCode);   // chỉ có dòng tiêu đề: chưa có họ tên nào
            Assert.Equal("REQUIRED", backBody.ErrorCode);
        }
        var (bad, _) = await Http.GetAsync<object>(Url(schoolClass.Id, "/import/template?format=pdf"), school.TeacherToken);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Import_rejects_wrong_file_type_too_many_rows_and_both_sources()
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken);

        var (txt, txtBody) = await _c.PreviewFormAsync<object>(school.TeacherToken, schoolClass.Id, file: ("ds.txt", Encoding.UTF8.GetBytes("An")));
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID"), (txt.StatusCode, txtBody.ErrorCode));
        Assert.Equal("Tệp danh sách không hợp lệ: chỉ nhận tệp .xlsx hoặc .csv UTF-8.", txtBody.Message);

        var (fake, _) = await _c.PreviewFormAsync<object>(school.TeacherToken, schoolClass.Id, file: ("ds.xlsx", Encoding.UTF8.GetBytes("không phải xlsx")));
        Assert.Equal(HttpStatusCode.BadRequest, fake.StatusCode);
        var (latin, latinBody) = await _c.PreviewFormAsync<object>(school.TeacherToken, schoolClass.Id,
            file: ("ds.csv", Encoding.Latin1.GetBytes("Nguyễn Văn An".Replace("ễ", "e").Replace("ă", "à") + "\r\nTrầné\r\n")));
        Assert.Equal(HttpStatusCode.BadRequest, latin.StatusCode);
        Assert.Contains("UTF-8", latinBody.Message);

        var many = string.Join("\n", Enumerable.Range(1, 61).Select(i => "Học sinh " + i));
        var (res, body) = await _c.PreviewFormAsync<object>(school.TeacherToken, schoolClass.Id, many);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("Danh sách học sinh không hợp lệ: tối đa 60 dòng mỗi lần.", body.Message);

        var (both, _) = await _c.PreviewFormAsync<object>(school.TeacherToken, schoolClass.Id, "An", ("ds.csv", Encoding.UTF8.GetBytes("An")));
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        var (empty, emptyBody) = await _c.PreviewFormAsync<object>(school.TeacherToken, schoolClass.Id, "  \n \n");
        Assert.Equal((HttpStatusCode.BadRequest, "REQUIRED"), (empty.StatusCode, emptyBody.ErrorCode));

        var (none, noneBody) = await Http.PostAsync<object>(Url(schoolClass.Id, "/import"), new { rows = new[] { new { rowNumber = 1, fullName = " " } } }, school.TeacherToken);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "BLOCKED"), (none.StatusCode, noneBody.ErrorCode));
        Assert.Equal("Không thể nhập danh sách học sinh. Không có dòng hợp lệ nào để lưu.", noneBody.Message);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Students.CountAsync(s => s.ClassId == schoolClass.Id)));
    }

    [Fact]
    public async Task Class_holds_at_most_60_active_students_and_import_marks_the_overflow_rows()
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken);
        var first = await _c.ImportAsync(school.TeacherToken, schoolClass.Id, Enumerable.Range(1, 58).Select(i => "Học sinh " + i));
        Assert.Equal(58, first.AddedCount);

        // 5 dòng mới nhưng chỉ còn 2 chỗ: 2 dòng đầu hợp lệ, 3 dòng sau báo vượt sĩ số.
        var names = Enumerable.Range(1, 5).Select(i => "Em thêm " + i).ToList();
        var (res, body) = await _c.PreviewFormAsync<ImportPreviewDto>(school.TeacherToken, schoolClass.Id, string.Join("\n", names));
        Assert.Equal((2, 3, 2), (body.Data!.ValidCount, body.Data.ErrorCount, body.Data.RemainingCapacity));
        Assert.Equal(StudentImportService.OverCapacityError, body.Data.Rows[4].Errors.Single());
        var saved = await _c.ImportAsync(school.TeacherToken, schoolClass.Id, names);
        Assert.Equal((2, 3), (saved.AddedCount, saved.SkippedCount));

        var (full, fullBody) = await Http.PostAsync<object>(Url(schoolClass.Id), new { fullName = "Em thứ 61" }, school.TeacherToken);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "BLOCKED"), (full.StatusCode, fullBody.ErrorCode));
        Assert.Equal("Không thể thêm học sinh. Lớp 8A1 đã đủ 60 học sinh.", fullBody.Message);

        // Vô hiệu một em thì có chỗ cho một em mới; mở lại em đó khi lớp đã đủ thì bị chặn.
        var (_, page) = await Http.GetAsync<StudentPageDto>(Url(schoolClass.Id, "?pageSize=100"), school.TeacherToken);
        Assert.Equal((60, 60), (page.Data!.TotalItems, page.Data.ActiveCount));
        var someone = page.Data.Items[0];
        var (deact, _) = await Http.PostAsync<StudentDto>(Url(schoolClass.Id, $"/{someone.Id}/deactivate"), null, school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, deact.StatusCode);
        await _c.AddStudentAsync(school.TeacherToken, schoolClass.Id, "Em thay thế");
        var (reopen, reopenBody) = await Http.PostAsync<object>(Url(schoolClass.Id, $"/{someone.Id}/reactivate"), null, school.TeacherToken);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "BLOCKED"), (reopen.StatusCode, reopenBody.ErrorCode));
    }

    [Fact]
    public async Task Rename_changes_name_but_not_code_and_deactivate_reactivate_delete_follow_the_rules()
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken);
        var created = (await _c.AddStudentAsync(school.TeacherToken, schoolClass.Id, "Hoàng Văn Em")).Students.Single();

        var (ren, renBody) = await Http.PutAsync<StudentDto>(Url(schoolClass.Id, $"/{created.Id}"), new { fullName = "Hoàng Văn Êm" }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, ren.StatusCode);
        Assert.Equal(("Hoàng Văn Êm", created.StudentCode), (renBody.Data!.FullName, renBody.Data.StudentCode));
        Assert.Equal("Hoàng Văn Êm", await factory.WithDbAsync(async db =>
            (await db.Students.Include(s => s.User).SingleAsync(s => s.Id == created.Id)).User!.FullName));

        var (deact, deactBody) = await Http.PostAsync<StudentDto>(Url(schoolClass.Id, $"/{created.Id}/deactivate"), null, school.TeacherToken);
        Assert.Equal((HttpStatusCode.OK, false), (deact.StatusCode, deactBody.Data!.IsActive));
        var (again, againBody) = await Http.PostAsync<object>(Url(schoolClass.Id, $"/{created.Id}/deactivate"), null, school.TeacherToken);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "Không thể vô hiệu học sinh. Học sinh đã bị vô hiệu."), (again.StatusCode, againBody.Message));

        var (inactive, inactiveBody) = await Http.GetAsync<StudentPageDto>(Url(schoolClass.Id, "?status=inactive"), school.TeacherToken);
        Assert.Equal(created.Id, Assert.Single(inactiveBody.Data!.Items).Id);
        Assert.Equal(0, inactiveBody.Data.ActiveCount);
        var (badStatus, _) = await Http.GetAsync<object>(Url(schoolClass.Id, "?status=xyz"), school.TeacherToken);
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);

        var (react, reactBody) = await Http.PostAsync<StudentDto>(Url(schoolClass.Id, $"/{created.Id}/reactivate"), null, school.TeacherToken);
        Assert.Equal((HttpStatusCode.OK, true), (react.StatusCode, reactBody.Data!.IsActive));

        var (del, _) = await Http.DeleteAsync<object>(Url(schoolClass.Id, $"/{created.Id}"), school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var (gone, goneBody) = await Http.GetAsync<StudentPageDto>(Url(schoolClass.Id), school.TeacherToken);
        Assert.Empty(goneBody.Data!.Items);
        var (again404, _) = await Http.PostAsync<object>(Url(schoolClass.Id, $"/{created.Id}/reactivate"), null, school.TeacherToken);
        Assert.Equal(HttpStatusCode.NotFound, again404.StatusCode);   // xóa mềm thì không mở lại được

        // Mã học sinh không cấp lại: dòng vẫn còn trong CSDL (xóa mềm cả học sinh lẫn tài khoản).
        var row = await factory.WithDbAsync(db => db.Students.Include(s => s.User).SingleAsync(s => s.Id == created.Id));
        Assert.NotNull(row.DeletedAt);
        Assert.NotNull(row.User!.DeletedAt);

        var actions = await factory.WithDbAsync(db => db.AuditLogs.Where(l => l.TargetType == AuditTargets.Students && l.TargetId == created.Id)
            .OrderBy(l => l.Id).Select(l => l.Action).ToListAsync());
        Assert.Equal([AuditAction.STUDENT_DEACTIVATED, AuditAction.STUDENT_REACTIVATED, AuditAction.STUDENT_DEACTIVATED], actions);
    }

    [Fact]
    public async Task Student_with_an_attempt_in_progress_cannot_be_deactivated_or_deleted()
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken);
        var student = (await _c.AddStudentAsync(school.TeacherToken, schoolClass.Id, "Đang Làm Bài")).Students.Single();
        await factory.WithDbAsync(async db =>
        {
            var lesson = new Lesson { TeacherId = school.Teacher.Id, ClassId = schoolClass.Id, Title = "Bài 1" };
            db.Lessons.Add(lesson);
            await db.SaveChangesAsync();
            var quiz = new Quiz { LessonId = lesson.Id, CreatedBy = school.Teacher.Id, Name = "Kiểm tra", Status = QuizStatus.PUBLISHED };
            db.Quizzes.Add(quiz);
            await db.SaveChangesAsync();
            db.Attempts.Add(new Attempt { QuizId = quiz.Id, StudentId = student.Id, Status = AttemptStatus.IN_PROGRESS });
            return await db.SaveChangesAsync();
        });

        var (deact, deactBody) = await Http.PostAsync<object>(Url(schoolClass.Id, $"/{student.Id}/deactivate"), null, school.TeacherToken);
        var (del, delBody) = await Http.DeleteAsync<object>(Url(schoolClass.Id, $"/{student.Id}"), school.TeacherToken);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, HttpStatusCode.UnprocessableEntity), (deact.StatusCode, del.StatusCode));
        Assert.Equal("Không thể vô hiệu học sinh. Học sinh Đang Làm Bài đang làm bài kiểm tra. Hãy chờ em nộp bài hoặc hết giờ.", deactBody.Message);
        Assert.Equal("BLOCKED", delBody.ErrorCode);
    }

    [Fact]
    public async Task Resetting_passwords_issues_new_sheets_and_old_password_and_sessions_stop_working()
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken);
        var created = await _c.ImportAsync(school.TeacherToken, schoolClass.Id, ["An", "Bình", "Chi"]);
        var entries = created.Created.Sheet.Entries;
        var token = await _c.ReadyStudentTokenAsync(entries[0], "MatKhauCua123An");

        var ids = created.Created.Students.Select(s => s.Id).Take(2).ToList();
        var (res, body) = await Http.PostAsync<LoginSheetDto>(Url(schoolClass.Id, "/reset-password"), new { studentIds = ids }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Đặt lại mật khẩu thành công.", body.Message);
        var sheet = body.Data!;
        Assert.Equal(2, sheet.Entries.Count);
        Assert.All(sheet.Entries, e => Assert.Matches(@"^[A-HJ-NP-Z2-9a-km-z]{8}$", e.TemporaryPassword));
        Assert.DoesNotContain(sheet.Entries, e => entries.Any(old => old.TemporaryPassword == e.TemporaryPassword));

        // Phiên đang mở của em An bị từ chối ngay; mật khẩu em tự đặt hết dùng; phải đăng nhập lại bằng mật khẩu tạm mới.
        var (me, _) = await Http.GetAsync<object>("/api/v1/student/me", token);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        var (oldLogin, _) = await _c.StudentLoginRawAsync(entries[0].StudentCode!, "MatKhauCua123An");
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        var fresh = sheet.Entries.Single(e => e.StudentCode == entries[0].StudentCode);
        var session = await _c.StudentLoginAsync(fresh.StudentCode!, fresh.TemporaryPassword);
        Assert.True(session.Student.MustChangePassword);

        // Em Chi không được chọn nên giữ mật khẩu tạm cũ.
        var chi = await _c.StudentLoginAsync(entries[2].StudentCode!, entries[2].TemporaryPassword);
        Assert.True(chi.Student.MustChangePassword);

        var log = await factory.WithDbAsync(db => db.AuditLogs.SingleAsync(l => l.Action == AuditAction.STUDENT_PASSWORD_RESET));
        Assert.Equal((AuditTargets.Classes, schoolClass.Id), (log.TargetType, log.TargetId));
        Assert.Contains($"\"count\":2", log.NewValue);
        Assert.All(sheet.Entries, e => Assert.DoesNotContain(e.TemporaryPassword, log.NewValue!));
    }

    [Fact]
    public async Task Reset_unlocks_a_locked_student_and_refuses_inactive_or_foreign_students()
    {
        var school = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken);
        var other = await _c.CreateClassAsync(school.TeacherToken, "8A2");
        var student = (await _c.AddStudentAsync(school.TeacherToken, schoolClass.Id, "Khóa Thử")).Students.Single();
        var foreign = (await _c.AddStudentAsync(school.TeacherToken, other.Id, "Lớp Khác")).Students.Single();

        for (var i = 0; i < 5; i++) await _c.StudentLoginRawAsync(student.StudentCode!, "SaiMatKhau1");
        var (locked, lockedBody) = await _c.StudentLoginRawAsync(student.StudentCode!, "SaiMatKhau1");
        Assert.Equal((HttpStatusCode.Forbidden, "STUDENT_ACCOUNT_BLOCKED"), (locked.StatusCode, lockedBody.ErrorCode));

        var (reset, resetBody) = await Http.PostAsync<LoginSheetDto>(Url(schoolClass.Id, "/reset-password"), new { studentIds = new[] { student.Id } }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var entry = resetBody.Data!.Entries.Single();
        await _c.StudentLoginAsync(entry.StudentCode!, entry.TemporaryPassword);   // tạm khóa đã được gỡ

        var (cross, crossBody) = await Http.PostAsync<object>(Url(schoolClass.Id, "/reset-password"), new { studentIds = new[] { foreign.Id } }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        Assert.Equal("Không thể đặt lại mật khẩu. Không tìm thấy học sinh trong lớp của bạn.", crossBody.Message);

        await Http.PostAsync<object>(Url(schoolClass.Id, $"/{student.Id}/deactivate"), null, school.TeacherToken);
        var (inactive, inactiveBody) = await Http.PostAsync<object>(Url(schoolClass.Id, "/reset-password"), new { studentIds = new[] { student.Id } }, school.TeacherToken);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "BLOCKED"), (inactive.StatusCode, inactiveBody.ErrorCode));
        Assert.Contains("đang bị vô hiệu", inactiveBody.Message);

        foreach (var ids in new object[] { new long[0], Enumerable.Range(1, 61).Select(i => (long)i).ToArray() })
        {
            var (bad, _) = await Http.PostAsync<object>(Url(schoolClass.Id, "/reset-password"), new { studentIds = ids }, school.TeacherToken);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
    }

    [Fact]
    public async Task Other_teachers_org_admin_and_other_schools_cannot_touch_the_students()
    {
        var school = await _c.SchoolAsync();
        var (_, sameSchool) = await _c.AnotherTeacherAsync(school);
        var other = await _c.SchoolAsync();
        var schoolClass = await _c.CreateClassAsync(school.TeacherToken);
        var student = (await _c.AddStudentAsync(school.TeacherToken, schoolClass.Id)).Students.Single();

        foreach (var token in new[] { sameSchool, other.TeacherToken })
        {
            var calls = new (HttpMethod Method, string Url, object? Body)[]
            {
                (HttpMethod.Get, Url(schoolClass.Id), null),
                (HttpMethod.Post, Url(schoolClass.Id), new { fullName = "Xâm nhập" }),
                (HttpMethod.Post, Url(schoolClass.Id, "/import"), new { rows = new[] { new { rowNumber = 1, fullName = "Xâm nhập" } } }),
                (HttpMethod.Post, Url(schoolClass.Id, "/reset-password"), new { studentIds = new[] { student.Id } }),
                (HttpMethod.Put, Url(schoolClass.Id, $"/{student.Id}"), new { fullName = "Đổi tên" }),
                (HttpMethod.Post, Url(schoolClass.Id, $"/{student.Id}/deactivate"), null),
                (HttpMethod.Delete, Url(schoolClass.Id, $"/{student.Id}"), null),
            };
            foreach (var (method, url, body) in calls)
            {
                var (res, _) = await Http.SendJsonAsync<object>(method, url, body, token);
                Assert.True(res.StatusCode == HttpStatusCode.NotFound, $"{method} {url}: {(int)res.StatusCode}");
            }
        }

        // Học sinh của lớp mình nhưng khai nhầm lớp khác của chính mình cũng là 404.
        var mine2 = await _c.CreateClassAsync(school.TeacherToken, "8A2");
        var (wrongClass, _) = await Http.PostAsync<object>(Url(mine2.Id, $"/{student.Id}/deactivate"), null, school.TeacherToken);
        Assert.Equal(HttpStatusCode.NotFound, wrongClass.StatusCode);
        Assert.True(await factory.WithDbAsync(db => db.Students.AnyAsync(x => x.Id == student.Id && x.IsActive && x.DeletedAt == null)));
    }

    private static HttpRequestMessage Auth(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
