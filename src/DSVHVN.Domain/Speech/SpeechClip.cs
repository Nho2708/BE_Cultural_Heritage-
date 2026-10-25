using DSVHVN.Domain.Common;

namespace DSVHVN.Domain.Speech;

/// <summary>
/// Bảng <c>speech_clips</c>: bộ nhớ đệm bền của giọng đọc tự host. Mỗi dòng là một đoạn chữ (đã chuẩn hóa khoảng trắng) đã được đọc bằng một giọng và lưu thành tệp MP3
/// trong kho tệp (<c>giong-doc/{giọng}/{băm}.mp3</c>). Cùng chữ và cùng giọng thì chỉ sinh một lần: lần sau trả ngay đường dẫn tệp. Bảng không chứa chữ gốc
/// (chỉ băm), vì chữ có thể là nội dung bài học của giáo viên.
/// </summary>
public class SpeechClip : IHasCreatedAt
{
    public long Id { get; set; }

    /// <summary>SHA-256 (64 ký tự hex thường) của giọng và chữ đã chuẩn hóa; xem <c>SpeechRules.Hash</c>.</summary>
    public string TextHash { get; set; } = string.Empty;

    /// <summary>Mã giọng đã dùng, ví dụ <c>vieneu-mai-anh</c>.</summary>
    public string Voice { get; set; } = string.Empty;

    /// <summary>Khóa tệp MP3 trong kho tệp (không phải đường dẫn công khai, để đổi nơi lưu vẫn đúng).</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>Thời lượng tệp, mili giây (do dịch vụ AI báo).</summary>
    public int DurationMs { get; set; }

    /// <summary>Dung lượng tệp, byte.</summary>
    public int ByteSize { get; set; }

    public DateTime? CreatedAt { get; set; }
}
