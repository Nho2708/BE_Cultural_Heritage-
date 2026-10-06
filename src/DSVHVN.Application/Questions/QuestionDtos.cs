using DSVHVN.Application.Billing;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Application.Questions;

// Hợp đồng dữ liệu của ngân hàng câu hỏi 8 dạng. Cùng một khuôn dùng cho gửi lên (tạo, sửa câu), trả về (chi tiết câu) và AI sinh câu.

/// <summary>Một phương án: một đáp án đúng, chọn hình đúng (kèm hình, nhãn ở <see cref="Content"/>), đọc tư liệu.</summary>
public sealed record QuestionOption(string? Content, string? ImageUrl, bool IsCorrect);

/// <summary>Một cặp đúng của dạng ghép cặp: vế trái là chữ hoặc hình, vế phải là tên.</summary>
public sealed record QuestionPair(string? Left, string? LeftImageUrl, string? Right);

/// <summary>Một chỗ trống của dạng điền từ và các cách viết được chấp nhận.</summary>
public sealed record QuestionBlank(List<string>? Accepted);

/// <summary>Một mệnh đề của dạng nhiều mệnh đề Đúng/Sai kèm đáp án.</summary>
public sealed record QuestionStatement(string? Text, bool IsTrue);

/// <summary>
/// Nội dung câu hỏi gửi lên khi tạo hoặc sửa. Phần đáp án đổi theo <see cref="QuestionType"/>; trường của dạng khác bị bỏ qua.
/// <list type="bullet">
/// <item>SINGLE_CHOICE: <see cref="Options"/> đúng 4 phương án, đúng một phương án <c>isCorrect</c>.</item>
/// <item>TRUE_FALSE: <see cref="AnswerIsTrue"/>; <see cref="ImageUrl"/> tùy chọn, bắt buộc khi câu dùng được ở cấp 1.</item>
/// <item>IMAGE_CHOICE: <see cref="Options"/> 3-4 thẻ, mỗi thẻ một hình khác nhau kèm nhãn 1-50 ký tự, đúng một thẻ đúng.</item>
/// <item>MATCHING: <see cref="Pairs"/> 3-5 cặp.</item>
/// <item>ORDERING: <see cref="Steps"/> 3-6 mục (tối đa 4 khi câu dùng được ở cấp 1) liệt kê theo thứ tự đúng.</item>
/// <item>FILL_BLANK: <see cref="Content"/> có 1-3 chỗ trống đánh dấu bằng ba dấu gạch dưới liền nhau; <see cref="Blanks"/> khớp thứ tự;
/// <see cref="Distractors"/> (từ gây nhiễu) bật kho từ 3-8 từ.</item>
/// <item>MULTI_TRUE_FALSE: <see cref="Content"/> là đoạn dẫn; <see cref="Statements"/> 3-5 mệnh đề, không cùng Đúng hoặc cùng Sai.</item>
/// <item>SOURCE_READING: <see cref="Passage"/> (tối đa 150 từ) và <see cref="PassageSource"/> bắt buộc; <see cref="Content"/> là câu hỏi con,
/// <see cref="Options"/> đúng 4 phương án.</item>
/// </list>
/// <see cref="LevelMin"/>, <see cref="LevelMax"/> để trống thì lấy đủ các cấp dạng câu cho phép.
/// </summary>
public sealed record SaveQuestionRequest(
    QuestionType? QuestionType,
    long? HeritageId,
    string? Content,
    string? Explanation,
    DifficultyLevel? DifficultyLevel,
    int? LevelMin = null,
    int? LevelMax = null,
    string? ImageUrl = null,
    string? Passage = null,
    string? PassageSource = null,
    bool? AnswerIsTrue = null,
    List<QuestionOption>? Options = null,
    List<QuestionPair>? Pairs = null,
    List<string>? Steps = null,
    List<QuestionBlank>? Blanks = null,
    List<string>? Distractors = null,
    List<QuestionStatement>? Statements = null);

/// <summary>Ảnh dùng trong câu kèm ghi công (mọi ảnh của câu là ảnh trong thư viện của di sản được gắn).</summary>
public sealed record QuestionImageDto(string Url, string? Caption, string? Credit);

/// <summary>Lý do câu chỉ đọc với người đang xem.</summary>
public enum QuestionReadOnlyReason
{
    /// <summary>Câu đã nằm trong ít nhất một bài kiểm tra.</summary>
    USED_IN_QUIZ,

    /// <summary>Câu do người khác tạo.</summary>
    NOT_OWNER,

    /// <summary>Quản trị hệ thống đã ẩn câu.</summary>
    HIDDEN_BY_ADMIN,
}

/// <summary>Quyền của người đang xem trên một câu, để giao diện bật tắt nút (Sửa, Xóa, Duyệt, Loại, Nhân bản; quản trị hệ thống: Ẩn, Hiện lại).</summary>
public sealed record QuestionAccessDto(
    bool IsReadOnly,
    QuestionReadOnlyReason? Reason,
    string? Message,
    bool CanEdit,
    bool CanDelete,
    bool CanApprove,
    bool CanReject,
    bool CanDuplicate,
    bool CanHide = false,
    bool CanRestore = false);

/// <summary>Một dòng của danh sách ngân hàng câu hỏi.</summary>
public sealed record QuestionRowDto(
    long Id,
    QuestionType QuestionType,
    string TypeName,
    string Content,
    string? ImageUrl,
    long HeritageId,
    string? HeritageName,
    DifficultyLevel? DifficultyLevel,
    int LevelMin,
    int LevelMax,
    QuestionSource? Source,
    ReviewStatus? ReviewStatus,
    long CreatedBy,
    string? CreatorName,
    string? OrganizationName,
    bool IsMine,
    bool IsHidden,
    int QuizCount,
    DateTime? CreatedAt,
    DateTime? UpdatedAt);

public sealed record QuestionPageDto(
    IReadOnlyList<QuestionRowDto> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages);

/// <summary>
/// Chi tiết một câu cho giáo viên (có đáp án). Cấu trúc đáp án cùng khuôn với <see cref="SaveQuestionRequest"/>; <see cref="WordBank"/> là kho từ
/// học sinh thấy (rỗng nếu câu điền từ không có kho từ).
/// </summary>
public sealed record QuestionDetailDto(
    long Id,
    QuestionType QuestionType,
    string TypeName,
    long HeritageId,
    string? HeritageName,
    string? Content,
    string? Explanation,
    DifficultyLevel? DifficultyLevel,
    int LevelMin,
    int LevelMax,
    IReadOnlyList<int> Levels,
    string? ImageUrl,
    string? Passage,
    string? PassageSource,
    bool? AnswerIsTrue,
    IReadOnlyList<QuestionOption> Options,
    IReadOnlyList<QuestionPair> Pairs,
    IReadOnlyList<string> Steps,
    IReadOnlyList<QuestionBlank> Blanks,
    IReadOnlyList<string> Distractors,
    IReadOnlyList<string> WordBank,
    IReadOnlyList<QuestionStatement> Statements,
    int PartsTotal,
    IReadOnlyList<QuestionImageDto> Images,
    QuestionSource? Source,
    ReviewStatus? ReviewStatus,
    long CreatedBy,
    string? CreatorName,
    string? OrganizationName,
    long? AiGenerationId,
    long? ReviewedBy,
    DateTime? ReviewedAt,
    int QuizCount,
    QuestionAccessDto Access,
    DateTime? CreatedAt,
    DateTime? UpdatedAt);

/// <summary>Hộp tìm kiếm ngân hàng. <see cref="Scope"/> mặc định là câu dùng chung.</summary>
public sealed record QuestionListQuery(
    QuestionScope? Scope = null,
    QuestionType? QuestionType = null,
    int? Level = null,
    DifficultyLevel? DifficultyLevel = null,
    long? HeritageId = null,
    ReviewStatus? Status = null,
    QuestionSource? Source = null,
    long? CreatedBy = null,
    string? Keyword = null,
    int? Page = null,
    int? PageSize = null);

/// <summary>Ba tab của ngân hàng: Dùng chung, Của tôi, Chờ duyệt.</summary>
public enum QuestionScope
{
    /// <summary>Câu đã duyệt của mọi giáo viên mọi trường, trừ câu bị ẩn.</summary>
    SHARED,

    /// <summary>Câu của mình, mọi trạng thái.</summary>
    MINE,

    /// <summary>Câu AI chờ duyệt của mình.</summary>
    PENDING,
}

/// <summary>Số đếm trên các tab và dạng câu dùng được ở từng cấp.</summary>
public sealed record QuestionCountsDto(int Shared, int Mine, int Pending, int Rejected);

public sealed record QuestionTypeDto(QuestionType Type, string Name, int MinLevel, int MaxLevel, bool MultiPart);

/// <summary>Bảng dạng câu theo cấp cho trình soạn: mỗi dạng, cấp dùng được, và các dạng của từng cấp.</summary>
public sealed record QuestionTypesDto(
    IReadOnlyList<QuestionTypeDto> Types,
    IReadOnlyDictionary<int, IReadOnlyList<QuestionType>> ByLevel);

/// <summary>Duyệt nhiều câu một lần. Câu không duyệt được nằm ở <see cref="Skipped"/> kèm lý do.</summary>
public sealed record ApproveQuestionsRequest(List<long>? QuestionIds);

public sealed record SkippedQuestionDto(long QuestionId, string Reason);

public sealed record ApproveQuestionsResultDto(IReadOnlyList<long> Approved, IReadOnlyList<SkippedQuestionDto> Skipped, string Message);

/// <summary>Kết quả kiểm hợp lệ không lưu: lỗi theo trường, số phần chấm, cách học sinh của từng cấp sẽ thấy và kết quả chấm thử nếu có.</summary>
public sealed record ValidateQuestionRequest(SaveQuestionRequest? Question, QuestionSubmission? Submission, int? PreviewLevel);

public sealed record ValidateQuestionResultDto(
    bool IsValid,
    IReadOnlyDictionary<string, string> Errors,
    int PartsTotal,
    QuestionPreviewDto? Preview,
    GradeResultDto? Trial);

/// <summary>Cách học sinh làm câu: một lần chọn, ghép cặp, sắp xếp, điền chữ hoặc đánh dấu từng mệnh đề (chấm thử và chấm bài dùng chung).</summary>
/// <param name="SelectedIndex">Dạng chọn một phương án: vị trí (từ 0) của phương án đã chọn.</param>
/// <param name="Matches">Ghép cặp: với mỗi vế trái (từ 0), vị trí của vế phải đã nối (null nếu chưa nối).</param>
/// <param name="Order">Sắp xếp: vị trí (từ 0) của các mục theo thứ tự học sinh đã đặt; null hoặc rỗng nếu chưa sắp xếp.</param>
/// <param name="Blanks">Điền từ: chữ ở từng chỗ trống theo thứ tự (gõ hoặc chọn từ kho từ).</param>
/// <param name="Statements">Nhiều mệnh đề: Đúng (true) hoặc Sai (false) cho từng mệnh đề, null nếu chưa chọn.</param>
public sealed record QuestionSubmission(
    int? SelectedIndex = null,
    List<int?>? Matches = null,
    List<int>? Order = null,
    List<string?>? Blanks = null,
    List<bool?>? Statements = null);

/// <summary>Kết quả chấm một câu: số phần đúng trên tổng số phần và từng phần.</summary>
public sealed record GradeResultDto(int PartsCorrect, int PartsTotal, IReadOnlyList<bool> Parts, bool IsCorrect, decimal Fraction);

/// <summary>
/// Câu như học sinh của một cấp thấy: không có đáp án đúng; các cột của ghép cặp, sắp xếp, kho từ đã xáo (thứ tự xáo cố định theo câu và cấp để
/// xem trước ổn định). <see cref="Layout"/> và <see cref="ReadAloud"/> theo cấp: cấp 1 thẻ lớn và đọc to lời câu, cấp 2 chuẩn, cấp 3 gọn.
/// </summary>
public sealed record QuestionPreviewDto(
    int Level,
    string Layout,
    bool ReadAloud,
    QuestionType QuestionType,
    string TypeName,
    string Content,
    string? ImageUrl,
    string? Passage,
    string? PassageSource,
    IReadOnlyList<QuestionPreviewOptionDto> Options,
    IReadOnlyList<QuestionPreviewOptionDto> LeftItems,
    IReadOnlyList<string> RightItems,
    IReadOnlyList<string> Items,
    int BlankCount,
    IReadOnlyList<string> WordBank,
    IReadOnlyList<string> Statements);

public sealed record QuestionPreviewOptionDto(string? Content, string? ImageUrl);

// ---- AI sinh câu hỏi ----

/// <summary>
/// Yêu cầu AI sinh câu từ một bài học (màn hình Sinh và duyệt câu hỏi bằng AI): 1-20 câu, độ khó, các dạng trong dạng dùng được ở cấp của lớp
/// (để trống thì lấy mọi dạng của cấp, chia đều), và một di sản trong các di sản của bài (để trống thì AI chọn trong các di sản của bài).
/// </summary>
public sealed record GenerateQuestionsRequest(
    int? Count,
    DifficultyLevel? DifficultyLevel,
    List<QuestionType>? Types = null,
    long? HeritageId = null);

/// <summary>Kết quả một lần sinh: các câu đã vào ngân hàng ở trạng thái chờ duyệt, số câu nhận được, lượt AI còn lại.</summary>
public sealed record QuestionBatchDto(
    long GenerationId,
    int Level,
    string LevelName,
    int Requested,
    int Received,
    int Dropped,
    IReadOnlyList<QuestionDetailDto> Questions,
    AiQuotaDto AiQuota,
    string Model,
    string Message);

/// <summary>Quản trị hệ thống ẩn câu kém chất lượng; lý do (tùy chọn) ghi vào nhật ký thao tác.</summary>
public sealed record AdminHideQuestionRequest(string? Reason);
