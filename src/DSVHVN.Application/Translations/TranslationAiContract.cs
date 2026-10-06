using DSVHVN.Application.Lessons;

namespace DSVHVN.Application.Translations;

// Hợp đồng gọi Dịch vụ AI để tóm tắt và dịch di sản (POST /v1/summarize-heritage và POST /v1/translate-heritage trong kiến trúc). Backend chỉ biết giao diện
// IHeritageAiClient: bản giả lập (không gọi nhà cung cấp nào) và bản gọi dịch vụ AI nội bộ qua HTTP cùng thực hiện giao diện này, chọn bằng cấu hình Ai:Mode
// như soạn nội dung bài học. Dữ liệu gửi đi chỉ gồm chữ của di sản (không có thông tin người dùng). Mã ngôn ngữ trong hợp đồng là mã BCP 47: vi, en, ko, zh-Hans, ja.

/// <summary>Tên và mô tả của một giai đoạn (mã giữ nguyên khi dịch để ghép lại).</summary>
public sealed record AiTimelineText(long Id, string Name, string? Description);

/// <summary>Tên và mô tả của một sự kiện.</summary>
public sealed record AiEventText(long Id, long TimelineId, string Name, string? Description);

/// <summary>Chú thích của một ảnh (ghi công không dịch nên không gửi).</summary>
public sealed record AiMediaText(long Id, string Caption);

/// <summary>
/// Mọi phần chữ của di sản cần dịch: tên, mô tả ngắn, nội dung, giai đoạn, sự kiện, chú thích ảnh. Địa chỉ không gửi (giữ như văn bản xếp hạng).
/// </summary>
public sealed record AiHeritageText(
    string Name,
    string? ShortDescription,
    string? Content,
    IReadOnlyList<AiTimelineText> Timelines,
    IReadOnlyList<AiEventText> Events,
    IReadOnlyList<AiMediaText> Media);

/// <summary>Bản tóm tắt: bản ngắn để nghe (khoảng 80-120 từ tiếng Việt) và bản chi tiết để đọc (khoảng 250-400 từ).</summary>
public sealed record AiSummaryText(string ShortText, string DetailedText);

/// <summary>Yêu cầu tóm tắt bằng tiếng Việt từ chính nội dung đã có của di sản (không thêm dữ kiện mới, không chép nguyên bài).</summary>
public sealed record AiSummarizeRequest(long GenerationId, long HeritageId, AiHeritageText Source);

public sealed record AiSummarizeResult(string ShortText, string DetailedText, string Model, AiUsage Usage);

/// <summary>
/// Yêu cầu dịch sang một ngôn ngữ đích. <see cref="Content"/> viết bằng <see cref="ContentLanguage"/> (tiếng Việt, hoặc tiếng Anh đã duyệt khi dịch sang tiếng Hàn, Trung, Nhật);
/// <see cref="Summary"/> (nếu có) viết bằng <see cref="SummaryLanguage"/> (luôn tiếng Việt, tóm tắt các ngôn ngữ khác dịch từ tóm tắt tiếng Việt).
/// </summary>
public sealed record AiTranslateRequest(
    long GenerationId,
    long HeritageId,
    string TargetLanguage,
    string ContentLanguage,
    AiHeritageText Content,
    string? SummaryLanguage,
    AiSummaryText? Summary);

/// <summary>Bản dịch: mã giai đoạn, sự kiện, ảnh giữ như đã gửi; phần nào không dịch được thì bỏ khỏi danh sách (nội dung công khai lùi về tiếng Việt cho phần đó).</summary>
public sealed record AiTranslateResult(AiHeritageText Content, AiSummaryText? Summary, string Model, AiUsage Usage);

/// <summary>Cổng gọi Dịch vụ AI tóm tắt và dịch di sản. Người gọi đặt thời gian chờ mỗi lần qua <c>CancellationToken</c>.</summary>
public interface IHeritageAiClient
{
    /// <summary>Tên mô hình hoặc bản giả lập, ghi vào <c>ai_generations.model</c> của lần gọi thành công.</summary>
    string ModelName { get; }

    Task<AiSummarizeResult> SummarizeHeritageAsync(AiSummarizeRequest request, string correlationId, CancellationToken ct);

    Task<AiTranslateResult> TranslateHeritageAsync(AiTranslateRequest request, string correlationId, CancellationToken ct);
}
