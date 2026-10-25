using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.StudentAuth;

/// <summary>
/// Đăng nhập bằng mã học sinh, đổi mật khẩu và xem lớp của em, dùng chung cho khu học của web học tập và chế độ Học sinh của app.
/// Tài khoản người lớn đăng nhập ở đây nhận đúng thông điệp sai thông tin đăng nhập, như mã không tồn tại.
/// </summary>
public sealed class StudentAuthService(
    IAppDbContext db,
    RequestValidator validator,
    IPasswordHasher hasher,
    ITokenIssuer tokenIssuer,
    IAuthThrottle throttle,
    IAuditLogger audit,
    TimeProvider clock)
{
    /// <summary>
    /// Sai thông tin luôn trả cùng một thông điệp (không cho biết sai ô nào). Sai 5 lần liên tiếp trong 15 phút thì tạm khóa đăng nhập
    /// 15 phút (bộ đếm trong bộ nhớ máy chủ, không đổi trạng thái tài khoản) và ghi nhật ký. Đúng mật khẩu mới xét học sinh, lớp,
    /// trường để người đoán mật khẩu không biết tài khoản bị vô hiệu. Thành công: ghi <c>last_login_at</c> trước khi phát token
    /// (không ghi nhật ký lần đăng nhập thành công của học sinh); token kèm cấp học của lớp.
    /// </summary>
    public async Task<StudentSessionDto> LoginAsync(StudentLoginRequest request, string? ipAddress, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var code = StudentCredentials.NormalizeStudentCode(request.StudentCode);

        var user = await db.Users.Include(u => u.Organization)
            .SingleOrDefaultAsync(u => u.DeletedAt == null && u.RoleId == Roles.StudentId && u.Username == code, ct);

        // Đếm theo tài khoản khi tìm thấy; mã không tồn tại đếm theo chuỗi đã nhập để không lộ mã nào có thật.
        var throttleKey = user is null ? "login:" + code : "user:" + user.Id;
        if (throttle.GetLoginLockout(throttleKey) is { } remaining)
            throw AppException.Forbidden(Messages.StudentCannotSignIn(Messages.StudentBlockedReasons.TemporaryLock(remaining)));

        var check = PasswordCheck.Failed;
        if (user?.PasswordHash is { } passwordHash) check = hasher.Verify(passwordHash, request.Password!);
        else hasher.VerifyAgainstDummy(request.Password!);

        if (user is null || check == PasswordCheck.Failed)
        {
            if (!throttle.RegisterFailedLogin(throttleKey)) throw AppException.Unauthorized(Messages.StudentInvalidCredentials);

            if (user is not null)
            {
                audit.Record(AuditAction.LOGIN_LOCKED_OUT, AuditTargets.Users, user.Id, ActorOf(user, ipAddress),
                    newValue: new { lockedMinutes = (int)AccountRules.LoginLockoutDuration.TotalMinutes });
                await db.SaveChangesAsync(ct);
            }
            throw AppException.Forbidden(Messages.StudentCannotSignIn(
                Messages.StudentBlockedReasons.TemporaryLock(AccountRules.LoginLockoutDuration)));
        }

        var student = await FindStudentAsync(user.Id, tracking: false, ct);
        if (StudentAccess.BlockedReason(user.Status, user.Organization?.DeletedAt is not null, student?.IsActive,
                student?.DeletedAt is not null, student?.Class?.DeletedAt is not null) is { } reason)
            throw AppException.Forbidden(Messages.StudentCannotSignIn(reason));

        throttle.ResetFailedLogins(throttleKey);
        if (check == PasswordCheck.SuccessRehashNeeded) user.PasswordHash = hasher.Hash(request.Password!);
        user.LastLoginAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        return Session(user, student!);
    }

    /// <summary>Thông tin của em và lớp, kể cả khi đang bị buộc đổi mật khẩu (giao diện cần tên để chào ở màn hình Đổi mật khẩu).</summary>
    public async Task<StudentProfileDto> GetProfileAsync(Actor actor, CancellationToken ct)
    {
        var student = await FindStudentAsync(actor.UserId, tracking: false, ct)
                      ?? throw AppException.Unauthorized(Messages.Forbidden);
        return Profile(student.User!, student);
    }

    /// <summary>Lớp của em: tên, khối, cấp học, năm học, giáo viên phụ trách, trường. Không có danh sách bạn cùng lớp.</summary>
    public async Task<StudentClassDto> GetClassAsync(Actor actor, CancellationToken ct)
    {
        var student = await FindStudentAsync(actor.UserId, tracking: false, ct)
                      ?? throw AppException.Unauthorized(Messages.Forbidden);
        return StudentClassDto.From(student.Class!);
    }

    /// <summary>
    /// Đổi mật khẩu (mật khẩu mới dài 6-64 ký tự với lớp cấp 1, theo quy tắc mật khẩu chung với cấp 2, 3). Chế độ bắt buộc (đang bị buộc đổi, lần đầu hoặc sau khi giáo viên đặt lại): không hỏi mật khẩu hiện tại vì token này
    /// chỉ có được bằng cách vừa đăng nhập với mật khẩu tạm; mật khẩu mới phải khác mật khẩu tạm. Chế độ tự đổi: phải nhập đúng mật khẩu
    /// hiện tại và mật khẩu mới phải khác. Đổi xong tắt cờ buộc đổi, trả token mới trong cùng phản hồi (dấu bảo mật đổi nên token cũ
    /// và phiên trên thiết bị khác hết dùng), học sinh vào thẳng trang học.
    /// </summary>
    public async Task<StudentSessionDto> ChangePasswordAsync(Actor actor, StudentPasswordRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var student = await FindStudentAsync(actor.UserId, tracking: true, ct)
                      ?? throw AppException.Unauthorized(Messages.Forbidden);
        var user = student.User!;

        // Lớp cấp 1: mật khẩu mới chỉ cần dài 6-64 ký tự; cấp 2, 3 theo quy tắc mật khẩu chung.
        var level = SchoolClass.SchoolLevelOf(student.Class!.Grade);
        if (!PasswordPolicy.IsSatisfiedByStudent(request.NewPassword, level))
            throw AppException.Validation("newPassword", level == 1 ? Messages.PasswordLengthOnly : Messages.PasswordPolicy);

        if (user.MustChangePassword)
        {
            // So mật khẩu mới với mật khẩu tạm (bản băm đang lưu).
            if (user.PasswordHash is { } temporaryHash && hasher.Verify(temporaryHash, request.NewPassword!) != PasswordCheck.Failed)
                throw AppException.Validation("newPassword", Messages.SamePassword);
        }
        else
        {
            if (string.IsNullOrEmpty(request.CurrentPassword))
                throw AppException.Validation("currentPassword", Messages.Required("mật khẩu hiện tại"));
            if (user.PasswordHash is null || hasher.Verify(user.PasswordHash, request.CurrentPassword) == PasswordCheck.Failed)
                throw AppException.Validation("currentPassword",
                    Messages.Invalid("Mật khẩu hiện tại", "không khớp với mật khẩu đang dùng"));
            if (request.NewPassword == request.CurrentPassword)
                throw AppException.Validation("newPassword", Messages.SamePassword);
        }

        user.PasswordHash = hasher.Hash(request.NewPassword!);
        user.MustChangePassword = false;
        await db.SaveChangesAsync(ct);
        throttle.ResetFailedLogins("user:" + user.Id);
        return Session(user, student);
    }

    private Task<Student?> FindStudentAsync(long userId, bool tracking, CancellationToken ct)
    {
        var query = db.Students
            .Include(s => s.User).ThenInclude(u => u!.Organization)
            .Include(s => s.Class).ThenInclude(c => c!.Teacher)
            .Include(s => s.Class).ThenInclude(c => c!.Organization)
            .Where(s => s.UserId == userId);
        if (!tracking) query = query.AsNoTracking();
        return query.SingleOrDefaultAsync(ct);
    }

    private StudentSessionDto Session(User user, Student student)
    {
        var level = SchoolClass.SchoolLevelOf(student.Class!.Grade);
        var issued = tokenIssuer.CreateAccessToken(new AccessTokenSubject(
            user.Id, user.Username ?? string.Empty, user.FullName ?? string.Empty, user.RoleCode, user.OrganizationId,
            SecurityStamps.From(user.PasswordHash), level));
        return new StudentSessionDto("Bearer", issued.Token, issued.ExpiresAt, Profile(user, student));
    }

    private static StudentProfileDto Profile(User user, Student student)
    {
        var schoolClass = student.Class!;
        var level = SchoolClass.SchoolLevelOf(schoolClass.Grade);
        return new StudentProfileDto(user.Id, student.Id, student.StudentCode, student.FullName ?? user.FullName, user.RoleCode,
            user.MustChangePassword, level, SchoolLevels.NameOf(level), StudentClassDto.From(schoolClass), user.LastLoginAt,
            student.CreatedAt);
    }

    private static Actor ActorOf(User user, string? ipAddress) => new(user.Id, user.RoleCode, user.OrganizationId, ipAddress);
}
