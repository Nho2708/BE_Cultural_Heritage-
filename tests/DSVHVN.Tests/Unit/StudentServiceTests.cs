using DSVHVN.Application.Audit;
using DSVHVN.Application.Classes;
using DSVHVN.Application.Common;
using DSVHVN.Application.StudentAuth;
using DSVHVN.Application.Students;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Files;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Tầng service của lớp và học sinh trên SQLite: sinh mã không trùng (thử lại tối đa 5 lần, mã đã xóa không cấp lại), giao dịch tất cả hoặc không có gì,
/// tạm khóa đăng nhập hết sau 15 phút, mật khẩu tạm chỉ lưu bản băm và không lọt vào nhật ký.
/// </summary>
public sealed class StudentServiceTests : IDisposable
{
    private const string Ip = "198.51.100.7";
    private readonly ServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task<(Actor Teacher, SchoolClass Class)> TeacherWithClassAsync(int grade = 8)
    {
        var org = await _h.AddOrganizationAsync();
        var user = await _h.AddUserAsync("giaovien.a", RoleCode.TEACHER, org.Id);
        var actor = new Actor(user.Id, RoleCode.TEACHER, org.Id, Ip);
        var created = await _h.Service<ClassService, ClassDto>(s => s.CreateAsync(actor, new ClassRequest("8A1", grade, "2026-2027"), default));
        return (actor, await _h.QueryAsync(db => db.Classes.SingleAsync(c => c.Id == created.Id)));
    }

    private Task<StudentsCreatedDto> AddAsync(Actor teacher, long classId, string name) =>
        _h.Service<StudentService, StudentsCreatedDto>(s => s.AddAsync(teacher, classId, new AddStudentRequest(name), default));

    private Task<StudentSessionDto> LoginAsync(string code, string password) =>
        _h.Service<StudentAuthService, StudentSessionDto>(s => s.LoginAsync(new StudentLoginRequest(code, password), Ip, default));

    private sealed class ScriptedGenerator(params string[] codes) : IStudentCredentialGenerator
    {
        private readonly Queue<string> _codes = new(codes);
        public int CodesIssued { get; private set; }
        public string NewStudentCode() { CodesIssued++; return _codes.Count > 0 ? _codes.Dequeue() : StudentCredentials.NewStudentCode(); }
        public string NewTemporaryPassword() => StudentCredentials.NewTemporaryPassword();
        public string NewSimplePassword() => StudentCredentials.NewSimplePassword();
    }

    private sealed class ExplodingAudit : IAuditLogger
    {
        public void Record(AuditAction action, string targetType, long? targetId, Actor? actor, object? oldValue = null, object? newValue = null) =>
            throw new InvalidOperationException("Ghi nhật ký thất bại giữa chừng.");
    }

    private (StudentAccountFactory Factory, StudentService Students, StudentImportService Import) Build(IServiceProvider sp,
        IStudentCredentialGenerator generator, IAuditLogger? audit = null)
    {
        var db = sp.GetRequiredService<IAppDbContext>();
        var logger = audit ?? sp.GetRequiredService<IAuditLogger>();
        var factory = new StudentAccountFactory(db, sp.GetRequiredService<IPasswordHasher>(), generator, logger,
            NullLogger<StudentAccountFactory>.Instance);
        var sheets = new LoginSheetBuilder(Options.Create(new StudentSheetOptions()));
        var students = new StudentService(db, sp.GetRequiredService<RequestValidator>(), sp.GetRequiredService<IPasswordHasher>(), generator,
            factory, sheets, sp.GetRequiredService<IAuthThrottle>(), logger, sp.GetRequiredService<TimeProvider>());
        return (factory, students, new StudentImportService(db, new StudentListFileReader(), factory, students));
    }

    [Fact]
    public async Task A_taken_code_is_replaced_by_a_new_one_including_codes_of_deleted_students_and_adult_usernames()
    {
        var (teacher, schoolClass) = await TeacherWithClassAsync();
        var first = (await AddAsync(teacher, schoolClass.Id, "An")).Students.Single();
        await _h.AddUserAsync("HS222222", RoleCode.TEACHER, schoolClass.OrganizationId);   // tên đăng nhập của người khác

        // Xóa mềm học sinh đầu: mã của em không bao giờ cấp lại cho người khác.
        await _h.Service<StudentService>(s => s.DeleteAsync(teacher, schoolClass.Id, first.Id, default));

        var generator = new ScriptedGenerator(first.StudentCode!, "HS222222", "HS333333");
        var created = await _h.RunAsync(sp =>
        {
            var (factory, _, _) = Build(sp, generator);
            return factory.CreateAsync(schoolClass, ["Bình"], teacher, default);
        });
        Assert.Equal("HS333333", created.Single().Student.StudentCode);
        Assert.Equal(3, generator.CodesIssued);
    }

    [Fact]
    public async Task Duplicate_codes_inside_one_batch_are_regenerated()
    {
        var (teacher, schoolClass) = await TeacherWithClassAsync();
        var generator = new ScriptedGenerator("HS444444", "HS444444", "HS555555");
        var created = await _h.RunAsync(sp => Build(sp, generator).Factory.CreateAsync(schoolClass, ["An", "Bình"], teacher, default));
        Assert.Equal(["HS444444", "HS555555"], created.Select(c => c.Student.StudentCode));
    }

    [Fact]
    public async Task When_every_attempt_collides_it_fails_with_a_system_error_and_saves_nothing()
    {
        var (teacher, schoolClass) = await TeacherWithClassAsync();
        var existing = (await AddAsync(teacher, schoolClass.Id, "An")).Students.Single().StudentCode!;
        var generator = new ScriptedGenerator(Enumerable.Repeat(existing, 100).ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.RunAsync(sp =>
            Build(sp, generator).Factory.CreateAsync(schoolClass, ["Bình"], teacher, default)));
        Assert.Equal(1, await _h.QueryAsync(db => db.Students.CountAsync()));
        Assert.Equal(StudentAccountFactory.MaxCodeAttempts, generator.CodesIssued);   // thử đúng 5 lần rồi dừng
    }

    [Fact]
    public async Task Import_is_one_transaction_a_failure_after_the_students_are_saved_rolls_everything_back()
    {
        var (teacher, schoolClass) = await TeacherWithClassAsync();
        var request = new ImportStudentsRequest([new ImportRowRequest(1, "An"), new ImportRowRequest(2, "Bình"), new ImportRowRequest(3, "Chi")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.RunAsync(sp =>
            Build(sp, new RandomStudentCredentialGenerator(), new ExplodingAudit()).Import.ImportAsync(teacher, schoolClass.Id, request, default)));

        Assert.Equal((0, 0), (await _h.QueryAsync(db => db.Students.CountAsync()), await _h.QueryAsync(db => db.Users.CountAsync(u => u.RoleId == Roles.StudentId))));
        Assert.Equal(0, await _h.QueryAsync(db => db.AuditLogs.CountAsync(l => l.Action == AuditAction.STUDENT_ACCOUNTS_CREATED)));

        // Chạy lại bình thường thì lưu đủ.
        var ok = await _h.Service<StudentImportService, ImportResultDto>(s => s.ImportAsync(teacher, schoolClass.Id, request, default));
        Assert.Equal(3, ok.AddedCount);
    }

    [Fact]
    public async Task Temporary_password_is_stored_only_as_a_hash_and_works_for_the_first_login()
    {
        var (teacher, schoolClass) = await TeacherWithClassAsync();
        var created = await AddAsync(teacher, schoolClass.Id, "An");
        var entry = created.Sheet.Entries.Single();

        var user = await _h.QueryAsync(db => db.Users.SingleAsync(u => u.Username == entry.StudentCode));
        Assert.NotEqual(entry.TemporaryPassword, user.PasswordHash);
        Assert.Equal(PasswordCheck.Success, _h.Hasher.Verify(user.PasswordHash!, entry.TemporaryPassword));
        Assert.True(PasswordPolicy.IsSatisfiedBy(entry.TemporaryPassword));

        var allText = string.Join(' ', await _h.QueryAsync(db => db.AuditLogs.Select(l => l.OldValue + " " + l.NewValue).ToListAsync()));
        Assert.DoesNotContain(entry.TemporaryPassword, allText);
        Assert.DoesNotContain(user.PasswordHash!, allText);

        var session = await LoginAsync(entry.StudentCode!, entry.TemporaryPassword);
        Assert.True(session.Student.MustChangePassword);
        Assert.Equal(_h.Clock.GetUtcNow().UtcDateTime, (await _h.QueryAsync(db => db.Users.SingleAsync(u => u.Id == user.Id))).LastLoginAt);
    }

    [Fact]
    public async Task Student_login_lock_lasts_15_minutes_and_only_wrong_passwords_inside_the_window_count()
    {
        var (teacher, schoolClass) = await TeacherWithClassAsync();
        var entry = (await AddAsync(teacher, schoolClass.Id, "An")).Sheet.Entries.Single();
        Task<AppException> Wrong() => Assert.ThrowsAsync<AppException>(() => LoginAsync(entry.StudentCode!, "SaiMatKhau1"));

        // Bốn lần sai rồi đúng thì đếm về 0.
        for (var i = 0; i < 4; i++) Assert.Equal(401, (await Wrong()).StatusCode);
        await LoginAsync(entry.StudentCode!, entry.TemporaryPassword);
        for (var i = 0; i < 4; i++) Assert.Equal(401, (await Wrong()).StatusCode);

        // Lần sai thứ năm tạm khóa; còn 15 phút.
        var locked = await Wrong();
        Assert.Equal((403, "STUDENT_ACCOUNT_BLOCKED"), (locked.StatusCode, locked.AppMessage.Code));
        _h.Clock.Advance(TimeSpan.FromMinutes(14));
        var still = await Assert.ThrowsAsync<AppException>(() => LoginAsync(entry.StudentCode!, entry.TemporaryPassword));
        Assert.Contains("thử lại sau 1 phút", still.AppMessage.Text);

        _h.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.NotNull(await LoginAsync(entry.StudentCode!, entry.TemporaryPassword));

        var audit = await _h.QueryAsync(db => db.AuditLogs.Where(l => l.Action == AuditAction.LOGIN_LOCKED_OUT).ToListAsync());
        Assert.Equal(ActorRole.STUDENT, Assert.Single(audit).ActorRole);
    }

    [Fact]
    public async Task Unknown_codes_are_throttled_by_the_typed_text_without_revealing_which_codes_exist()
    {
        for (var i = 0; i < 4; i++)
            Assert.Equal(401, (await Assert.ThrowsAsync<AppException>(() => LoginAsync("hs abcdef", "SaiMatKhau1"))).StatusCode);
        var fifth = await Assert.ThrowsAsync<AppException>(() => LoginAsync("HS-ABCDEF", "SaiMatKhau1"));
        Assert.Equal((403, "STUDENT_ACCOUNT_BLOCKED"), (fifth.StatusCode, fifth.AppMessage.Code));
        Assert.Equal(0, await _h.QueryAsync(db => db.AuditLogs.CountAsync(l => l.Action == AuditAction.LOGIN_LOCKED_OUT)));   // không có tài khoản để ghi
    }

    [Fact]
    public async Task Forced_change_skips_the_current_password_and_voluntary_change_requires_it()
    {
        var (teacher, schoolClass) = await TeacherWithClassAsync();
        var entry = (await AddAsync(teacher, schoolClass.Id, "An")).Sheet.Entries.Single();
        var session = await LoginAsync(entry.StudentCode!, entry.TemporaryPassword);
        var actor = new Actor(session.Student.UserId, RoleCode.STUDENT, teacher.OrganizationId, Ip);

        Task<StudentSessionDto> Change(string? current, string next, string? confirm = null) =>
            _h.Service<StudentAuthService, StudentSessionDto>(s => s.ChangePasswordAsync(actor, new StudentPasswordRequest(current, next, confirm ?? next), default));

        var ex = await Assert.ThrowsAsync<AppException>(() => Change(null, entry.TemporaryPassword));
        Assert.Equal("SAME_PASSWORD", ex.AppMessage.Code);

        var changed = await Change(null, "MatKhauMoi2026");
        Assert.False(changed.Student.MustChangePassword);
        Assert.NotEqual(session.AccessToken, changed.AccessToken);
        Assert.NotEqual(SecurityStamps.From(entry.TemporaryPassword), SecurityStamps.From("MatKhauMoi2026"));

        // Tự đổi: cần mật khẩu hiện tại; không dùng được mật khẩu tạm cũ làm "mật khẩu hiện tại".
        Assert.Equal("REQUIRED", (await Assert.ThrowsAsync<AppException>(() => Change(null, "MatKhauKhac2026"))).AppMessage.Code);
        Assert.Equal("INVALID", (await Assert.ThrowsAsync<AppException>(() => Change(entry.TemporaryPassword, "MatKhauKhac2026"))).AppMessage.Code);
        Assert.NotNull(await Change("MatKhauMoi2026", "MatKhauKhac2026"));
    }

    [Fact]
    public async Task Capacity_counts_only_active_students_and_a_full_class_blocks_reactivation()
    {
        var (teacher, schoolClass) = await TeacherWithClassAsync();
        var names = Enumerable.Range(1, ClassRules.MaxActiveStudents).Select(i => new ImportRowRequest(i, "Học sinh " + i)).ToList();
        var imported = await _h.Service<StudentImportService, ImportResultDto>(s => s.ImportAsync(teacher, schoolClass.Id, new ImportStudentsRequest(names), default));
        Assert.Equal(60, imported.AddedCount);

        var full = await Assert.ThrowsAsync<AppException>(() => AddAsync(teacher, schoolClass.Id, "Em thứ 61"));
        Assert.Equal((422, "Không thể thêm học sinh. Lớp 8A1 đã đủ 60 học sinh."), (full.StatusCode, full.AppMessage.Text));

        var one = imported.Created.Students[0];
        await _h.Service<StudentService, StudentDto>(s => s.DeactivateAsync(teacher, schoolClass.Id, one.Id, default));
        await AddAsync(teacher, schoolClass.Id, "Em thay thế");
        var blocked = await Assert.ThrowsAsync<AppException>(() =>
            _h.Service<StudentService, StudentDto>(s => s.ReactivateAsync(teacher, schoolClass.Id, one.Id, default)));
        Assert.Contains("đã đủ 60 học sinh", blocked.AppMessage.Text);
    }

    [Fact]
    public async Task Non_teachers_cannot_use_the_class_and_student_services()
    {
        var (teacher, schoolClass) = await TeacherWithClassAsync();
        foreach (var role in new[] { RoleCode.ORG_ADMIN, RoleCode.ADMIN, RoleCode.STUDENT })
        {
            var actor = new Actor(teacher.UserId, role, teacher.OrganizationId, Ip);
            var ex = await Assert.ThrowsAsync<AppException>(() => AddAsync(actor, schoolClass.Id, "Xâm nhập"));
            Assert.Equal(403, ex.StatusCode);
            var list = await Assert.ThrowsAsync<AppException>(() =>
                _h.Service<ClassService, ClassPageDto>(s => s.ListAsync(actor, new ClassListQuery(null, null, null, null, null), default)));
            Assert.Equal(403, list.StatusCode);
        }
        Assert.Equal(0, await _h.QueryAsync(db => db.Students.CountAsync()));
    }

    [Fact]
    public async Task Login_is_refused_for_every_blocked_situation_with_the_same_wording_and_only_after_the_password_is_right()
    {
        var (teacher, schoolClass) = await TeacherWithClassAsync();
        var entry = (await AddAsync(teacher, schoolClass.Id, "An")).Sheet.Entries.Single();
        var askTeacher = "Tài khoản của em tạm thời chưa đăng nhập được. Em hãy báo thầy cô phụ trách lớp.";

        async Task ExpectBlocked(string password = "")
        {
            var ex = await Assert.ThrowsAsync<AppException>(() => LoginAsync(entry.StudentCode!, password.Length > 0 ? password : entry.TemporaryPassword));
            Assert.Equal((403, askTeacher), (ex.StatusCode, ex.AppMessage.Text));
        }

        // Tài khoản ngừng sử dụng.
        await _h.QueryAsync(async db => { (await db.Users.SingleAsync(u => u.Username == entry.StudentCode)).Status = UserStatus.INACTIVE; return await db.SaveChangesAsync(); });
        await ExpectBlocked();
        await _h.QueryAsync(async db => { (await db.Users.SingleAsync(u => u.Username == entry.StudentCode)).Status = UserStatus.ACTIVE; return await db.SaveChangesAsync(); });

        // Học sinh bị vô hiệu: sai mật khẩu vẫn nhận thông điệp sai thông tin, không lộ trạng thái.
        await _h.QueryAsync(async db => { (await db.Students.SingleAsync(s => s.StudentCode == entry.StudentCode)).IsActive = false; return await db.SaveChangesAsync(); });
        await ExpectBlocked();
        var wrong = await Assert.ThrowsAsync<AppException>(() => LoginAsync(entry.StudentCode!, "SaiMatKhau1"));
        Assert.Equal((401, "STUDENT_INVALID_CREDENTIALS"), (wrong.StatusCode, wrong.AppMessage.Code));
        await _h.QueryAsync(async db => { (await db.Students.SingleAsync(s => s.StudentCode == entry.StudentCode)).IsActive = true; return await db.SaveChangesAsync(); });

        // Lớp đã xóa.
        await _h.QueryAsync(async db => { (await db.Classes.SingleAsync()).DeletedAt = DateTime.UtcNow; return await db.SaveChangesAsync(); });
        await ExpectBlocked();
        await _h.QueryAsync(async db => { (await db.Classes.SingleAsync()).DeletedAt = null; return await db.SaveChangesAsync(); });

        // Trường đã ngừng.
        await _h.QueryAsync(async db => { (await db.Organizations.SingleAsync()).DeletedAt = DateTime.UtcNow; return await db.SaveChangesAsync(); });
        await ExpectBlocked();
        await _h.QueryAsync(async db => { (await db.Organizations.SingleAsync()).DeletedAt = null; return await db.SaveChangesAsync(); });

        Assert.NotNull(await LoginAsync(entry.StudentCode!, entry.TemporaryPassword));
    }

    [Fact]
    public async Task ForgotPassword_fails_for_unknown_student_and_notifies_teacher_when_found()
    {
        var (teacher, schoolClass) = await TeacherWithClassAsync();
        var entry = (await AddAsync(teacher, schoolClass.Id, "Nguyễn Văn A")).Sheet.Entries.Single();
        var studentCode = entry.StudentCode!;

        // Case 1: Unknown student code -> 404
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            _h.Service<StudentAuthService, StudentForgotPasswordResultDto>(s =>
                s.ForgotPasswordAsync(new StudentForgotPasswordRequest("HSUNKNOWN"), default)));
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal(Messages.StudentAccountNotFound.Text, ex.Message);

        // Case 2: Known student code -> sends email to teacher
        var result = await _h.Service<StudentAuthService, StudentForgotPasswordResultDto>(s =>
            s.ForgotPasswordAsync(new StudentForgotPasswordRequest(studentCode), default));
        Assert.Equal("Nguyễn Văn A", result.StudentName);
        Assert.Equal(1, _h.Mail.CountTo("giaovien.a@example.vn"));
    }
}
