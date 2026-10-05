using DSVHVN.Application.Audit;
using DSVHVN.Application.Classes;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Students;

/// <summary>
/// Quản lý học sinh của lớp (tab Học sinh trong chi tiết lớp) cho giáo viên phụ trách lớp: xem danh sách, thêm từng em
/// (thêm học sinh là tạo tài khoản), sửa họ tên, vô hiệu và mở lại, xóa mềm, đặt lại mật khẩu. Không cần gói hiệu lực.
/// Quản trị trường và quản trị hệ thống không dùng được các thao tác này. Mọi thao tác lọc theo lớp của giáo viên;
/// học sinh của lớp khác trả 404 như không tồn tại.
/// </summary>
public sealed class StudentService(
    IAppDbContext db,
    RequestValidator validator,
    IPasswordHasher hasher,
    IStudentCredentialGenerator generator,
    StudentAccountFactory factory,
    LoginSheetBuilder sheets,
    IAuthThrottle throttle,
    IAuditLogger audit,
    TimeProvider clock)
{
    public async Task<StudentPageDto> ListAsync(Actor actor, long classId, StudentListQuery query, CancellationToken ct)
    {
        await db.FindClassAsync(actor, classId, "xem học sinh", tracking: false, ct);

        var students = db.Students.AsNoTracking().Include(s => s.User)
            .Where(s => s.ClassId == classId && s.DeletedAt == null);
        switch (query.Status?.Trim().ToLowerInvariant())
        {
            case null or "" or "all": break;
            case "active": students = students.Where(s => s.IsActive); break;
            case "inactive": students = students.Where(s => !s.IsActive); break;
            default:
                throw AppException.Validation("status", Messages.Invalid("Trạng thái", "chỉ nhận active, inactive hoặc all"));
        }
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            var code = StudentCredentials.NormalizeStudentCode(keyword);
            students = students.Where(s => (s.FullName != null && s.FullName.Contains(keyword))
                                           || (s.StudentCode != null && s.StudentCode.Contains(code)));
        }

        var page = await students.OrderBy(s => s.FullName).ThenBy(s => s.StudentCode)
            .ToPagedAsync(query.Page, query.PageSize, ct);
        var active = await CountActiveAsync(classId, ct);
        return new StudentPageDto(page.Items.Select(StudentDto.From).ToList(), page.Page, page.PageSize, page.TotalItems,
            page.TotalPages, active, ClassRules.MaxActiveStudents);
    }

    /// <summary>
    /// Thêm từng em: trong một giao dịch có khóa lớp — kiểm sĩ số (tối đa 60 học sinh đang hoạt động), sinh mã học sinh,
    /// mật khẩu tạm, tạo tài khoản và học sinh, ghi nhật ký. Trả phiếu đăng nhập có mật khẩu tạm (chỉ hiện lần này).
    /// </summary>
    public async Task<StudentsCreatedDto> AddAsync(Actor actor, long classId, AddStudentRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var name = StudentNames.Clean(request.FullName);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockClassAsync(classId, ct);
        var schoolClass = await db.FindClassAsync(actor, classId, "thêm học sinh", tracking: true, ct);
        await EnsureCapacityAsync(schoolClass, 1, ct);
        var created = await factory.CreateAsync(schoolClass, [name], actor, ct);
        await tx.CommitAsync(ct);
        return ToCreatedDto(schoolClass, created);
    }

    public async Task<StudentDto> RenameAsync(Actor actor, long classId, long studentId, RenameStudentRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        await db.FindClassAsync(actor, classId, "sửa học sinh", tracking: false, ct);
        var student = await FindStudentAsync(classId, studentId, "sửa học sinh", ct);

        var name = StudentNames.Clean(request.FullName);
        student.FullName = name;
        student.User!.FullName = name;
        await db.SaveChangesAsync(ct);
        return StudentDto.From(student);
    }

    /// <summary>
    /// Vô hiệu học sinh (<c>students.is_active</c> = 0): không đăng nhập, không mở bài, không làm bài được; kết quả cũ giữ nguyên;
    /// mở lại được. Không vô hiệu học sinh đang có lượt làm bài dở.
    /// </summary>
    public async Task<StudentDto> DeactivateAsync(Actor actor, long classId, long studentId, CancellationToken ct)
    {
        await db.FindClassAsync(actor, classId, "vô hiệu học sinh", tracking: false, ct);
        var student = await FindStudentAsync(classId, studentId, "vô hiệu học sinh", ct);
        if (!student.IsActive)
            throw AppException.BusinessRule(Messages.Blocked("vô hiệu học sinh", "Học sinh đã bị vô hiệu."));
        await EnsureNoAttemptInProgressAsync(student, "vô hiệu học sinh", ct);

        student.IsActive = false;
        audit.Record(AuditAction.STUDENT_DEACTIVATED, AuditTargets.Students, student.Id, actor,
            oldValue: new { isActive = true }, newValue: new { isActive = false, studentCode = student.StudentCode });
        await db.SaveChangesAsync(ct);
        return StudentDto.From(student);
    }

    /// <summary>Mở lại học sinh đã vô hiệu; lớp vẫn chỉ được tối đa 60 học sinh đang hoạt động.</summary>
    public async Task<StudentDto> ReactivateAsync(Actor actor, long classId, long studentId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockClassAsync(classId, ct);
        var schoolClass = await db.FindClassAsync(actor, classId, "mở lại học sinh", tracking: false, ct);
        var student = await FindStudentAsync(classId, studentId, "mở lại học sinh", ct);
        if (student.IsActive)
            throw AppException.BusinessRule(Messages.Blocked("mở lại học sinh", "Học sinh đang hoạt động."));
        await EnsureCapacityAsync(schoolClass, 1, ct);

        student.IsActive = true;
        audit.Record(AuditAction.STUDENT_REACTIVATED, AuditTargets.Students, student.Id, actor,
            oldValue: new { isActive = false }, newValue: new { isActive = true, studentCode = student.StudentCode });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return StudentDto.From(student);
    }

    /// <summary>
    /// Xóa mềm học sinh cùng tài khoản: ẩn khỏi danh sách lớp, không mở lại được; mã học sinh không cấp lại; kết quả cũ giữ nguyên.
    /// Không xóa học sinh đang có lượt làm bài dở.
    /// </summary>
    public async Task DeleteAsync(Actor actor, long classId, long studentId, CancellationToken ct)
    {
        await db.FindClassAsync(actor, classId, "xóa học sinh", tracking: false, ct);
        var student = await FindStudentAsync(classId, studentId, "xóa học sinh", ct);
        await EnsureNoAttemptInProgressAsync(student, "xóa học sinh", ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var wasActive = student.IsActive;
        student.IsActive = false;
        student.DeletedAt = now;
        student.User!.DeletedAt = now;
        audit.Record(AuditAction.STUDENT_DEACTIVATED, AuditTargets.Students, student.Id, actor,
            oldValue: new { isActive = wasActive }, newValue: new { deleted = true, studentCode = student.StudentCode });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Đặt lại mật khẩu một hoặc nhiều học sinh đang hoạt động của lớp trong một giao dịch: mật khẩu tạm mới, thay bản băm cũ,
    /// bật buộc đổi mật khẩu, gỡ tạm khóa đăng nhập. Mật khẩu cũ và mọi phiên đang mở hết dùng ngay (dấu bảo mật đổi theo mật khẩu).
    /// Học sinh bị vô hiệu phải được mở lại trước. Ghi một dòng nhật ký kèm danh sách học sinh.
    /// </summary>
    public async Task<LoginSheetDto> ResetPasswordsAsync(Actor actor, long classId, ResetPasswordsRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var ids = request.StudentIds!.Distinct().ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var schoolClass = await db.FindClassAsync(actor, classId, "đặt lại mật khẩu", tracking: false, ct);
        var students = await db.Students.Include(s => s.User)
            .Where(s => ids.Contains(s.Id) && s.ClassId == classId && s.DeletedAt == null)
            .OrderBy(s => s.FullName).ThenBy(s => s.StudentCode)
            .ToListAsync(ct);
        if (students.Count != ids.Count)
            throw AppException.NotFound(Messages.NotFound("đặt lại mật khẩu", "học sinh trong lớp của bạn"));
        if (students.FirstOrDefault(s => !s.IsActive) is { } inactive)
            throw AppException.BusinessRule(Messages.Blocked("đặt lại mật khẩu",
                $"Học sinh {inactive.FullName} đang bị vô hiệu. Hãy mở lại học sinh trước khi đặt lại mật khẩu."));

        var passwords = students.Select(_ => generator.NewTemporaryPassword()).ToArray();
        var hashes = new string[passwords.Length];
        Parallel.For(0, passwords.Length, i => hashes[i] = hasher.Hash(passwords[i]));
        for (var i = 0; i < students.Count; i++)
        {
            students[i].User!.PasswordHash = hashes[i];
            students[i].User!.MustChangePassword = true;
        }

        audit.Record(AuditAction.STUDENT_PASSWORD_RESET, AuditTargets.Classes, classId, actor,
            newValue: new { classId, className = schoolClass.Name, studentIds = students.Select(s => s.Id).ToList(), count = students.Count });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Gỡ tạm khóa đăng nhập ngay sau khi lưu; tạm khóa chỉ nằm trong bộ nhớ máy chủ.
        foreach (var student in students) throttle.ResetFailedLogins("user:" + student.UserId);

        return sheets.Build(schoolClass.Name, students.Select((s, i) => new LoginSheetEntryDto(
            s.Id, s.FullName, schoolClass.Name, s.StudentCode, passwords[i])));
    }

    internal StudentsCreatedDto ToCreatedDto(SchoolClass schoolClass, IReadOnlyList<NewStudentAccount> created) =>
        new(created.Select(c => StudentDto.From(c.Student)).ToList(),
            sheets.Build(schoolClass.Name, created.Select(c => new LoginSheetEntryDto(
                c.Student.Id, c.Student.FullName, schoolClass.Name, c.Student.StudentCode, c.TemporaryPassword))));

    /// <summary>Chặn khi thêm <paramref name="adding"/> em nữa làm lớp vượt 60 học sinh đang hoạt động. Gọi trong giao dịch đã khóa lớp.</summary>
    internal async Task EnsureCapacityAsync(SchoolClass schoolClass, int adding, CancellationToken ct)
    {
        var used = await CountActiveAsync(schoolClass.Id, ct);
        var remaining = ClassRules.MaxActiveStudents - used;
        if (remaining >= adding) return;
        throw AppException.BusinessRule(Messages.Blocked("thêm học sinh", remaining <= 0
            ? $"Lớp {schoolClass.Name} đã đủ {ClassRules.MaxActiveStudents} học sinh."
            : $"Lớp {schoolClass.Name} chỉ còn chỗ cho {remaining} học sinh (tối đa {ClassRules.MaxActiveStudents} học sinh đang hoạt động)."));
    }

    internal Task<int> CountActiveAsync(long classId, CancellationToken ct) =>
        db.Students.CountAsync(s => s.ClassId == classId && s.DeletedAt == null && s.IsActive, ct);

    private async Task<Student> FindStudentAsync(long classId, long studentId, string action, CancellationToken ct) =>
        await db.Students.Include(s => s.User)
            .SingleOrDefaultAsync(s => s.Id == studentId && s.ClassId == classId && s.DeletedAt == null, ct)
        ?? throw AppException.NotFound(Messages.NotFound(action, "học sinh trong lớp của bạn"));

    private async Task EnsureNoAttemptInProgressAsync(Student student, string action, CancellationToken ct)
    {
        if (await db.Attempts.AnyAsync(a => a.StudentId == student.Id && a.Status == AttemptStatus.IN_PROGRESS, ct))
            throw AppException.BusinessRule(Messages.Blocked(action,
                $"Học sinh {student.FullName} đang làm bài kiểm tra. Hãy chờ em nộp bài hoặc hết giờ."));
    }
}
