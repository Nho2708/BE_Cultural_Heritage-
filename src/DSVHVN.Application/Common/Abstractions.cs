using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DSVHVN.Application.Common;

/// <summary>Cổng vào CSDL cho tầng Application. Mỗi luồng thêm DbSet của mình cùng migration mới của luồng.</summary>
public interface IAppDbContext
{
    DbSet<Organization> Organizations { get; }
    DbSet<Role> Roles { get; }
    DbSet<User> Users { get; }
    DbSet<PasswordResetToken> PasswordResetTokens { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<Plan> Plans { get; }
    DbSet<Subscription> Subscriptions { get; }

    /// <summary>Mở giao dịch cho thao tác nhiều bước (tạo trường, tạo giáo viên trong hạn mức).</summary>
    DatabaseFacade Database { get; }

    /// <summary>
    /// Khóa dòng <c>organizations</c> tới hết giao dịch hiện tại để các thao tác đếm-rồi-thêm của cùng một trường
    /// (vd <c>max_teachers</c>) chạy tuần tự. Phải gọi bên trong giao dịch.
    /// </summary>
    Task LockOrganizationAsync(long organizationId, CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public enum PasswordCheck
{
    Failed,
    Success,
    SuccessRehashNeeded,
}

/// <summary>Băm mật khẩu có muối PBKDF2.</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    PasswordCheck Verify(string passwordHash, string password);

    /// <summary>So với một chuỗi băm giả để thời gian trả lời không lộ tài khoản có tồn tại hay không.</summary>
    void VerifyAgainstDummy(string password);
}

/// <summary>Thông tin đưa vào access token. <see cref="SecurityStamp"/> đổi khi mật khẩu đổi (xem <see cref="SecurityStamps"/>).</summary>
public sealed record AccessTokenSubject(
    long UserId,
    string Username,
    string FullName,
    RoleCode Role,
    long? OrganizationId,
    string SecurityStamp);

public sealed record IssuedAccessToken(string Token, DateTime ExpiresAt);

/// <summary>Phát JWT truy cập. Chỉ có access token (hạn 8 giờ), không có refresh token.</summary>
public interface ITokenIssuer
{
    IssuedAccessToken CreateAccessToken(AccessTokenSubject subject);
}

/// <summary>Gửi thư. Hiện chỉ có bản giả ghi ra log; gửi qua SMTP thật là việc của luồng sau.</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default);
}

/// <summary>Soạn hai loại thư đặt mật khẩu (hệ thống chỉ có hai loại thư).</summary>
public interface IAccountMailer
{
    /// <summary>Tài khoản mới do ADMIN hoặc ORG_ADMIN tạo: liên kết đặt mật khẩu lần đầu, hạn 48 giờ.</summary>
    Task SendFirstPasswordLinkAsync(string email, string fullName, string username, string? organizationName,
        string rawToken, DateTime expiresAt, CancellationToken cancellationToken = default);

    /// <summary>Quên mật khẩu: liên kết đặt lại, hạn 30 phút.</summary>
    Task SendResetPasswordLinkAsync(string email, string fullName, string rawToken, DateTime expiresAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Chống dò mật khẩu và giới hạn yêu cầu quên mật khẩu, đếm trong bộ nhớ máy chủ (không có bảng).
/// Khóa đăng nhập đếm theo <b>khóa</b> do AuthService chọn: theo tài khoản nếu tìm thấy, không thì theo chuỗi đã nhập.
/// </summary>
public interface IAuthThrottle
{
    /// <summary>Thời gian còn bị tạm khóa, hoặc null nếu không bị khóa.</summary>
    TimeSpan? GetLoginLockout(string key);

    /// <summary>Ghi một lần sai. Trả true nếu lần sai này làm khóa đăng nhập bắt đầu.</summary>
    bool RegisterFailedLogin(string key);

    void ResetFailedLogins(string key);

    /// <summary>Trả false nếu email đã dùng hết 3 yêu cầu trong một giờ.</summary>
    bool TryConsumeForgotPasswordRequest(string email);
}
