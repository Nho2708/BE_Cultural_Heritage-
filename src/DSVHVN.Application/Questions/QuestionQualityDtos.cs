using DSVHVN.Domain.Enums;

namespace DSVHVN.Application.Questions;

// Hợp đồng dữ liệu màn Kiểm duyệt ngân hàng câu hỏi của quản trị hệ thống: danh sách kèm chất lượng tính từ kết quả làm bài.

/// <summary>Cách sắp xếp danh sách kiểm duyệt: câu mới nhất trước, hoặc câu có tỉ lệ đúng thấp nhất trước (câu chưa ai làm xếp cuối).</summary>
public enum QuestionQualitySort
{
    NEWEST,
    LOWEST_RATE,
}

/// <summary>
/// Hộp lọc của danh sách kiểm duyệt. <see cref="Status"/> chỉ nhận APPROVED (đang trong ngân hàng) hoặc REJECTED (đã bị ẩn); bỏ trống là cả hai.
/// <see cref="LowQualityOnly"/> chỉ giữ câu cần xem lại (đủ lượt trả lời mà tỉ lệ đúng quá thấp). <see cref="OrganizationId"/> lọc theo trường của người tạo.
/// </summary>
public sealed record QuestionQualityQuery(
    ReviewStatus? Status = null,
    bool LowQualityOnly = false,
    QuestionQualitySort? Sort = null,
    QuestionType? QuestionType = null,
    int? Level = null,
    DifficultyLevel? DifficultyLevel = null,
    long? HeritageId = null,
    long? OrganizationId = null,
    long? CreatedBy = null,
    string? Keyword = null,
    int? Page = null,
    int? PageSize = null);

/// <summary>
/// Một dòng kiểm duyệt: dòng câu hỏi của ngân hàng kèm chất lượng. <see cref="Answered"/> là số lượt trả lời (tính cả bỏ trống) của các lượt đã nộp
/// (đã nộp hoặc hết giờ tự nộp), <see cref="Correct"/> là số lượt đúng hoàn toàn; <see cref="CorrectRatePercent"/> null khi chưa ai làm.
/// <see cref="LowQuality"/> bật khi đủ <c>10</c> lượt trả lời mà tỉ lệ đúng dưới <c>30%</c>. <see cref="HiddenAt"/> là lúc bị ẩn (câu đang bị ẩn).
/// </summary>
public sealed record QuestionQualityRowDto(
    QuestionRowDto Question,
    int Answered,
    int Correct,
    decimal? CorrectRatePercent,
    bool LowQuality,
    DateTime? HiddenAt);

/// <summary>
/// Trang kiểm duyệt. Ba số đếm tính trên toàn ngân hàng (không phụ thuộc bộ lọc): câu đang trong ngân hàng, câu đã bị ẩn, câu cần xem lại
/// trong số câu đang trong ngân hàng. Ngưỡng nằm ở <see cref="MinAnswers"/> và <see cref="LowCorrectPercent"/> để giao diện giải thích dấu hiệu.
/// </summary>
public sealed record QuestionQualityPageDto(
    IReadOnlyList<QuestionQualityRowDto> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages,
    int ApprovedCount,
    int HiddenCount,
    int LowQualityCount,
    int MinAnswers,
    int LowCorrectPercent);
