using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Rules;

/// <summary>Một ngôn ngữ của phần công khai: mã lưu trong CSDL, mã BCP 47 của giao diện, tên viết bằng chính ngôn ngữ đó và mã giọng đọc của thiết bị.</summary>
public sealed record LanguageInfo(LanguageCode Code, string Tag, string NativeName, string SpeechTag);

/// <summary>
/// Quy tắc năm ngôn ngữ của phần công khai (quy tắc Ngôn ngữ hỗ trợ và phạm vi áp dụng, Ngôn ngữ mặc định và ghi nhớ lựa chọn):
/// tiếng Việt <c>vi</c> (bản gốc), tiếng Anh <c>en</c>, tiếng Hàn <c>ko</c>, tiếng Trung giản thể <c>zh-Hans</c> (lưu <c>zh</c>), tiếng Nhật <c>ja</c>.
/// Không có tiếng Trung phồn thể và không có ngôn ngữ nào khác. Mã không thuộc năm ngôn ngữ bị bỏ qua và nội dung trả bằng tiếng Việt.
/// Cách đọc mã nhận cả mã dạng vùng của trình duyệt (<c>en-US</c>, <c>ko-KR</c>, <c>zh-CN</c>, <c>zh-Hans-SG</c>).
/// </summary>
public static class Languages
{
    public static readonly IReadOnlyList<LanguageInfo> All =
    [
        new(LanguageCode.vi, "vi", "Tiếng Việt", "vi-VN"),
        new(LanguageCode.en, "en", "English", "en-US"),
        new(LanguageCode.ko, "ko", "한국어", "ko-KR"),
        new(LanguageCode.zh, "zh-Hans", "简体中文", "zh-CN"),
        new(LanguageCode.ja, "ja", "日本語", "ja-JP"),
    ];

    /// <summary>Ba ngôn ngữ không có bước duyệt: luôn là bản dịch máy kèm nhãn.</summary>
    public static readonly IReadOnlyList<LanguageCode> MachineOnly = [LanguageCode.ko, LanguageCode.zh, LanguageCode.ja];

    /// <summary>Bốn ngôn ngữ đích của bản dịch nội dung (tiếng Việt là bản gốc).</summary>
    public static readonly IReadOnlyList<LanguageCode> TranslationTargets = [LanguageCode.en, LanguageCode.ko, LanguageCode.zh, LanguageCode.ja];

    public static LanguageInfo Info(LanguageCode code) => All.Single(l => l.Code == code);

    /// <summary>Mã BCP 47 trả ra API và dùng ở giao diện (<c>zh-Hans</c> cho tiếng Trung giản thể).</summary>
    public static string Tag(LanguageCode code) => Info(code).Tag;

    /// <summary>Nhãn tiếng Việt của ngôn ngữ cho giao diện quản trị.</summary>
    public static string ViLabel(LanguageCode code) => code switch
    {
        LanguageCode.vi => "Tiếng Việt",
        LanguageCode.en => "Tiếng Anh",
        LanguageCode.ko => "Tiếng Hàn",
        LanguageCode.zh => "Tiếng Trung giản thể",
        LanguageCode.ja => "Tiếng Nhật",
        _ => code.ToString(),
    };

    /// <summary>
    /// Đọc một mã ngôn ngữ: <c>vi</c>, <c>en</c>, <c>ko</c>, <c>ja</c> và mọi mã bắt đầu bằng đó (<c>en-GB</c>); tiếng Trung chỉ nhận <c>zh</c>,
    /// <c>zh-CN</c>, <c>zh-SG</c> và <c>zh-Hans*</c>, còn phồn thể (<c>zh-TW</c>, <c>zh-HK</c>, <c>zh-Hant</c>) không thuộc năm ngôn ngữ. Không nhận ra thì null.
    /// </summary>
    public static LanguageCode? TryParse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var parts = tag.Trim().Replace('_', '-').ToLowerInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        switch (parts[0])
        {
            case "vi": return LanguageCode.vi;
            case "en": return LanguageCode.en;
            case "ko": return LanguageCode.ko;
            case "ja": return LanguageCode.ja;
            case "zh":
                if (parts.Length == 1) return LanguageCode.zh;
                if (parts[1] == "hans") return LanguageCode.zh;
                if (parts[1] == "hant") return null;
                return parts[1] is "cn" or "sg" ? LanguageCode.zh : null;
            default: return null;
        }
    }

    /// <summary>
    /// Ngôn ngữ đầu tiên thuộc năm ngôn ngữ trong danh sách ưu tiên của trình duyệt hoặc thiết bị (đã xếp từ ưu tiên cao xuống thấp); null nếu không có.
    /// </summary>
    public static LanguageCode? FirstSupported(IEnumerable<string?> preferences)
    {
        foreach (var tag in preferences)
            if (TryParse(tag) is { } code) return code;
        return null;
    }

    /// <summary>Ngôn ngữ mặc định lần đầu mở: ngôn ngữ ưu tiên đầu tiên thuộc năm ngôn ngữ, không có thì tiếng Anh (kể cả tiếng Trung phồn thể).</summary>
    public static LanguageCode DefaultFor(IEnumerable<string?> preferences) => FirstSupported(preferences) ?? LanguageCode.en;

    /// <summary>
    /// Đọc tiêu đề <c>Accept-Language</c> (vd <c>ko-KR,ko;q=0.9,en;q=0.8</c>): sắp theo trọng số giảm dần (giữ thứ tự gặp khi bằng nhau), bỏ mục có
    /// trọng số 0, trả ngôn ngữ đầu tiên thuộc năm ngôn ngữ; không có thì null.
    /// </summary>
    public static LanguageCode? FromAcceptLanguage(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var entries = new List<(string Tag, double Quality, int Index)>();
        var index = 0;
        foreach (var raw in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = raw.Split(';', StringSplitOptions.TrimEntries);
            var quality = 1.0;
            foreach (var attribute in pieces.Skip(1))
            {
                if (attribute.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(attribute[2..], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var q))
                    quality = q;
            }
            if (quality > 0) entries.Add((pieces[0], quality, index++));
        }
        return FirstSupported(entries.OrderByDescending(e => e.Quality).ThenBy(e => e.Index).Select(e => (string?)e.Tag));
    }

    /// <summary>
    /// Ngôn ngữ của một yêu cầu công khai: tham số <c>lang</c> nếu nhận ra, không thì tiêu đề <c>Accept-Language</c>, không thì tiếng Việt.
    /// Tham số có mà không thuộc năm ngôn ngữ thì bỏ qua và trả tiếng Việt, không lùi về tiêu đề.
    /// </summary>
    public static LanguageCode Resolve(string? lang, string? acceptLanguage) =>
        !string.IsNullOrWhiteSpace(lang) ? TryParse(lang) ?? LanguageCode.vi : FromAcceptLanguage(acceptLanguage) ?? LanguageCode.vi;

    // ---- Trạng thái ----

    /// <summary>Bản dịch nội dung có bước duyệt (chỉ tiếng Anh).</summary>
    public static bool TranslationNeedsReview(LanguageCode code) => code == LanguageCode.en;

    /// <summary>Tóm tắt có bước duyệt (tiếng Việt, tiếng Anh).</summary>
    public static bool SummaryNeedsReview(LanguageCode code) => code is LanguageCode.vi or LanguageCode.en;

    /// <summary>Trạng thái của bản dịch nội dung vừa do AI tạo: chờ duyệt với tiếng Anh, bản dịch máy với tiếng Hàn, Trung, Nhật.</summary>
    public static TranslationStatus NewTranslationStatus(LanguageCode code) =>
        TranslationNeedsReview(code) ? TranslationStatus.PENDING : TranslationStatus.MACHINE;

    /// <summary>Trạng thái của tóm tắt vừa do AI tạo: chờ duyệt với tiếng Việt, tiếng Anh, bản dịch máy với tiếng Hàn, Trung, Nhật.</summary>
    public static TranslationStatus NewSummaryStatus(LanguageCode code) =>
        SummaryNeedsReview(code) ? TranslationStatus.PENDING : TranslationStatus.MACHINE;

    /// <summary>Hiện cho khách khi đã duyệt (tiếng Việt, tiếng Anh) hoặc là bản dịch máy (tiếng Hàn, Trung, Nhật); chờ duyệt và bị từ chối thì không hiện.</summary>
    public static bool IsVisible(TranslationStatus status) => status is TranslationStatus.APPROVED or TranslationStatus.MACHINE;
}
