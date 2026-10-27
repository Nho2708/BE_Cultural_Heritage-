using DSVHVN.Application.Auth;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Rules;
using FluentValidation;

namespace DSVHVN.Application.StudentAuth;

/// <summary>Màn hình Đăng nhập học sinh: mã học sinh (tự viết hoa, bỏ khoảng trắng và dấu gạch) và mật khẩu.</summary>
public sealed record StudentLoginRequest(string? StudentCode, string? Password);

/// <summary>
/// Màn hình Đổi mật khẩu của học sinh. Chế độ bắt buộc (đang bị buộc đổi mật khẩu) không hỏi lại mật khẩu hiện tại;
/// chế độ tự đổi thì bắt buộc nhập <see cref="CurrentPassword"/>. Mật khẩu mới nhập hai lần.
/// </summary>
public sealed record StudentPasswordRequest(string? CurrentPassword, string? NewPassword, string? ConfirmNewPassword);

/// <summary>Lớp của em: cấp học suy từ khối lớp, quyết định bộ giao diện và luồng học.</summary>
public sealed record StudentClassDto(
    long Id,
    string? Name,
    int Grade,
    int SchoolLevel,
    string SchoolLevelName,
    string? SchoolYear,
    string? TeacherName,
    OrganizationRefDto? Organization)
{
    public static StudentClassDto From(SchoolClass c)
    {
        var level = SchoolClass.SchoolLevelOf(c.Grade);
        return new StudentClassDto(c.Id, c.Name, c.Grade, level, SchoolLevels.NameOf(level), c.SchoolYear, c.Teacher?.FullName,
            c.Organization is null ? null : new OrganizationRefDto(c.Organization.Id, c.Organization.Name));
    }
}

/// <summary>
/// Thông tin của học sinh đang đăng nhập. <see cref="MustChangePassword"/> bật thì giao diện chỉ mở màn hình Đổi mật khẩu.
/// Chỉ có họ tên, mã học sinh và lớp: không lưu ngày sinh, email, số điện thoại của học sinh.
/// </summary>
public sealed record StudentProfileDto(
    long UserId,
    long StudentId,
    string? StudentCode,
    string? FullName,
    RoleCode Role,
    bool MustChangePassword,
    int SchoolLevel,
    string SchoolLevelName,
    StudentClassDto Class,
    DateTime? LastLoginAt,
    DateTime? CreatedAt);

/// <summary>Kết quả đăng nhập và đổi mật khẩu của học sinh: access token hạn 8 giờ kèm thông tin học sinh và cấp học.</summary>
public sealed record StudentSessionDto(string TokenType, string AccessToken, DateTime ExpiresAt, StudentProfileDto Student);

/// <summary>Yêu cầu quên mật khẩu học sinh: nhập mã học sinh để hệ thống kiểm tra và báo cho giáo viên chủ nhiệm.</summary>
public sealed record StudentForgotPasswordRequest(string? StudentCode);

public sealed record StudentForgotPasswordResultDto(string StudentName, string TeacherName);

public sealed class StudentForgotPasswordRequestValidator : AbstractValidator<StudentForgotPasswordRequest>
{
    public StudentForgotPasswordRequestValidator()
    {
        RuleFor(x => x.StudentCode).RequiredField("mã học sinh");
    }
}

public sealed class StudentLoginRequestValidator : AbstractValidator<StudentLoginRequest>
{
    public StudentLoginRequestValidator()
    {
        RuleFor(x => x.StudentCode).RequiredField("mã học sinh");
        RuleFor(x => x.Password).RequiredField("mật khẩu");
    }
}

public sealed class StudentPasswordRequestValidator : AbstractValidator<StudentPasswordRequest>
{
    public StudentPasswordRequestValidator()
    {
        // Quy tắc độ mạnh của mật khẩu phụ thuộc cấp của lớp nên kiểm trong StudentAuthService.ChangePasswordAsync, sau khi biết em học cấp nào.
        RuleFor(x => x.NewPassword).RequiredField("mật khẩu mới");
        RuleFor(x => x.ConfirmNewPassword).Cascade(CascadeMode.Stop)
            .RequiredField("mật khẩu mới một lần nữa")
            .Must((request, confirm) => confirm == request.NewPassword).WithAppMessage(Messages.PasswordMismatch);
    }
}

/// <summary>
/// Điều kiện học sinh được dùng hệ thống, dùng ở đăng nhập và ở lần kiểm mỗi yêu cầu để vô hiệu, xóa học sinh, xóa lớp, ngừng trường
/// có hiệu lực ngay: tài khoản hoạt động, chưa xóa mềm, học sinh chưa vô hiệu, chưa xóa, lớp chưa xóa, trường chưa ngừng.
/// Giáo viên bị khóa hay xóa không chặn học sinh của họ.
/// </summary>
public static class StudentAccess
{
    /// <summary>Lý do chặn (phần <c>{ly_do}</c> của thông điệp), hoặc null nếu học sinh dùng được. Tài khoản đã xóa mềm không đi qua đây.</summary>
    public static string? BlockedReason(UserStatus? status, bool organizationDeactivated, bool? studentActive, bool studentDeleted,
        bool classDeleted) =>
        status != UserStatus.ACTIVE || organizationDeactivated || studentActive != true || studentDeleted || classDeleted
            ? Messages.StudentBlockedReasons.AskTeacher
            : null;
}
