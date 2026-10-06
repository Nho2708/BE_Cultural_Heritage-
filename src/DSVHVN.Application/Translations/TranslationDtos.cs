using DSVHVN.Domain.Enums;

namespace DSVHVN.Application.Translations;

// Dữ liệu vào, ra của luồng Đa ngôn ngữ: phía công khai (nội dung theo ngôn ngữ, tóm tắt) và phía quản trị hệ thống (bản dịch, tóm tắt, duyệt).
// Mã ngôn ngữ trả ra là mã BCP 47 của giao diện (vi, en, ko, zh-Hans, ja); trạng thái viết HOA như CSDL.

// ---- Công khai ----

/// <summary>
/// Kết quả chọn ngôn ngữ của một phản hồi công khai. <c>RequestedLanguage</c> là ngôn ngữ khách yêu cầu (đã chuẩn hóa), <c>Language</c> là ngôn ngữ nội dung
/// đang hiện: khác nhau khi <c>IsFallback</c> (chưa có bản hiện hành, đang hiện tiếng Việt: giao diện hiện thông báo một lần cho cả trang).
/// <c>IsMachineTranslated</c> bật khi nội dung đang hiện là bản dịch máy (tiếng Hàn, Trung, Nhật): giao diện gắn nhãn "bản dịch máy".
/// </summary>
public sealed record LocalizationDto(string RequestedLanguage, string Language, bool IsFallback, bool IsMachineTranslated);

/// <summary>Một ngôn ngữ của bộ chọn: mã, tên viết bằng chính ngôn ngữ đó, mã giọng đọc của thiết bị, và cờ cho biết là bản gốc hay chỉ có bản dịch máy.</summary>
public sealed record LanguageOptionDto(string Code, string NativeName, string SpeechTag, bool IsSource, bool IsMachineOnly);

/// <summary>
/// Tóm tắt của một di sản ở ngôn ngữ yêu cầu (lấy từ bản đã lưu, không gọi AI): <c>ShortText</c> để nghe thuyết minh, <c>DetailedText</c> để đọc.
/// Ngôn ngữ chưa có tóm tắt hiện hành thì trả tóm tắt tiếng Việt đã duyệt và <c>Localization.IsFallback</c> bật.
/// </summary>
public sealed record PublicSummaryDto(long HeritageId, string Slug, string Name, LocalizationDto Localization, string ShortText, string DetailedText);

// ---- Quản trị: tổng quan, hàng chờ ----

/// <summary>
/// Tình trạng bản dịch của cả di sản ở danh sách Quản lý di sản: <c>NONE</c> chưa dịch; <c>OUTDATED</c> có bản cần dịch lại (nội dung tiếng Việt đã sửa sau khi dịch);
/// <c>PENDING</c> có bản chờ duyệt; <c>APPROVED</c> đã dịch và không còn bản chờ duyệt hay cần dịch lại (gồm cả bản dịch máy). Xét theo thứ tự OUTDATED, PENDING, APPROVED.
/// </summary>
public enum TranslationState
{
    NONE,
    PENDING,
    APPROVED,
    OUTDATED,
}

/// <summary>Bản dịch nội dung hay bản tóm tắt: hai đơn vị duyệt của mỗi (di sản, ngôn ngữ).</summary>
public enum TranslationKind
{
    TRANSLATION,
    SUMMARY,
}

/// <summary>Phạm vi một lần duyệt hoặc từ chối: bản dịch, tóm tắt, hoặc cả hai của cùng một ngôn ngữ.</summary>
public enum ReviewScope
{
    TRANSLATION,
    SUMMARY,
    BOTH,
}

/// <summary>Tab của màn hình Duyệt bản dịch và tóm tắt: chờ duyệt, đã duyệt, bản dịch máy (xem, không có bước duyệt), bị từ chối (chờ dịch lại).</summary>
public enum QueueTab
{
    PENDING,
    APPROVED,
    MACHINE,
    REJECTED,
}

/// <summary>
/// Một ô của bảng 5 ngôn ngữ × 2 đơn vị duyệt. <c>Status</c> null nghĩa là chưa có bản; <c>IsOutdated</c> bật khi nội dung tiếng Việt đã sửa sau lần dịch
/// (bản cũ vẫn hiện tới khi dịch lại). <c>Label</c> là nhãn hiển thị: Chưa có, Bản dịch máy, Chờ duyệt, Đã duyệt, Bị từ chối, Cần dịch lại.
/// </summary>
public sealed record TranslationCellDto(
    TranslationKind Kind,
    string Language,
    string LanguageLabel,
    TranslationStatus? Status,
    bool IsOutdated,
    string Label,
    DateTime? CreatedAt,
    DateTime? UpdatedAt,
    DateTime? ReviewedAt,
    long? ReviewedBy,
    string? ReviewedByName);

/// <summary>
/// Tab Bản dịch và tóm tắt của một di sản: bốn ô bản dịch (en, ko, zh-Hans, ja), năm ô tóm tắt, số bản cần dịch lại kèm dải nhắc.
/// <c>CanGenerate</c> false (kèm lý do) khi di sản chưa có nội dung tiếng Việt, bị ẩn hoặc đang có yêu cầu dịch chạy.
/// </summary>
public sealed record TranslationOverviewDto(
    long HeritageId,
    string Name,
    bool CanGenerate,
    string? CannotGenerateReason,
    int OutdatedCount,
    string? OutdatedNotice,
    IReadOnlyList<TranslationCellDto> Translations,
    IReadOnlyList<TranslationCellDto> Summaries);

/// <summary>Số đếm của các tab Duyệt bản dịch và tóm tắt (đếm cả bản dịch lẫn tóm tắt).</summary>
public sealed record TranslationCountsDto(int Pending, int Approved, int Machine, int Outdated);

/// <summary>Một dòng của hàng chờ: di sản, loại (bản dịch hay tóm tắt), ngôn ngữ, trạng thái, ngày yêu cầu (lần ghi gần nhất).</summary>
public sealed record TranslationQueueItemDto(
    long HeritageId,
    string HeritageName,
    string Slug,
    TranslationKind Kind,
    string Language,
    string LanguageLabel,
    TranslationStatus Status,
    bool IsOutdated,
    DateTime? RequestedAt,
    DateTime? ReviewedAt);

/// <summary>Bộ lọc hàng chờ (tab mặc định là chờ duyệt); <c>Language</c> nhận mã BCP 47 hoặc mã ngắn (zh, zh-Hans).</summary>
public sealed record TranslationQueueQuery(QueueTab? Tab, long? HeritageId, string? Language, TranslationKind? Kind, int? Page, int? PageSize);

// ---- Quản trị: xem và sửa nội dung ----

public sealed record TimelineTextDto(long Id, string? Name, string? Description);

public sealed record EventTextDto(long Id, long TimelineId, string? Name, string? Description);

/// <summary><c>EventId</c> có khi ảnh thuộc sự kiện, null khi ảnh thuộc di sản.</summary>
public sealed record MediaTextDto(long Id, long? EventId, string? Caption);

/// <summary>Mọi phần chữ của một di sản ở một ngôn ngữ: di sản, giai đoạn, sự kiện, chú thích ảnh. Dùng cho cả bản gốc tiếng Việt và bản dịch.</summary>
public sealed record TranslationTextDto(
    string? Name,
    string? ShortDescription,
    string? Content,
    string? Address,
    IReadOnlyList<TimelineTextDto> Timelines,
    IReadOnlyList<EventTextDto> Events,
    IReadOnlyList<MediaTextDto> Media);

/// <summary>Hai cột của màn hình duyệt: bản gốc tiếng Việt bên trái, bản dịch (đang chờ duyệt hoặc đã có) bên phải.</summary>
public sealed record TranslationDetailDto(
    long HeritageId,
    string HeritageName,
    string Slug,
    string Language,
    string LanguageLabel,
    TranslationStatus Status,
    bool IsOutdated,
    DateTime? ReviewedAt,
    string? ReviewedByName,
    TranslationTextDto Original,
    TranslationTextDto Translated);

public sealed record SummaryTextDto(string ShortText, string DetailedText);

/// <summary>
/// Bản tóm tắt của một ngôn ngữ để xem và duyệt: <c>Original</c> là nội dung tiếng Việt của di sản (nguồn của tóm tắt), <c>ViSummary</c> là tóm tắt tiếng Việt
/// hiện có (null khi chính ngôn ngữ này là tiếng Việt hoặc chưa có), <c>Summary</c> là bản đang xem.
/// </summary>
public sealed record SummaryDetailDto(
    long HeritageId,
    string HeritageName,
    string Slug,
    string Language,
    string LanguageLabel,
    TranslationStatus Status,
    bool IsOutdated,
    DateTime? ReviewedAt,
    string? ReviewedByName,
    TranslationTextDto Original,
    SummaryTextDto? ViSummary,
    SummaryTextDto Summary);

// ---- Quản trị: yêu cầu ----

/// <summary>Sửa chữ của bản dịch tiếng Anh ngay tại màn hình duyệt. Phần có trong yêu cầu thay giá trị cũ; phần bỏ trống (null) giữ nguyên; mã phần không thuộc di sản thì bị từ chối.</summary>
public sealed record UpdateTranslationRequest(
    string? Name,
    string? ShortDescription,
    string? Content,
    string? Address,
    IReadOnlyList<TimelineTextDto>? Timelines,
    IReadOnlyList<EventTextDto>? Events,
    IReadOnlyList<MediaTextDto>? Media);

/// <summary>Sửa chữ của bản tóm tắt tiếng Việt hoặc tiếng Anh.</summary>
public sealed record UpdateSummaryRequest(string? ShortText, string? DetailedText);

/// <summary>Duyệt một ngôn ngữ: <c>Kind</c> là bản dịch, tóm tắt hoặc cả hai.</summary>
public sealed record ApproveTranslationRequest(ReviewScope? Kind);

/// <summary>Từ chối (yêu cầu dịch lại); <c>Reason</c> không bắt buộc, tối đa 500 ký tự, chỉ ghi vào nhật ký thao tác.</summary>
public sealed record RejectTranslationRequest(ReviewScope? Kind, string? Reason);

/// <summary>Kết quả duyệt hoặc từ chối: tổng quan mới của di sản, kèm cờ cho biết việc dịch lại tiếng Hàn, Trung, Nhật từ bản tiếng Anh đã duyệt có chạy không.</summary>
public sealed record ReviewResultDto(TranslationOverviewDto Overview, bool MachineTranslationsRefreshed, string? Warning);
