using DSVHVN.Application.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Lessons;

// ---- Dữ liệu vào ----

/// <summary>Tạo bài học nháp (hộp thoại Tạo bài học): một lớp của giáo viên, tiêu đề, 1-10 di sản đã xuất bản.</summary>
public sealed record CreateLessonRequest(long? ClassId, string? Title, IReadOnlyList<long>? HeritageIds);

/// <summary>
/// Lưu bài học (màn hình Soạn bài học). Trường để null thì giữ nguyên; có giá trị thì thay toàn bộ. <c>Sections</c> là toàn bộ các phần
/// theo thứ tự hiển thị. <c>HeritageIds</c> chỉ đổi được khi bài đang là Nháp.
/// </summary>
public sealed record UpdateLessonRequest(
    string? Title,
    string? Summary,
    IReadOnlyList<string>? KeyPoints,
    IReadOnlyList<LessonSectionRequest>? Sections,
    IReadOnlyList<long>? HeritageIds);

/// <summary>
/// Một phần nội dung. <c>Type</c> phải thuộc khung của cấp lớp. <c>ImageUrl</c> chọn trong thư viện ảnh của các di sản của bài.
/// <c>EventId</c> (mốc nối với sự kiện của di sản) phải là sự kiện của một di sản trong bài.
/// </summary>
public sealed record LessonSectionRequest(
    LessonSectionType? Type,
    string? Title,
    string? Body,
    string? ImageUrl,
    int? MilestoneYear,
    long? EventId,
    string? SourceCitation);

/// <summary>
/// Nhân bản bài học sang một lớp khác. <c>IncludeQuizzes</c> true thì chép kèm các bài kiểm tra chưa xóa của bài thành bài kiểm tra nháp của bài mới
/// (cùng câu, điểm, thứ tự và thiết lập; giờ mở, giờ đóng để trống).
/// </summary>
public sealed record DuplicateLessonRequest(long? TargetClassId, bool IncludeQuizzes = false);

/// <summary>Soạn nội dung bằng AI: độ dài Ngắn / Vừa / Chi tiết, nằm trong khoảng số từ của cấp; để trống thì lấy Vừa.</summary>
public sealed record ComposeLessonRequest(LessonLength? Length);

public sealed record LessonListQuery(
    long? ClassId,
    ContentStatus? Status,
    HeritageType? HeritageType,
    string? Keyword,
    int? Page,
    int? PageSize,
    DifficultyLevel? Difficulty = null);

// ---- Dữ liệu ra cho giáo viên ----

public sealed record LessonHeritageDto(long Id, string? Slug, string? Name, string? ThumbnailUrl, ContentStatus? Status);

/// <summary>Dòng của màn hình Bài học của tôi.</summary>
public sealed record LessonRowDto(
    long Id,
    string? Title,
    long ClassId,
    string? ClassName,
    int Grade,
    int SchoolLevel,
    ContentStatus? Status,
    string? AccessCode,
    IReadOnlyList<LessonHeritageDto> Heritages,
    int QuizCount,
    DateTime? CreatedAt,
    DateTime? UpdatedAt,
    DifficultyLevel? Difficulty = null,
    string DifficultyLabel = LessonDifficulty.NoQuizLabel);

public sealed record LessonPageDto(IReadOnlyList<LessonRowDto> Items, int Page, int PageSize, int TotalItems, int TotalPages);

/// <summary>Một phần nội dung. <c>Label</c> là chữ hiển thị sẵn của mốc ("1010", "179 TCN"); <c>ImageCredit</c> là ghi công của hình.</summary>
public sealed record LessonSectionDto(
    long? Id,
    LessonSectionType Type,
    string TypeLabel,
    string? Title,
    string? Body,
    string? ImageUrl,
    LessonImageCreditDto? Image,
    int? MilestoneYear,
    string? MilestoneLabel,
    long? EventId,
    string? SourceCitation,
    int SortOrder);

/// <summary>Chú thích và ghi công (tác giả, giấy phép, nguồn) của một hình trong thư viện ảnh của di sản.</summary>
public sealed record LessonImageCreditDto(string? Caption, string? Credit, string? Author, string? License, string? Source);

/// <summary>Khung nội dung của cấp để màn hình soạn đổi theo cấp: khoảng số từ, ba mức độ dài AI, loại phần dùng được.</summary>
public sealed record LessonFrameDto(
    int Level,
    string Name,
    int MinWords,
    int MaxWords,
    int ShortWords,
    int MediumWords,
    int DetailedWords,
    IReadOnlyList<LessonSectionType> AllowedTypes,
    string Layout,
    bool SequentialFlow,
    IReadOnlyList<FlowStep> Flow)
{
    public static LessonFrameDto From(LevelFrame f) => new(f.Level, f.Name, f.MinWords, f.MaxWords, f.ShortWords, f.MediumWords,
        f.DetailedWords, f.AllowedTypes, f.Layout, f.SequentialFlow, f.Flow);
}

/// <summary>Điều kiện xuất bản: <c>Missing</c> chặn xuất bản; <c>Warnings</c> chỉ nhắc.</summary>
public sealed record LessonReadinessDto(bool IsReady, IReadOnlyList<string> Missing, IReadOnlyList<string> Warnings);

public sealed record LessonDetailDto(
    long Id,
    string? Title,
    long ClassId,
    string? ClassName,
    int Grade,
    int SchoolLevel,
    string SchoolLevelName,
    ContentStatus? Status,
    string? AccessCode,
    string? AccessPath,
    string? Summary,
    IReadOnlyList<string> KeyPoints,
    IReadOnlyList<LessonSectionDto> Sections,
    IReadOnlyList<LessonHeritageDto> Heritages,
    LessonFrameDto Frame,
    int WordCount,
    LessonReadinessDto Readiness,
    int QuizCount,
    AiQuotaDto AiQuota,
    DateTime? CreatedAt,
    DateTime? UpdatedAt);

/// <summary>Kết quả xuất bản: bài sau khi xuất bản, mã vừa sinh lần đầu (hoặc mã cũ khi xuất bản lại) và câu thông báo.</summary>
public sealed record PublishLessonResultDto(LessonDetailDto Lesson, bool CodeCreated, string AccessCode, string Message);

/// <summary>
/// Kết quả nhân bản: bài nháp mới, số di sản đã ẩn bị bỏ, lời nhắc khi lớp đích khác cấp, số bài kiểm tra đã chép kèm và số câu hỏi bị bỏ
/// (đã ẩn, đã xóa hoặc không dùng được ở cấp của lớp đích).
/// </summary>
public sealed record DuplicateLessonResultDto(
    LessonDetailDto Lesson,
    int SkippedHeritages,
    string? LevelNotice,
    string Message,
    int CopiedQuizzes = 0,
    int SkippedQuestions = 0);

/// <summary>
/// Bản nháp AI soạn, chưa lưu vào bài: giáo viên xem, sửa rồi lưu. <c>GenerationId</c> truy về dòng nhật ký AI,
/// <c>WordCount</c> và <c>Warnings</c> cho biết nội dung có nằm trong khoảng của cấp không.
/// </summary>
public sealed record LessonDraftDto(
    long GenerationId,
    int Level,
    LessonLength Length,
    string LengthLabel,
    string Summary,
    IReadOnlyList<string> KeyPoints,
    IReadOnlyList<LessonSectionDto> Sections,
    int WordCount,
    IReadOnlyList<string> Warnings,
    string Model,
    AiQuotaDto AiQuota,
    string Message);

/// <summary>Một ảnh trong thư viện của các di sản của bài, để chọn cho bước câu chuyện, hình, chú thích.</summary>
public sealed record LessonImageOptionDto(
    long MediaId,
    string? Url,
    string? Caption,
    string? Credit,
    string? Author,
    string? License,
    string? Source,
    long HeritageId,
    string? HeritageName,
    long? EventId);

/// <summary>Một sự kiện trên dòng thời gian của các di sản của bài, để chọn làm mốc thời gian.</summary>
public sealed record LessonEventOptionDto(
    long EventId,
    long HeritageId,
    string? HeritageName,
    string? PhaseName,
    string? Name,
    int StartYear,
    string DateLabel);

public sealed record LessonMaterialsDto(IReadOnlyList<LessonImageOptionDto> Images, IReadOnlyList<LessonEventOptionDto> Events);

// ---- Dữ liệu ra cho học sinh ----

/// <summary>Thẻ di sản trong bài học của học sinh (chỉ di sản đang xuất bản): dẫn sang chi tiết, bản đồ, dòng thời gian.</summary>
public sealed record StudentHeritageCardDto(
    long Id,
    string Slug,
    string Name,
    string? ShortDescription,
    string? ThumbnailUrl,
    string? ProvinceName,
    decimal? Latitude,
    decimal? Longitude,
    bool HasTimeline);

/// <summary>Một phần nội dung cho học sinh, đã lọc theo khung của cấp em.</summary>
public sealed record StudentSectionDto(
    LessonSectionType Type,
    string? Title,
    string? Body,
    string? ImageUrl,
    LessonImageCreditDto? Image,
    int? MilestoneYear,
    string? MilestoneLabel,
    long? EventId,
    string? SourceCitation,
    int SortOrder);

/// <summary>Trạng thái bài kiểm tra của bài với riêng em.</summary>
public enum StudentQuizState
{
    NOT_OPEN,
    OPEN,
    IN_PROGRESS,
    SUBMITTED,
    CLOSED,
}

/// <summary>Một bài kiểm tra đã phát hành (hoặc đã đóng) của bài kèm trạng thái với em: chưa mở, đang mở, em đang làm, em đã làm, đã đóng.</summary>
public sealed record StudentQuizDto(
    long Id,
    string? Name,
    string? Description,
    DateTime? OpenAt,
    DateTime? CloseAt,
    int? TimeLimitMinutes,
    int QuestionCount,
    StudentQuizState State,
    string StateLabel);

public sealed record StudentLayoutDto(string Layout, bool SequentialFlow, IReadOnlyList<FlowStep> Steps);

/// <summary>
/// Bài học theo luồng của cấp lớp em: tiêu đề, lớp, giáo viên (không thông tin liên hệ), nội dung, ý chính, các phần theo khung của cấp,
/// thẻ di sản đang xuất bản và danh sách bài kiểm tra kèm trạng thái với em. <see cref="FocusQuizId"/> có giá trị khi em gõ mã bài kiểm tra: giao diện vào thẳng bài đó.
/// </summary>
public sealed record StudentLessonDto(
    long Id,
    string AccessCode,
    string? Title,
    string? ClassName,
    string? TeacherName,
    int SchoolLevel,
    string SchoolLevelName,
    StudentLayoutDto Layout,
    string? Summary,
    IReadOnlyList<string> KeyPoints,
    IReadOnlyList<StudentSectionDto> Sections,
    IReadOnlyList<StudentHeritageCardDto> Heritages,
    IReadOnlyList<StudentQuizDto> Quizzes,
    long? FocusQuizId = null);

/// <summary>
/// Xem thử của giáo viên ("Xem như học sinh"): đúng bài học như học sinh lớp đó thấy, kèm trạng thái hiện tại của bài (Nháp, Đã xuất bản, Lưu trữ)
/// cho dải "Đang xem như học sinh, không lưu kết quả". <see cref="QuizzesViewOnly"/> luôn true: bài kiểm tra chỉ xem câu hỏi, không nộp được.
/// </summary>
public sealed record LessonPreviewDto(StudentLessonDto Lesson, ContentStatus? Status, bool QuizzesViewOnly);
