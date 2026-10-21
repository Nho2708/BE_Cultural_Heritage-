using DSVHVN.Domain.Common;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Heritages;

/// <summary>
/// Bảng <c>heritage_narrations</c>: thuyết minh tạo sẵn của bản tóm tắt ngắn (<c>heritage_summaries.short_text</c>) theo ngôn ngữ (vi, en, ko, zh, ja):
/// tệp MP3 do giọng tự host tạo trên máy phát triển rồi tải lên qua API quản trị (máy chủ không tạo giọng), mốc thời gian từng câu để tô sáng khi phát,
/// và băm của chính đoạn chữ đã đọc. Mỗi (di sản, ngôn ngữ) đúng một dòng: tải lên lần nữa thì thay dòng cũ. Tóm tắt đổi chữ thì băm lệch và
/// bản thuyết minh không còn được trả cho khách cho tới khi tạo lại; khi đó tab "Nghe" đọc bằng giọng thiết bị như trước.
/// </summary>
public class HeritageNarration : IHasCreatedAt, IHasUpdatedAt
{
    public long Id { get; set; }

    public long HeritageId { get; set; }
    public Heritage? Heritage { get; set; }

    public LanguageCode LanguageCode { get; set; }

    /// <summary>SHA-256 (64 ký tự hex thường) của <c>short_text</c> đúng như đang lưu lúc tạo giọng.</summary>
    public string TextHash { get; set; } = string.Empty;

    /// <summary>Đường dẫn tệp MP3 trong kho tệp của nền tảng (<c>/files/thuyet-minh/{mã}.mp3</c> hoặc địa chỉ đầy đủ của kho).</summary>
    public string AudioUrl { get; set; } = string.Empty;

    /// <summary>Thời lượng tệp, mili giây.</summary>
    public int DurationMs { get; set; }

    /// <summary>JSON mảng câu <c>[{ "text", "startMs", "endMs" }]</c> theo thứ tự đọc; <c>text</c> là câu gốc để hiển thị.</summary>
    public string Sentences { get; set; } = string.Empty;

    /// <summary>Mã giọng đã dùng, ví dụ <c>vieneu-mai-anh</c>, <c>melo-en-us</c>.</summary>
    public string Voice { get; set; } = string.Empty;

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
