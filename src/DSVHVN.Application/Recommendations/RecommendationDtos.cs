using DSVHVN.Domain.Enums;

namespace DSVHVN.Application.Recommendations;

// Hợp đồng dữ liệu của gợi ý: "Gợi ý cho em" của học sinh (năm phần) và gợi ý ôn tập cho lớp của giáo viên.
// Mọi thứ tính khi hiển thị từ lượt làm bài; không có bảng gợi ý riêng.

/// <summary>Thẻ di sản được gợi ý (chỉ di sản đã xuất bản). <see cref="Slug"/> để mở trang di sản công khai.</summary>
public sealed record RecommendedHeritageDto(
    long HeritageId,
    string? Slug,
    string? Name,
    string? ThumbnailUrl,
    string? ShortDescription,
    string? ProvinceName,
    string? TypeName,
    string Reason);

/// <summary>Quiz đang mở em chưa làm. <see cref="NeedsLessonCode"/> true khi em chưa có lượt nào ở bài học của quiz (em mở bài bằng mã thầy cô đưa).</summary>
public sealed record RecommendedQuizDto(
    long QuizId,
    string? Name,
    long LessonId,
    string? LessonTitle,
    DateTime? CloseAt,
    DifficultyLevel? DifficultyLevel,
    string? DifficultyName,
    bool NeedsLessonCode,
    string? Label,
    string Reason);

/// <summary>
/// Bài học được gợi ý. Bài em đã làm (nên ôn lại) có mã bài học để mở lại; bài mới chỉ có tiêu đề và câu "Nhờ thầy cô đưa mã bài học",
/// <see cref="AccessCode"/> luôn null và không mở trực tiếp.
/// </summary>
public sealed record RecommendedLessonDto(
    long LessonId,
    string? Title,
    string? AccessCode,
    string? Hint,
    string Reason);

/// <summary>
/// Gợi ý cho em, đủ năm phần, cùng nội dung và cùng cách tính ở mọi cấp (cấp 1 chỉ bỏ số liệu khỏi câu lý do).
/// <see cref="ReviewPraise"/> khác null khi em làm đúng hết nên phần nên ôn thay bằng lời khen; <see cref="Message"/> là lời mời nhập mã bài học
/// khi em chưa có lượt nào hoặc lớp chưa có bài nào xuất bản. <see cref="RelatedFromFallback"/> true khi Dịch vụ AI lỗi và máy chủ tự tính phần liên quan.
/// </summary>
public sealed record StudentRecommendationsDto(
    int Level,
    string LevelName,
    string Layout,
    long? AttemptId,
    DifficultyLevel TargetDifficulty,
    string TargetDifficultyName,
    IReadOnlyList<RecommendedQuizDto> NextQuizzes,
    IReadOnlyList<RecommendedLessonDto> LessonsToReview,
    IReadOnlyList<RecommendedHeritageDto> HeritagesToReview,
    IReadOnlyList<RecommendedHeritageDto> RelatedHeritages,
    IReadOnlyList<RecommendedLessonDto> NewLessons,
    string? ReviewPraise,
    string? Message,
    bool RelatedFromFallback);

/// <summary>Di sản nên ôn của lớp: tỉ lệ sai, số câu trả lời, số câu sai hoặc bỏ trống và lý do "Lớp sai {x}% ở {n} câu về di sản này".</summary>
public sealed record ClassReviewHeritageDto(
    long HeritageId,
    string? Slug,
    string? Name,
    string? ThumbnailUrl,
    string? ShortDescription,
    string? ProvinceName,
    string? TypeName,
    int Answered,
    int Wrong,
    decimal WrongPercent,
    string Reason);

/// <summary>
/// Gợi ý ôn tập cho lớp: di sản nên ôn (tỉ lệ sai từ 40%, ít nhất 10 câu trả lời, tối đa 5) và di sản liên quan (tối đa 5). Từ đây giáo viên tạo bài
/// học ôn tập (bài nháp mới của lớp) qua API tạo bài học với các <see cref="SuggestedHeritageIds"/>. <see cref="Message"/> là lời báo khi chưa có lượt nộp.
/// </summary>
public sealed record ClassRecommendationsDto(
    long ClassId,
    string? ClassName,
    int Level,
    long? QuizId,
    DateTime? From,
    DateTime? To,
    int SubmittedAttempts,
    int AnsweredQuestions,
    IReadOnlyList<ClassReviewHeritageDto> HeritagesToReview,
    IReadOnlyList<RecommendedHeritageDto> RelatedHeritages,
    IReadOnlyList<long> SuggestedHeritageIds,
    string? Message,
    bool RelatedFromFallback);
