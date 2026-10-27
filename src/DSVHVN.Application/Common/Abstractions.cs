using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Guests;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Organizations;
using DSVHVN.Domain.Questions;
using DSVHVN.Domain.Quizzes;
using DSVHVN.Domain.Speech;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DSVHVN.Application.Common;

/// <summary>
/// Cổng vào CSDL cho tầng Application: 27 bảng của CSDL v3 và các bảng của CSDL v4 đã thêm theo từng luồng (hiện có lesson_sections, năm bảng đa ngôn ngữ, thuyết minh tạo sẵn, mô hình 3D, bộ nhớ đệm giọng đọc tự host (speech_clips) và ba bảng của khách tham quan và quảng cáo).
/// Thay đổi lược đồ về sau đi qua migration mới.
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

    DbSet<HeritageTranslation> HeritageTranslations { get; }
    DbSet<TimelineTranslation> TimelineTranslations { get; }
    DbSet<EventTranslation> EventTranslations { get; }
    DbSet<MediaTranslation> MediaTranslations { get; }
    DbSet<HeritageSummary> HeritageSummaries { get; }
    DbSet<HeritageNarration> HeritageNarrations { get; }
    DbSet<HeritageModel> HeritageModels { get; }

    DbSet<SpeechClip> SpeechClips { get; }

    DbSet<GuestDevice> GuestDevices { get; }
    DbSet<GuestActivity> GuestActivities { get; }
    DbSet<AdUnlock> AdUnlocks { get; }

    DbSet<Lesson> Lessons { get; }
    DbSet<LessonHeritage> LessonHeritages { get; }
    DbSet<LessonSection> LessonSections { get; }
    DbSet<LessonCompletion> LessonCompletions { get; }
    DbSet<AiGeneration> AiGenerations { get; }

    DbSet<Question> Questions { get; }
    DbSet<Answer> Answers { get; }

    DbSet<Quiz> Quizzes { get; }
    DbSet<QuizQuestion> QuizQuestions { get; }
    DbSet<Attempt> Attempts { get; }
    DbSet<AttemptAnswer> AttemptAnswers { get; }
    DbSet<AttemptAnswerPart> AttemptAnswerParts { get; }

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

    /// <summary>
    /// Khóa dòng <c>classes</c> tới hết giao dịch hiện tại để các thao tác đếm-rồi-thêm của cùng một lớp
    /// (sĩ số tối đa 60 học sinh đang hoạt động) chạy tuần tự. Phải gọi bên trong giao dịch.
    /// </summary>
    Task LockClassAsync(long classId, CancellationToken cancellationToken);

    /// <summary>
    /// Khóa việc đánh số hóa đơn tới hết giao dịch hiện tại (khóa ứng dụng của SQL Server, có hiệu lực giữa mọi tiến trình dùng chung CSDL) để hai trường mua cùng lúc
    /// không xin trùng số hóa đơn của ngày. Phải gọi bên trong giao dịch, sau khi đã khóa dòng trường.
    /// </summary>
    Task LockInvoiceNumberingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Khóa dòng <c>attempts</c> tới hết giao dịch hiện tại để lưu câu trả lời, nộp bài và tự nộp khi hết giờ của cùng một lượt chạy tuần tự.
    /// Phải gọi bên trong giao dịch.
    /// </summary>
    Task LockAttemptAsync(long attemptId, CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Một dòng đọc từ tệp hoặc văn bản danh sách học sinh: số dòng trong nguồn (dòng 1 là dòng đầu) và nội dung ô họ tên.</summary>
public sealed record ListRow(int RowNumber, string? Value);

/// <summary>Tệp tải về cho giáo viên (tệp mẫu danh sách học sinh).</summary>
public sealed record DownloadFile(byte[] Content, string ContentType, string FileName);

/// <summary>
/// Đọc tệp danh sách học sinh (<c>.xlsx</c> hoặc <c>.csv</c> UTF-8) và dựng tệp mẫu. Chỉ lấy cột đầu tiên (họ tên) của
/// bảng tính đầu tiên, giữ nguyên mọi dòng kể cả dòng tiêu đề và dòng trống (người gọi tự bỏ tiêu đề, cắt dòng trống cuối). Tệp sai loại hoặc không đọc được thì ném <see cref="AppException"/> 400.
/// </summary>
public interface IStudentListFile
{
    IReadOnlyList<ListRow> Read(Stream content, string fileName);

    /// <summary>Tệp mẫu một cột "Họ và tên", không có dòng mẫu để giáo viên quên xóa cũng không tạo thêm học sinh ngoài ý muốn.</summary>
    DownloadFile BuildTemplate(StudentListFormat format);
}

public enum StudentListFormat
{
    Xlsx,
    Csv,
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

/// <summary>Thông tin đưa vào access token. <see cref="SecurityStamp"/> đổi khi mật khẩu đổi (xem <see cref="SecurityStamps"/>). <see cref="SchoolLevel"/> (1, 2, 3) chỉ có với học sinh: cấp học của lớp lúc phát token.</summary>
public sealed record AccessTokenSubject(
    long UserId,
    string Username,
    string FullName,
    RoleCode Role,
    long? OrganizationId,
    string SecurityStamp,
    int? SchoolLevel = null);

public sealed record IssuedAccessToken(string Token, DateTime ExpiresAt);

/// <summary>Phát JWT truy cập. Chỉ có access token (hạn 8 giờ), không có refresh token.</summary>
public interface ITokenIssuer
{
    IssuedAccessToken CreateAccessToken(AccessTokenSubject subject);
}

/// <summary>
/// Một thư đã soạn xong: địa chỉ và họ tên người nhận, tiêu đề, bản chữ thuần và bản HTML cùng nội dung.
/// <paramref name="ReplyTo"/> (không bắt buộc) là địa chỉ nhận thư trả lời.
/// </summary>
public sealed record EmailMessage(string To, string? ToName, string Subject, string TextBody, string HtmlBody, string? ReplyTo = null);

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

    /// <summary>Gửi email thông báo cho giáo viên chủ nhiệm khi học sinh xin đặt lại mật khẩu.</summary>
    Task SendStudentPasswordResetRequestToTeacherAsync(string teacherEmail, string teacherName, string studentCode,
        string studentName, string className, CancellationToken cancellationToken = default);
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
