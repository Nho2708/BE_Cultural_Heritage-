using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Organizations;
using DSVHVN.Domain.Questions;
using DSVHVN.Domain.Quizzes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DSVHVN.Application.Common;

/// <summary>
/// Cổng vào CSDL cho tầng Application: đủ 27 bảng của CSDL v3. Thay đổi lược đồ về sau đi qua migration mới.
/// </summary>
public interface IAppDbContext
{
    DbSet<Organization> Organizations { get; }
    DbSet<Role> Roles { get; }
    DbSet<User> Users { get; }
    DbSet<PasswordResetToken> PasswordResetTokens { get; }
    DbSet<AuditLog> AuditLogs { get; }

    DbSet<SchoolClass> Classes { get; }
    DbSet<Student> Students { get; }

    DbSet<Province> Provinces { get; }
    DbSet<Heritage> Heritages { get; }
    DbSet<Timeline> Timelines { get; }
    DbSet<TimelineEvent> Events { get; }
    DbSet<Media> Media { get; }
    DbSet<HeritageReference> HeritageReferences { get; }

    DbSet<Lesson> Lessons { get; }
    DbSet<LessonHeritage> LessonHeritages { get; }
    DbSet<AiGeneration> AiGenerations { get; }

    DbSet<Question> Questions { get; }
    DbSet<Answer> Answers { get; }

    DbSet<Quiz> Quizzes { get; }
    DbSet<QuizQuestion> QuizQuestions { get; }
    DbSet<Attempt> Attempts { get; }
    DbSet<AttemptAnswer> AttemptAnswers { get; }

    DbSet<Plan> Plans { get; }
    DbSet<Subscription> Subscriptions { get; }
    DbSet<Invoice> Invoices { get; }
    DbSet<PaymentProvider> PaymentProviders { get; }
    DbSet<Payment> Payments { get; }

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

/// <summary>
/// Một thư đã soạn xong: địa chỉ và họ tên người nhận, tiêu đề, bản chữ thuần và bản HTML cùng nội dung.
/// </summary>
public sealed record EmailMessage(string To, string? ToName, string Subject, string TextBody, string HtmlBody);

/// <summary>
/// Gửi thư. Hai bản chọn theo cấu hình <c>Email:Mode</c>: <c>Log</c> (mặc định, chỉ ghi thư ra log cho máy phát triển)
/// và <c>Smtp</c> (gửi thật). Gửi không được thì ném ngoại lệ; người gọi quyết định ghi log hay báo lỗi.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Soạn hai loại thư đặt mật khẩu (hệ thống chỉ có hai loại thư, chỉ gửi cho tài khoản có email).</summary>
public interface IAccountMailer
{
    /// <summary>Tài khoản mới do quản trị hệ thống hoặc quản trị trường tạo: liên kết đặt mật khẩu lần đầu, hạn 48 giờ.</summary>
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
