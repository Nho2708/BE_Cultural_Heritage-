using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DSVHVN.Domain.Rules;

/// <summary>
/// Hằng số và phép kiểm thuần của kho di sản: độ dài theo cột CSDL v3, tọa độ trong lãnh thổ Việt Nam, đường dẫn (slug),
/// số từ của nội dung, ngày của sự kiện, ghi công ảnh, loại nguồn. Đổi giá trị ở đây là đổi quy tắc nghiệp vụ.
/// </summary>
public static partial class HeritageRules
{
    // Độ dài theo cột của CSDL v3 (riêng mô tả ngắn hẹp hơn cột: quy tắc nghiệp vụ tối đa 300 ký tự).
    public const int NameMaxLength = 255;
    public const int SlugMaxLength = 255;
    public const int ShortDescriptionMaxLength = 300;
    public const int AddressMaxLength = 500;
    public const int PlaceIdMaxLength = 255;
    public const int UrlMaxLength = 500;
    public const int CaptionMaxLength = 500;
    public const int CreditMaxLength = 255;
    public const int ReferenceTitleMaxLength = 500;
    public const int TimelineNameMaxLength = 255;
    public const int EventNameMaxLength = 255;

    /// <summary>Nội dung chi tiết cần tối thiểu 200 từ mới xuất bản được, đủ làm đầu vào cho AI.</summary>
    public const int MinContentWords = 200;

    /// <summary>Vĩ độ 8,0-23,5 và kinh độ 102,0-117,5 bao trọn lãnh thổ Việt Nam.</summary>
    public const decimal MinLatitude = 8.0m;
    public const decimal MaxLatitude = 23.5m;
    public const decimal MinLongitude = 102.0m;
    public const decimal MaxLongitude = 117.5m;

    /// <summary>Số chữ số thập phân của tọa độ (cột decimal(9,6)).</summary>
    public const int CoordinateScale = 6;

    /// <summary>Năm âm là trước Công nguyên; cận dưới đủ cho di chỉ tiền sử.</summary>
    public const int MinYear = -100_000;

    /// <summary>Dấu ngăn ba phần của ghi công: tác giả · giấy phép · nguồn.</summary>
    public const string CreditSeparator = " · ";

    // Loại media (media.media_type, chuỗi không CHECK).
    public const string MediaImage = "IMAGE";
    public const string MediaVideo = "VIDEO";
    public const string MediaAudio = "AUDIO";
    public static readonly IReadOnlyList<string> MediaTypes = [MediaImage, MediaVideo, MediaAudio];

    // Loại nguồn tham chiếu (heritage_references.source_type, chuỗi không CHECK): chỉ ba giá trị.
    public const string SourceGovDoc = "GOV_DOC";
    public const string SourceWebsite = "WEBSITE";
    public const string SourceBook = "BOOK";
    public static readonly IReadOnlyList<string> SourceTypes = [SourceGovDoc, SourceWebsite, SourceBook];

    public static string SourceTypeLabel(string? sourceType) => sourceType switch
    {
        SourceGovDoc => "Văn bản nhà nước",
        SourceWebsite => "Website",
        SourceBook => "Sách",
        _ => sourceType ?? string.Empty,
    };

    /// <summary>
    /// Đường dẫn sinh từ tên: bỏ dấu (đ thành d), chữ thường, mọi đoạn ký tự khác chữ và số nối bằng một dấu gạch ngang,
    /// cắt còn tối đa <see cref="SlugMaxLength"/> ký tự. Tên không có chữ hay số nào thì trả chuỗi rỗng.
    /// </summary>
    public static string Slugify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        var pendingSeparator = false;
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var c = ch switch { 'đ' or 'Đ' => 'd', _ => char.ToLowerInvariant(ch) };
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingSeparator && sb.Length > 0) sb.Append('-');
                pendingSeparator = false;
                sb.Append(c);
            }
            else
            {
                pendingSeparator = true;
            }
        }
        var slug = sb.Length > SlugMaxLength ? sb.ToString(0, SlugMaxLength) : sb.ToString();
        return slug.TrimEnd('-');
    }

    /// <summary>Đường dẫn hợp lệ: chữ thường không dấu, chữ số, gạch ngang giữa các đoạn; không bắt đầu hay kết thúc bằng gạch ngang.</summary>
    public static bool IsValidSlug(string? slug) =>
        slug is { Length: >= 1 and <= SlugMaxLength } && SlugPattern().IsMatch(slug);

    /// <summary>Số từ của nội dung; mã đánh dấu Markdown như "##" không tính là từ.</summary>
    public static int CountWords(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return 0;
        var count = 0;
        foreach (var token in content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (token.Any(char.IsLetterOrDigit)) count++;
        return count;
    }

    /// <summary>Tọa độ nhập đủ cả hai hoặc bỏ trống cả hai.</summary>
    public static bool HasBothOrNoneCoordinates(decimal? latitude, decimal? longitude) => latitude.HasValue == longitude.HasValue;

    public static bool IsLatitude(decimal value) => value is >= MinLatitude and <= MaxLatitude;

    public static bool IsLongitude(decimal value) => value is >= MinLongitude and <= MaxLongitude;

    /// <summary>Số ngày của tháng; năm nhuận theo lịch Gregorian, áp dụng cả cho năm âm (trước Công nguyên).</summary>
    public static int DaysInMonth(int year, int month)
    {
        var y = Math.Abs(year);
        var leap = y % 4 == 0 && (y % 100 != 0 || y % 400 == 0);
        return month switch
        {
            2 => leap ? 29 : 28,
            4 or 6 or 9 or 11 => 30,
            _ => 31,
        };
    }

    /// <summary>Ngày hợp lệ theo tháng; chỉ nhập ngày khi đã có tháng.</summary>
    public static bool IsValidDay(int year, int? month, int? day)
    {
        if (day is null) return true;
        if (month is null) return false;
        return month is >= 1 and <= 12 && day >= 1 && day <= DaysInMonth(year, month.Value);
    }

    /// <summary>
    /// Thời điểm kết thúc có trước thời điểm bắt đầu không. Chỉ so tới mức chi tiết mà cả hai cùng có
    /// (kết thúc chỉ có năm thì so năm; cùng năm và cả hai có tháng thì so tháng; cùng tháng và cả hai có ngày thì so ngày).
    /// </summary>
    public static bool EndsBeforeStart(int startYear, int? startMonth, int? startDay, int endYear, int? endMonth, int? endDay)
    {
        if (endYear != startYear) return endYear < startYear;
        if (startMonth is null || endMonth is null) return false;
        if (endMonth != startMonth) return endMonth < startMonth;
        if (startDay is null || endDay is null) return false;
        return endDay < startDay;
    }

    /// <summary>
    /// Giấy phép ảnh được dùng: CC0, CC BY, CC BY-SA (kèm số phiên bản), phạm vi công cộng (public domain) hoặc ảnh nhóm tự chụp.
    /// Các giấy phép hạn chế như CC BY-NC, CC BY-ND không được dùng.
    /// </summary>
    public static bool IsAllowedLicense(string? license)
    {
        var value = license?.Trim();
        return !string.IsNullOrEmpty(value) && AllowedLicense().IsMatch(value);
    }

    /// <summary>Ghi công đủ ba phần "tác giả · giấy phép · nguồn", lưu trong cột <c>media.credit</c>.</summary>
    public static string ComposeCredit(string author, string license, string source) =>
        string.Join(CreditSeparator, author.Trim(), license.Trim(), source.Trim());

    /// <summary>Tách ghi công thành ba phần; dữ liệu cũ không đúng dạng ba phần thì trả null.</summary>
    public static (string Author, string License, string Source)? ParseCredit(string? credit)
    {
        if (string.IsNullOrWhiteSpace(credit)) return null;
        var parts = credit.Split(CreditSeparator);
        return parts.Length == 3 ? (parts[0].Trim(), parts[1].Trim(), parts[2].Trim()) : null;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"^(CC0|CC BY-SA|CC BY)(?![\w-])|^(public domain|phạm vi công cộng|ảnh nhóm tự chụp|nhóm tự chụp)(?![\w-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AllowedLicense();
}
