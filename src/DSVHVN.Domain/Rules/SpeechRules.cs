using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DSVHVN.Domain.Rules;

/// <summary>
/// Quy tắc thuần của giọng đọc tự host: chuẩn hóa chữ trước khi đọc, băm để làm khóa bộ nhớ đệm, tách câu, và các giới hạn của hàng đợi tạo trước.
/// Cùng một chữ (sau khi bỏ khoảng trắng thừa) và cùng giọng luôn ra cùng một băm nên chỉ sinh âm thanh một lần.
/// </summary>
public static partial class SpeechRules
{
    /// <summary>Giọng mặc định: "Mai Anh" của VieNeu-TTS, cùng giọng với thuyết minh tạo sẵn của tóm tắt.</summary>
    public const string DefaultVoice = "vieneu-mai-anh";

    /// <summary>Độ dài tối đa của mã giọng (khớp cột <c>speech_clips.voice</c>).</summary>
    public const int VoiceMaxLength = 100;

    /// <summary>Độ dài tối đa của mã giọng khi dùng làm tên thư mục trong kho tệp.</summary>
    public const int VoiceFolderMaxLength = 64;

    /// <summary>Một lần đọc tối đa 1000 ký tự (sau khi chuẩn hóa), khớp giới hạn của dịch vụ AI.</summary>
    public const int TextMaxLength = 1000;

    /// <summary>Một lần gửi tạo trước tối đa 40 đoạn chữ.</summary>
    public const int PrefetchMaxTexts = 40;

    /// <summary>Hàng đợi tạo trước chứa tối đa chừng này đoạn chờ; đầy thì đoạn mới bị bỏ (người dùng nghe tới đâu sinh tới đó).</summary>
    public const int PrefetchQueueCapacity = 2000;

    /// <summary>Thời gian tối đa một yêu cầu nghe ngay chờ giọng máy chủ, giây; hết hạn thì trả null và giao diện lùi về giọng thiết bị.</summary>
    public const int DefaultWaitSeconds = 30;

    /// <summary>
    /// Chữ đã chuẩn hóa: cắt hai đầu và gộp mọi dãy khoảng trắng (kể cả xuống dòng) thành một dấu cách. Rỗng hoặc dài hơn
    /// <see cref="TextMaxLength"/> ký tự thì trả null (không đọc).
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = WhiteSpace().Replace(text.Trim(), " ");
        return normalized.Length is >= 1 and <= TextMaxLength ? normalized : null;
    }

    /// <summary>Mã giọng hợp lệ: chữ thường, chữ số và gạch nối, bắt đầu bằng chữ hoặc số, tối đa <see cref="VoiceFolderMaxLength"/> ký tự (an toàn làm tên thư mục).</summary>
    public static bool IsValidVoice(string? voice) => voice is not null && VoicePattern().IsMatch(voice);

    /// <summary>
    /// Băm làm khóa bộ nhớ đệm: SHA-256 trên byte UTF-8 của <c>{giọng}\n{chữ đã chuẩn hóa}</c>, viết 64 ký tự hex thường.
    /// Người gọi phải chuẩn hóa chữ bằng <see cref="Normalize"/> trước.
    /// </summary>
    public static string Hash(string normalizedText, string voice) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(voice + "\n" + normalizedText)));

    /// <summary>
    /// Tách chữ thành câu ở chỗ sau dấu kết câu (<c>. ! ? …</c>, có thể kèm ngoặc hoặc nháy đóng) mà theo sau là khoảng trắng; dấu kết câu ở cuối chữ ở lại với câu cuối.
    /// Câu được cắt khoảng trắng, bỏ câu rỗng. Chữ rỗng trả danh sách rỗng.
    /// </summary>
    public static IReadOnlyList<string> SplitSentences(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return SentenceBoundary().Split(text.Trim())
            .Select(s => WhiteSpace().Replace(s.Trim(), " "))
            .Where(s => s.Length > 0)
            .ToList();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex VoicePattern();

    [GeneratedRegex(@"(?<=[.!?…]+[""')\]”’」』]*)\s+")]
    private static partial Regex SentenceBoundary();
}
