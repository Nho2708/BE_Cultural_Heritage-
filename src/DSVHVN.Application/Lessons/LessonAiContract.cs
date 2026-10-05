using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Lessons;

// Hợp đồng gọi Dịch vụ AI để soạn nội dung bài học theo cấp (POST /v1/compose-lesson trong kiến trúc). Backend chỉ biết giao diện
// ILessonAiClient: bản giả lập (không gọi nhà cung cấp nào) và bản gọi dịch vụ AI nội bộ qua HTTP cùng thực hiện giao diện này,
// chọn bằng cấu hình Ai:Mode. Dữ liệu gửi đi chỉ gồm nội dung di sản, cấp học của lớp và danh sách ảnh có sẵn của di sản;
// không có họ tên, mã học sinh, tên lớp, email.

/// <summary>Một mốc trên dòng thời gian của di sản gửi cho AI (sự kiện của giai đoạn).</summary>
public sealed record AiTimelineInput(long EventId, string Phase, string Name, string? Description, int StartYear, string DateLabel);

/// <summary>Một nguồn tham chiếu của di sản: tiêu đề (đã ghi cơ quan, ngày truy cập) và đường dẫn.</summary>
public sealed record AiSourceInput(string Title, string? Url);

/// <summary>Một ảnh có sẵn của di sản AI được chọn (AI không tạo ảnh mới).</summary>
public sealed record AiImageInput(long MediaId, string Url, string? Caption);

/// <summary>Nội dung một di sản đã xuất bản của bài.</summary>
public sealed record AiHeritageInput(
    long HeritageId,
    string Name,
    string? ShortDescription,
    string? Content,
    IReadOnlyList<AiTimelineInput> Timeline,
    IReadOnlyList<AiSourceInput> Sources,
    IReadOnlyList<AiImageInput> Images);

/// <summary>
/// Yêu cầu soạn nội dung bài. <see cref="GradeLevel"/> là cấp học suy từ khối lớp (1, 2, 3); <see cref="Length"/> nhắm tới số từ trong
/// khoảng của cấp; <see cref="CurrentContent"/> là nội dung hiện có của bài (có thể rỗng).
/// </summary>
public sealed record AiComposeRequest(
    long GenerationId,
    long LessonId,
    int GradeLevel,
    LessonLength Length,
    IReadOnlyList<AiHeritageInput> Heritages,
    string CurrentContent);

/// <summary>
/// Một phần có cấu trúc AI soạn. <see cref="Kind"/> là STORY (bước câu chuyện cấp 1, kèm <see cref="MediaId"/>), IMAGE (hình kèm câu
/// ngắn trong <see cref="Text"/>), MILESTONE (mốc thời gian: <see cref="Year"/>, có thể kèm <see cref="EventId"/>) hoặc REFLECTION
/// (câu hỏi gợi suy nghĩ cấp 3).
/// </summary>
public sealed record AiComposedStep(LessonSectionType Kind, string? Text, long? MediaId, int? Year, long? EventId, string? Title);

/// <summary>Đoạn trích tư liệu gốc cấp 3: phải là chuỗi con nguyên văn của nội dung di sản đã gửi, kèm nguồn.</summary>
public sealed record AiSourceQuote(long HeritageId, string Quote, string Source);

public sealed record AiUsage(int InputTokens, int OutputTokens);

/// <summary>
/// Bản nháp AI trả về: <see cref="Content"/> là nội dung chính tiếng Việt đúng khung của cấp, <see cref="Steps"/> các phần có cấu trúc,
/// <see cref="SourceQuotes"/> chỉ cấp 3, <see cref="KeyPoints"/> 3-7 ý chính. Luôn là bản nháp: AI không tự xuất bản.
/// </summary>
public sealed record AiComposeResult(
    string Content,
    IReadOnlyList<AiComposedStep> Steps,
    IReadOnlyList<AiSourceQuote> SourceQuotes,
    IReadOnlyList<string> KeyPoints,
    int GradeLevel,
    LessonLength Length,
    string Model,
    AiUsage Usage);

/// <summary>
/// Lỗi khi gọi Dịch vụ AI. <see cref="Retryable"/> true với lỗi mạng, quá thời gian, 5xx hoặc kết quả sai lược đồ (backend thử lại
/// tối đa 2 lần trong cùng một dòng nhật ký AI); false với 4xx (sai khóa nội bộ, đầu vào sai lược đồ), không thử lại.
/// </summary>
public sealed class AiServiceException(string message, bool retryable, Exception? inner = null) : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;
}

/// <summary>Cổng gọi Dịch vụ AI soạn nội dung bài học. Người gọi đặt thời gian chờ mỗi lần qua <c>CancellationToken</c>.</summary>
public interface ILessonAiClient
{
    /// <summary>Tên mô hình hoặc bản giả lập, ghi vào <c>ai_generations.model</c> của lần gọi thành công.</summary>
    string ModelName { get; }

    Task<AiComposeResult> ComposeLessonAsync(AiComposeRequest request, string correlationId, CancellationToken ct);
}

/// <summary>
/// Cấu hình gọi AI (mục <c>Ai</c>). <see cref="Mode"/> <c>Simulated</c> (mặc định): bản giả lập sinh nội dung mẫu đúng cấp từ dữ liệu di sản,
/// không gọi mạng. <c>Remote</c>: gọi Dịch vụ AI nội bộ ở <see cref="BaseUrl"/> với khóa <see cref="InternalKey"/> (khóa nội bộ giữa hai
/// dịch vụ của nhóm, không phải khóa của nhà cung cấp mô hình). Hai giá trị thời gian cho phép kiểm thử ép nhanh.
/// </summary>
public sealed class AiOptions
{
    public const string Section = "Ai";

    public AiMode Mode { get; set; } = AiMode.Simulated;

    public string BaseUrl { get; set; } = string.Empty;

    public string InternalKey { get; set; } = string.Empty;

    /// <summary>Thời gian chờ mỗi lần gọi (giây, được lẻ); mặc định 45 theo hợp đồng.</summary>
    public double AttemptTimeoutSeconds { get; set; } = LessonRules.AiAttemptTimeout.TotalSeconds;

    /// <summary>Số lần thử lại thêm sau lần đầu; mặc định 2 theo hợp đồng.</summary>
    public int MaxRetries { get; set; } = LessonRules.AiMaxRetries;

    public TimeSpan AttemptTimeout => TimeSpan.FromSeconds(Math.Max(0.05, AttemptTimeoutSeconds));
}

public enum AiMode
{
    Simulated,
    Remote,
}
