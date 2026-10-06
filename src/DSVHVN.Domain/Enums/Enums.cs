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

/// <summary><c>heritage_models.file_format</c>: chỉ nhận <c>.glb</c> (glTF 2.0 nhị phân một tệp), không nhận <c>.gltf</c> nhiều tệp.</summary>
public enum ModelFormat
{
    GLB,
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

/// <summary>
/// <c>ai_generations.task_type</c>: soạn nội dung bài học theo cấp, tạo câu hỏi (tác vụ của giáo viên, gắn với bài học);
/// dịch di sản, tóm tắt di sản (tác vụ của quản trị hệ thống, gắn với di sản; CSDL v4).
/// </summary>
public enum AiTaskType
{
    LESSON_SUMMARY,
    QUIZ_GENERATION,
    HERITAGE_TRANSLATION,
    HERITAGE_SUMMARY,
}

/// <summary>
/// <c>lesson_sections.section_type</c> (CSDL v4): loại phần nội dung của bài học theo khung của cấp. Loại dùng được theo cấp của lớp
/// kiểm ở tầng code (<c>LessonFrames</c>): cấp 1 STORY, IMAGE; cấp 2 READING, IMAGE, MILESTONE;
/// cấp 3 READING, SOURCE_EXCERPT, MILESTONE, REFLECTION.
/// </summary>
public enum LessonSectionType
{
    STORY,
    IMAGE,
    READING,
    MILESTONE,
    SOURCE_EXCERPT,
    REFLECTION,
}

/// <summary><c>ai_generations.status</c>: đang xử lý, thành công, lỗi.</summary>
public enum AiTaskStatus
{
    PROCESSING,
    SUCCEEDED,
    FAILED,
}

/// <summary>
/// <c>questions.question_type</c> (CSDL v4): 8 dạng. Cấp dùng được của từng dạng nằm ở <c>QuestionTypes</c> (bảng cố định trong code).
/// Hai giá trị đầu có từ CSDL v3.
/// </summary>
public enum QuestionType
{
    SINGLE_CHOICE,
    TRUE_FALSE,
    IMAGE_CHOICE,
    MATCHING,
    ORDERING,
    FILL_BLANK,
    MULTI_TRUE_FALSE,
    SOURCE_READING,
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
/// <c>language_code</c> (CSDL v4): năm ngôn ngữ của phần công khai. Tên thành viên chính là mã lưu trong CSDL (chữ thường, khác các enum còn lại):
/// <c>vi</c> là bản gốc của mọi bản dịch, <c>en</c> do AI dịch rồi quản trị hệ thống duyệt, <c>ko</c>, <c>zh</c> (tiếng Trung giản thể, giao diện gọi là zh-Hans),
/// <c>ja</c> do AI dịch tự động và luôn mang nhãn "bản dịch máy".
/// </summary>
public enum LanguageCode
{
    vi,
    en,
    ko,
    zh,
    ja,
}

/// <summary>
/// <c>translation_status</c> (CSDL v4): trạng thái bản dịch và bản tóm tắt di sản. Tiếng Anh (bản dịch) và tiếng Việt, tiếng Anh (tóm tắt) đi qua
/// PENDING, APPROVED, REJECTED; tiếng Hàn, Trung, Nhật luôn là MACHINE (CHECK trong CSDL).
/// </summary>
public enum TranslationStatus
{
    MACHINE,
    PENDING,
    APPROVED,
    REJECTED,
}

/// <summary>
/// <c>audit_logs.action</c>: 24 mã thao tác được ghi nhật ký (21 mã của CSDL v3, <c>HERITAGE_PREVIEW_ISSUED</c> khi cấp liên kết xem trước bản nháp di sản, và
/// <c>TRANSLATION_APPROVED</c>, <c>TRANSLATION_REJECTED</c> khi duyệt, từ chối bản dịch và tóm tắt di sản). Cột không có CHECK, danh sách hợp lệ kiểm ở tầng code.
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
    HERITAGE_PREVIEW_ISSUED,
    TRANSLATION_APPROVED,
    TRANSLATION_REJECTED,
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
    HERITAGE_MODEL_CHANGED,
}
