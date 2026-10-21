using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DSVHVN.Domain.Rules;

/// <summary>Một câu của thuyết minh tạo sẵn: câu gốc để hiển thị và mốc bắt đầu, kết thúc (mili giây) trong tệp đã ghép.</summary>
public sealed record NarrationSentence(string Text, int StartMs, int EndMs);

/// <summary>Lý do một danh sách câu bị từ chối; <see cref="None"/> khi hợp lệ.</summary>
public enum SentenceProblem
{
    None,

    /// <summary>Không có câu nào, hoặc nhiều hơn <see cref="NarrationRules.MaxSentences"/> câu.</summary>
    Count,

    /// <summary>Câu trống hoặc dài quá <see cref="NarrationRules.SentenceMaxLength"/> ký tự.</summary>
    Text,

    /// <summary>Mốc âm: <c>startMs</c> &lt; 0, hoặc <c>startMs</c> không nhỏ hơn <c>endMs</c>.</summary>
    Range,

    /// <summary><c>endMs</c> vượt quá thời lượng tệp cộng <see cref="NarrationRules.EndSlackMs"/>.</summary>
    BeyondDuration,

    /// <summary>Câu sau bắt đầu sớm hơn câu trước (<c>startMs</c> phải không giảm).</summary>
    Order,
}

/// <summary>
/// Quy tắc thuần của thuyết minh tạo sẵn: băm đoạn chữ đã đọc, nhận dạng tệp MP3 bằng nội dung, giới hạn thời lượng, giọng và danh sách câu,
/// và nhận ra khóa của tệp MP3 trong kho tệp từ một đường dẫn đã lưu. Dung lượng tối đa của tệp nằm ở <see cref="StoredFileRules.MaxNarrationBytes"/>.
/// </summary>
public static partial class NarrationRules
{
    /// <summary>Băm SHA-256 viết hex thường: 64 ký tự.</summary>
    public const int HashLength = 64;

    public const int VoiceMaxLength = 100;

    /// <summary>Thời lượng tối đa 30 phút (bản tóm tắt ngắn đọc trong khoảng một phút; biên rộng để không chặn giọng chậm).</summary>
    public const int MaxDurationMs = 1_800_000;

    public const int MaxSentences = 300;

    /// <summary>Một câu không dài hơn cả bản tóm tắt ngắn.</summary>
    public const int SentenceMaxLength = TranslationRules.SummaryShortMaxLength;

    /// <summary>Mốc kết thúc của câu cuối được vượt thời lượng tối đa chừng này (sai số làm tròn của công cụ ghép).</summary>
    public const int EndSlackMs = 1000;

    public const string AudioContentType = "audio/mpeg";

    /// <summary>
    /// Băm của đoạn chữ sẽ đọc: SHA-256 trên các byte UTF-8 của <paramref name="text"/> đúng như đang lưu (không cắt khoảng trắng, không chuẩn hóa),
    /// viết 64 ký tự hex thường. Chỉ máy chủ tính; công cụ tạo giọng nhận băm từ API danh sách và gửi lại nguyên văn khi tải lên.
    /// </summary>
    public static string TextHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Băm do người gọi gửi lên có đúng dạng 64 ký tự hex thường không.</summary>
    public static bool IsValidHash(string? hash) => hash is not null && HashPattern().IsMatch(hash);

    /// <summary>
    /// Tệp có phải MP3 không, xét theo nội dung chứ không theo đuôi hay loại nội dung khai báo: mở đầu bằng thẻ <c>ID3</c> hoặc một khung MPEG
    /// (hai byte đầu <c>0xFF</c> rồi <c>0xE0..0xFF</c>, tức 11 bit đồng bộ).
    /// </summary>
    public static bool IsMp3(ReadOnlySpan<byte> content)
    {
        if (content.Length < 3) return false;
        if (content[0] == (byte)'I' && content[1] == (byte)'D' && content[2] == (byte)'3') return true;
        return content[0] == 0xFF && (content[1] & 0xE0) == 0xE0;
    }

    /// <summary>
    /// Kiểm danh sách câu: 1..<see cref="MaxSentences"/> câu, mỗi câu có chữ (sau khi cắt khoảng trắng) và không quá <see cref="SentenceMaxLength"/> ký tự,
    /// <c>0 ≤ startMs &lt; endMs ≤ durationMs + <see cref="EndSlackMs"/></c>, <c>startMs</c> không giảm giữa các câu.
    /// </summary>
    public static SentenceProblem Check(IReadOnlyList<NarrationSentence> sentences, int durationMs)
    {
        if (sentences.Count is 0 or > MaxSentences) return SentenceProblem.Count;
        var previousStart = 0;
        foreach (var sentence in sentences)
        {
            if (string.IsNullOrWhiteSpace(sentence.Text) || sentence.Text.Length > SentenceMaxLength) return SentenceProblem.Text;
            if (sentence.StartMs < 0 || sentence.StartMs >= sentence.EndMs) return SentenceProblem.Range;
            if (sentence.EndMs > durationMs + EndSlackMs) return SentenceProblem.BeyondDuration;
            if (sentence.StartMs < previousStart) return SentenceProblem.Order;
            previousStart = sentence.StartMs;
        }
        return SentenceProblem.None;
    }

    /// <summary>
    /// Khóa trong kho của tệp thuyết minh mà <paramref name="url"/> trỏ tới (<c>thuyet-minh/{32 ký tự}.mp3</c>), hoặc null nếu đường dẫn không có dạng đó.
    /// Chỉ nhận đúng hai dạng kho sinh ra: <c>/files/thuyet-minh/{mã}.mp3</c> (có thể kèm địa chỉ máy chủ) và địa chỉ của Cloudinary
    /// <c>https://res.cloudinary.com/{tên}/raw/upload/{thư mục}/thuyet-minh/{mã}.mp3</c>.
    /// </summary>
    public static string? StoredNarrationKey(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var value = url.Trim();
        var local = StoredNarrationPath().Match(value);
        if (local.Success) return local.Groups["key"].Value;
        var cloud = CloudinaryNarrationUrl().Match(value);
        return cloud.Success ? cloud.Groups["key"].Value : null;
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();

    [GeneratedRegex(@"^(?:https?://[^/\s]+)?/files/(?<key>thuyet-minh/[0-9a-f]{32}\.mp3)$")]
    private static partial Regex StoredNarrationPath();

    [GeneratedRegex(@"^https://res\.cloudinary\.com/[A-Za-z0-9_-]+/raw/upload/(?:v\d+/)?(?:[A-Za-z0-9_-]+/)*(?<key>thuyet-minh/[0-9a-f]{32}\.mp3)$")]
    private static partial Regex CloudinaryNarrationUrl();
}
