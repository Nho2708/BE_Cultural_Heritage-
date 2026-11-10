using DSVHVN.Application.Curriculum;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Application.Lessons;

// Thư viện bài mẫu dùng chung: bài hay của một giáo viên, sau khi quản trị hệ thống duyệt, vào thư viện cho giáo viên mọi trường xem trước và dùng cho lớp của mình.

/// <summary>Cách xếp thư viện: bài mới duyệt trước (mặc định) hoặc bài được dùng nhiều trước.</summary>
public enum LessonLibrarySort
{
    NEWEST,
    POPULAR,
}

/// <summary>Bộ lọc của thư viện (giáo viên): cấp 1-3, khối 1-12, di sản, từ khóa trong tiêu đề, cách xếp, trang, chủ đề chương trình.</summary>
public sealed record LessonLibraryQuery(
    int? Level,
    int? Grade,
    long? HeritageId,
    string? Keyword,
    LessonLibrarySort? Sort,
    int? Page,
    int? PageSize,
    int? TopicId = null);

/// <summary>Di sản hiện trên thẻ bài của thư viện.</summary>
public sealed record LibraryHeritageDto(long Id, string? Slug, string? Name, string? ThumbnailUrl);

/// <summary>
/// Thẻ bài của thư viện (giáo viên). Người soạn chỉ hiện họ tên và tên trường, không email hay số điện thoại. <c>UseCount</c> là số bài đã được tạo từ bài mẫu này;
/// <c>IsMine</c> là bài của chính người xem.
/// </summary>
public sealed record LessonLibraryItemDto(
    long Id,
    string? Title,
    int SchoolLevel,
    int Grade,
    IReadOnlyList<LibraryHeritageDto> Heritages,
    string SummaryExcerpt,
    int SectionCount,
    int QuizCount,
    string? AuthorName,
    string? SchoolName,
    int UseCount,
    DateTime? ApprovedAt,
    bool IsMine,
    IReadOnlyList<CurriculumTopicBriefDto>? CurriculumTopics = null);

/// <summary>Bài kiểm tra của bài mẫu (sẽ được chép nếu giáo viên chọn chép kèm): tên và số câu.</summary>
public sealed record LibraryQuizDto(string? Title, int QuestionCount);

/// <summary>
/// Xem trước bài mẫu: thẻ bài, bài như học sinh thấy (không mã bài học, không tên lớp, không danh sách bài kiểm tra của lớp gốc) và danh sách bài kiểm tra sẽ chép.
/// </summary>
public sealed record LessonLibraryDetailDto(LessonLibraryItemDto Item, StudentLessonDto Lesson, IReadOnlyList<LibraryQuizDto> Quizzes);

// ---- Quản trị hệ thống duyệt ----

/// <summary>Bộ lọc màn hình duyệt: trạng thái (mặc định chờ duyệt), cấp, từ khóa trong tiêu đề, trang.</summary>
public sealed record AdminLessonLibraryQuery(LessonSampleStatus? Status, int? Level, string? Keyword, int? Page, int? PageSize);

/// <summary>Dòng của màn hình duyệt bài mẫu của quản trị hệ thống.</summary>
public sealed record AdminLessonLibraryItemDto(
    long Id,
    string? Title,
    int SchoolLevel,
    int Grade,
    IReadOnlyList<LibraryHeritageDto> Heritages,
    string? AuthorName,
    string? SchoolName,
    LessonSampleStatus Status,
    DateTime? RequestedAt,
    DateTime? ReviewedAt,
    string? Note,
    int UseCount);

/// <summary>Xem bài mẫu để duyệt (chỉ đọc): dòng duyệt, bài như học sinh thấy, bài kiểm tra kèm theo.</summary>
public sealed record AdminLessonLibraryDetailDto(AdminLessonLibraryItemDto Item, StudentLessonDto Lesson, IReadOnlyList<LibraryQuizDto> Quizzes);

/// <summary>Kết quả duyệt hoặc từ chối: dòng sau khi đổi và câu thông báo.</summary>
public sealed record AdminLessonLibraryResultDto(AdminLessonLibraryItemDto Item, string Message);

/// <summary>Từ chối hoặc gỡ bài mẫu: lý do bắt buộc, 1-500 ký tự.</summary>
public sealed record RejectLessonSampleRequest(string? Note);
