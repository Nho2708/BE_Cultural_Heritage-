using System.Text.RegularExpressions;

namespace DSVHVN.Domain.Rules;

/// <summary>
/// Quy tắc thuần của mô hình 3D di sản: giấy phép mở được nhận, độ dài các trường, dòng ghi công, và nhận ra khóa của tệp <c>.glb</c> trong kho tệp
/// từ một đường dẫn đã lưu. Kiểu tệp (<c>.glb</c>, glTF 2.0 một tệp) và dung lượng tối đa nằm ở <see cref="StoredFileRules"/>.
/// </summary>
public static partial class HeritageModelRules
{
    public const int TitleMaxLength = 255;
    public const int UrlMaxLength = 500;
    public const int AuthorMaxLength = 255;
    public const int LicenseMaxLength = 100;
    public const int CreditMaxLength = 500;

    /// <summary>Phần nguồn trong dòng ghi công của mô hình nhóm tự dựng không có trang nguồn.</summary>
    public const string TeamSource = "Nhóm thực hiện";

    /// <summary>
    /// Giấy phép mở được nhận cho mô hình: CC0, CC BY, CC BY-SA (kèm số phiên bản), phạm vi công cộng (public domain) hoặc mô hình nhóm tự dựng.
    /// Không nhận CC BY-NC, CC BY-ND hay giấy phép không rõ.
    /// </summary>
    public static bool IsAllowedLicense(string? license)
    {
        var value = license?.Trim();
        return !string.IsNullOrEmpty(value) && AllowedLicense().IsMatch(value);
    }

    /// <summary>Mô hình do nhóm tự dựng (không cần trang nguồn): giấy phép ghi "mô hình nhóm tự dựng" hoặc "nhóm tự dựng".</summary>
    public static bool IsTeamBuilt(string? license)
    {
        var value = license?.Trim();
        return !string.IsNullOrEmpty(value) && TeamBuiltLicense().IsMatch(value);
    }

    /// <summary>Ghi công đủ ba phần "tác giả · giấy phép · nguồn"; không có trang nguồn (mô hình nhóm tự dựng) thì phần nguồn là "Nhóm thực hiện".</summary>
    public static string ComposeCredit(string author, string license, string? sourceUrl) =>
        HeritageRules.ComposeCredit(author, license, string.IsNullOrWhiteSpace(sourceUrl) ? TeamSource : sourceUrl);

    /// <summary>
    /// Khóa trong kho của tệp mô hình mà <paramref name="url"/> trỏ tới (<c>models/{32 ký tự}.glb</c>), hoặc null nếu đường dẫn không có dạng đó.
    /// Chỉ nhận đúng hai dạng kho sinh ra: <c>/files/models/{mã}.glb</c> (có thể kèm địa chỉ máy chủ) và địa chỉ của Cloudinary
    /// <c>https://res.cloudinary.com/{tên}/raw/upload/{thư mục}/models/{mã}.glb</c>; trang ngoài kho thì không bao giờ khớp.
    /// </summary>
    public static string? StoredModelKey(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var value = url.Trim();
        var local = StoredModelPath().Match(value);
        if (local.Success) return local.Groups["key"].Value;
        var cloud = CloudinaryModelUrl().Match(value);
        return cloud.Success ? cloud.Groups["key"].Value : null;
    }

    [GeneratedRegex(@"^(CC0|CC BY-SA|CC BY)(?![\w-])|^(public domain|phạm vi công cộng|mô hình nhóm tự dựng|nhóm tự dựng)(?![\w-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AllowedLicense();

    [GeneratedRegex(@"^(mô hình nhóm tự dựng|nhóm tự dựng)(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TeamBuiltLicense();

    [GeneratedRegex(@"^(?:https?://[^/\s]+)?/files/(?<key>models/[0-9a-f]{32}\.glb)$")]
    private static partial Regex StoredModelPath();

    [GeneratedRegex(@"^https://res\.cloudinary\.com/[A-Za-z0-9_-]+/raw/upload/(?:v\d+/)?(?:[A-Za-z0-9_-]+/)*(?<key>models/[0-9a-f]{32}\.glb)$")]
    private static partial Regex CloudinaryModelUrl();
}
