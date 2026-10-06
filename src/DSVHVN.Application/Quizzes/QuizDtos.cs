using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Quizzes;

// ---- Dữ liệu vào ----

/// <summary>
/// Tạo bài kiểm tra nháp cho một bài học của giáo viên (bài Nháp hoặc Đã xuất bản, không phải bài Lưu trữ). Lớp làm bài là lớp của bài học, không chọn riêng.
/// Giờ không kèm múi giờ hiểu là giờ Việt Nam. Thời gian làm theo cấp của lớp: cấp 1 phải để trống, cấp 2 tùy chọn, cấp 3 bắt buộc khi phát hành.
/// </summary>
public sealed record CreateQuizRequest(
    long? LessonId,
    string? Name,
    string? Description = null,
    DifficultyLevel? DifficultyLevel = null,
    int? TimeLimitMinutes = null,
    DateTime? OpenAt = null,
    DateTime? CloseAt = null,
    bool ShuffleQuestions = false,
    bool ShowAnswersAfterSubmit = false) : IQuizSettingsRequest;

/// <summary>
/// Lưu thiết lập của bài kiểm tra, gửi đủ mọi trường (trường để trống là xóa giá trị). Bài Nháp sửa được tất cả. Bài đã phát hành chỉ sửa được tên,
/// mô tả và giờ đóng (không sớm hơn hiện tại); các trường còn lại gửi lên phải giữ nguyên giá trị đang có. Bài đã đóng không sửa được.
/// </summary>
public sealed record UpdateQuizRequest(
    string? Name,
    string? Description = null,
    DifficultyLevel? DifficultyLevel = null,
    int? TimeLimitMinutes = null,
    DateTime? OpenAt = null,
    DateTime? CloseAt = null,
    bool ShuffleQuestions = false,
    bool ShowAnswersAfterSubmit = false) : IQuizSettingsRequest;

/// <summary>Thêm câu từ ngân hàng vào cuối bài Nháp. <c>Point</c> để trống thì mỗi câu 1 điểm.</summary>
public sealed record AddQuizQuestionsRequest(IReadOnlyList<long>? QuestionIds, decimal? Point = null);

public sealed record QuizQuestionItemRequest(long? QuestionId, decimal? Point);

/// <summary>
/// Đặt lại điểm, thứ tự và bỏ câu của bài Nháp: danh sách đầy đủ theo thứ tự mới; câu không có trong danh sách bị bỏ.
/// Chỉ nhận câu đã có trong bài (thêm câu mới qua thêm câu từ ngân hàng).
/// </summary>
public sealed record ReplaceQuizQuestionsRequest(IReadOnlyList<QuizQuestionItemRequest>? Items);

/// <summary>Nhân bản bài kiểm tra trong cùng bài học; <c>Name</c> để trống thì lấy tên cũ kèm "(bản sao)".</summary>
public sealed record DuplicateQuizRequest(string? Name = null);

public sealed record QuizListQuery(long? LessonId, long? ClassId, QuizStatus? Status, string? Keyword, int? Page, int? PageSize);

/// <summary>Hộp chọn câu từ ngân hàng: mặc định chỉ câu của các di sản của bài học (<c>LessonHeritagesOnly</c> = false để xem mọi di sản).</summary>
public sealed record QuizCandidateQuery(
    QuestionType? QuestionType,
    DifficultyLevel? DifficultyLevel,
    long? HeritageId,
    bool? LessonHeritagesOnly,
    string? Keyword,
    int? Page,
    int? PageSize);

// ---- Dữ liệu ra ----

/// <summary>Dòng của màn hình Bài kiểm tra của tôi.</summary>
public sealed record QuizRowDto(
    long Id,
    string? Name,
    long LessonId,
    string? LessonTitle,
    long ClassId,
    string? ClassName,
    int Grade,
    int SchoolLevel,
    QuizStatus? Status,
    string StatusLabel,
    QuizOpenState OpenState,
    string OpenStateLabel,
    DateTime? OpenAt,
    DateTime? CloseAt,
    int? TimeLimitMinutes,
    DifficultyLevel? DifficultyLevel,
    int QuestionCount,
    decimal? TotalPoint,
    int SubmittedCount,
    int StudentCount,
    DateTime? PublishedAt,
    DateTime? CreatedAt,
    DateTime? UpdatedAt);

public sealed record QuizPageDto(IReadOnlyList<QuizRowDto> Items, int Page, int PageSize, int TotalItems, int TotalPages);

/// <summary>
/// Một câu trong bài kiểm tra. <c>IsUsable</c> false (kèm <c>UnusableReason</c>) khi câu đã bị ẩn, bị xóa hoặc dạng và cấp không còn dùng được ở cấp
/// của lớp (ví dụ lớp vừa đổi khối sang cấp khác): giao diện hiện cảnh báo đỏ và việc phát hành bị chặn.
/// </summary>
public sealed record QuizQuestionDto(
    long QuestionId,
    int SortOrder,
    decimal Point,
    QuestionType? QuestionType,
    string TypeName,
    string Content,
    string? ImageUrl,
    DifficultyLevel? DifficultyLevel,
    long HeritageId,
    string? HeritageName,
    int LevelMin,
    int LevelMax,
    bool IsUsable,
    string? UnusableReason);

/// <summary>Điều kiện phát hành: <c>Missing</c> chặn phát hành; <c>Warnings</c> chỉ nhắc.</summary>
public sealed record QuizReadinessDto(bool IsReady, IReadOnlyList<string> Missing, IReadOnlyList<string> Warnings);

/// <summary>Quyền thao tác của giáo viên trên bài kiểm tra ở trạng thái hiện tại, để bật tắt nút.</summary>
public sealed record QuizAccessDto(
    bool CanEditSettings,
    bool CanEditCloseTime,
    bool CanEditQuestions,
    bool CanPublish,
    bool CanClose,
    bool CanDelete,
    bool CanDuplicate);

public sealed record QuizDetailDto(
    long Id,
    string? Name,
    string? Description,
    long LessonId,
    string? LessonTitle,
    ContentStatus? LessonStatus,
    string? LessonAccessCode,
    long ClassId,
    string? ClassName,
    int Grade,
    int SchoolLevel,
    string SchoolLevelName,
    DifficultyLevel? DifficultyLevel,
    int? TimeLimitMinutes,
    TimeLimitRule TimeLimitRule,
    string TimeLimitRuleText,
    DateTime? OpenAt,
    DateTime? CloseAt,
    bool ShuffleQuestions,
    bool ShowAnswersAfterSubmit,
    decimal TotalPoint,
    QuizStatus? Status,
    string StatusLabel,
    QuizOpenState OpenState,
    string OpenStateLabel,
    DateTime? PublishedAt,
    IReadOnlyList<QuizQuestionDto> Questions,
    int QuestionCount,
    QuizReadinessDto Readiness,
    QuizAccessDto Access,
    int SubmittedCount,
    int StudentCount,
    string? PublishNotice,
    DateTime? CreatedAt,
    DateTime? UpdatedAt);

public sealed record PublishQuizResultDto(QuizDetailDto Quiz, string Message);

/// <summary><c>ExpiredAttempts</c> là số lượt làm bài đang dở được tự nộp (EXPIRED) khi đóng.</summary>
public sealed record CloseQuizResultDto(QuizDetailDto Quiz, int ExpiredAttempts, string Message);

public sealed record DuplicateQuizResultDto(QuizDetailDto Quiz, int SkippedQuestions, string Message);

/// <summary>Câu trong hộp chọn câu: <c>InQuiz</c> true thì giao diện hiện mờ và không cho chọn lại.</summary>
public sealed record QuizCandidateDto(QuestionRowDto Question, bool InQuiz);

public sealed record QuizCandidatePageDto(
    IReadOnlyList<QuizCandidateDto> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages,
    int Level,
    IReadOnlyList<QuestionType> AllowedTypes);

/// <summary>Kết quả của tác vụ nền tự đóng bài kiểm tra quá giờ đóng.</summary>
public sealed record AutoCloseResultDto(int ClosedQuizzes, int ExpiredAttempts);
