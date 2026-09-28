namespace DSVHVN.Domain.Enums;

// Tên thành viên viết HOA đúng như CSDL v3: tên thành viên chính là giá trị lưu trong CSDL (nvarchar(20) + CHECK,
// hoặc chuỗi không CHECK) và là giá trị trong JSON của API. Giao diện luôn hiện nhãn tiếng Việt tương ứng.
// Đổi tên hay thêm bớt thành viên của enum có CHECK là đổi dữ liệu: phải kèm migration mới.
// 15 kiểu enum của CSDL, cộng ba danh mục mã lưu dạng chuỗi không CHECK (vai trò, vai trò người thực hiện, mã thao tác).

/// <summary><c>users.status</c>: hoạt động, bị khóa, ngừng sử dụng.</summary>
public enum UserStatus
{
    ACTIVE,
    LOCKED,
    INACTIVE,
}

/// <summary><c>heritages.status</c>, <c>lessons.status</c>: đang soạn, đã xuất bản, lưu trữ.</summary>
public enum ContentStatus
{
    DRAFT,
    PUBLISHED,
    ARCHIVED,
}

/// <summary><c>heritages.heritage_type</c>: vật thể, phi vật thể, thiên nhiên, hỗn hợp, tư liệu.</summary>
public enum HeritageType
{
    TANGIBLE,
    INTANGIBLE,
    NATURAL,
    MIXED,
    DOCUMENTARY,
}

/// <summary><c>heritages.recognition_level</c>: UNESCO, quốc gia đặc biệt, quốc gia, cấp tỉnh, chưa xếp hạng.</summary>
public enum RecognitionLevel
{
    UNESCO,
    NATIONAL_SPECIAL,
    NATIONAL,
    PROVINCIAL,
    NONE,
}

/// <summary><c>ai_generations.task_type</c>: tóm tắt bài học, tạo câu hỏi.</summary>
public enum AiTaskType
{
    LESSON_SUMMARY,
    QUIZ_GENERATION,
}

/// <summary><c>ai_generations.status</c>: đang xử lý, thành công, lỗi.</summary>
public enum AiTaskStatus
{
    PROCESSING,
    SUCCEEDED,
    FAILED,
}

/// <summary><c>questions.question_type</c>: chỉ hai loại, một đáp án đúng và đúng/sai.</summary>
public enum QuestionType
{
    SINGLE_CHOICE,
    TRUE_FALSE,
}

/// <summary><c>questions.difficulty_level</c>, <c>quizzes.difficulty_level</c>.</summary>
public enum DifficultyLevel
{
    EASY,
    MEDIUM,
    HARD,
}

/// <summary><c>questions.source</c>: giáo viên tự tạo hoặc AI tạo.</summary>
public enum QuestionSource
{
    MANUAL,
    AI,
}

/// <summary><c>questions.review_status</c>: chờ duyệt, đã duyệt, bị loại.</summary>
public enum ReviewStatus
{
    PENDING,
    APPROVED,
    REJECTED,
}

/// <summary><c>quizzes.status</c>: đang soạn, đã phát hành (khóa sửa câu hỏi), đã đóng.</summary>
public enum QuizStatus
{
    DRAFT,
    PUBLISHED,
    CLOSED,
}

/// <summary><c>attempts.status</c>: đang làm, đã nộp, hết giờ hệ thống tự nộp.</summary>
public enum AttemptStatus
{
    IN_PROGRESS,
    SUBMITTED,
    EXPIRED,
}

/// <summary><c>subscriptions.status</c>, có PENDING cho đăng ký chờ thanh toán.</summary>
public enum SubscriptionStatus
{
    PENDING,
    ACTIVE,
    EXPIRED,
    CANCELLED,
}

/// <summary><c>invoices.status</c>: chưa thanh toán, đã thanh toán, đã hủy.</summary>
public enum InvoiceStatus
{
    UNPAID,
    PAID,
    CANCELLED,
}

/// <summary><c>payments.status</c>: chờ, thành công, thất bại, đã hoàn tiền.</summary>
public enum PaymentStatus
{
    PENDING,
    SUCCEEDED,
    FAILED,
    REFUNDED,
}

/// <summary><c>roles.code</c>: mã lưu dạng chuỗi, không có CHECK. Mỗi tài khoản đúng một trong bốn vai trò.</summary>
public enum RoleCode
{
    ADMIN,
    ORG_ADMIN,
    TEACHER,
    STUDENT,
}

/// <summary><c>audit_logs.actor_role</c>: bản sao <c>roles.code</c> lúc ghi, thêm SYSTEM khi hệ thống tự làm.</summary>
public enum ActorRole
{
    ADMIN,
    ORG_ADMIN,
    TEACHER,
    STUDENT,
    SYSTEM,
}

/// <summary>
/// <c>audit_logs.action</c>: đúng 21 mã thao tác được ghi nhật ký. Cột không có CHECK, danh sách hợp lệ kiểm ở tầng code.
/// Nền tảng tài khoản và tổ chức ghi 8 mã đầu; các mã còn lại do chức năng tương ứng ghi qua cùng <c>IAuditLogger</c>.
/// </summary>
public enum AuditAction
{
    LOGIN,
    LOGIN_LOCKED_OUT,
    ORG_CREATED,
    ORG_DEACTIVATED,
    ACCOUNT_CREATED,
    ACCOUNT_LOCKED,
    ACCOUNT_UNLOCKED,
    ACCOUNT_DELETED,
    HERITAGE_PUBLISHED,
    HERITAGE_ARCHIVED,
    STUDENT_ACCOUNTS_CREATED,
    STUDENT_PASSWORD_RESET,
    STUDENT_DEACTIVATED,
    STUDENT_REACTIVATED,
    QUIZ_PUBLISHED,
    QUIZ_CLOSED,
    QUESTION_HIDDEN,
    PLAN_UPDATED,
    SUBSCRIPTION_CHANGED,
    BILLING_STATUS_CHANGED,
    IPN_REJECTED,
}
