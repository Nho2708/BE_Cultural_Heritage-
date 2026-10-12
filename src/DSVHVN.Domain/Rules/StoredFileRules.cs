using System.Text.RegularExpressions;

namespace DSVHVN.Domain.Rules;

/// <summary>
/// Quy tắc thuần của tệp tải lên kho tệp của nền tảng: dung lượng, kích thước ảnh, tên khóa của tệp và đường dẫn công khai.
/// Tên tệp trong kho luôn do hệ thống sinh (mã ngẫu nhiên 32 ký tự), không bao giờ dùng tên người tải đặt.
/// </summary>
public static partial class StoredFileRules
{
    /// <summary>Ảnh tải lên tối đa 5 MB.</summary>
    public const int MaxImageBytes = 5 * 1024 * 1024;

    /// <summary>Mô hình 3D (.glb) tối đa 15 MB.</summary>
    public const int MaxModelBytes = 15 * 1024 * 1024;

    /// <summary>Chặn ảnh nén rất mạnh nhưng giải ra chiếm nhiều bộ nhớ: tối đa 30 triệu điểm ảnh (khoảng 5500 x 5500).</summary>
    public const long MaxImagePixels = 30_000_000;

    /// <summary>Cạnh dài nhất của ảnh thu nhỏ, tính bằng điểm ảnh.</summary>
    public const int ThumbnailMaxSide = 320;

    /// <summary>Ảnh đại diện tài khoản được cắt vuông và thu nhỏ còn tối đa 512 điểm ảnh mỗi cạnh.</summary>
    public const int AvatarMaxSide = 512;

    /// <summary>Phần đầu của đường dẫn công khai tới tệp trong kho.</summary>
    public const string PublicPathPrefix = "/files/";

    public const string ImageFolder = "images";
    public const string ModelFolder = "models";

    public const string Jpeg = "jpg";
    public const string Png = "png";
    public const string Webp = "webp";
    public const string Glb = "glb";

    /// <summary>Biến đổi của Cloudinary cho ảnh thu nhỏ: giữ tỉ lệ, cạnh dài tối đa 320 điểm ảnh, không phóng to, chất lượng tự chọn.</summary>
    public const string CloudinaryThumbnailTransformation = "c_limit,w_320,q_auto";

    public static readonly IReadOnlyList<string> ImageExtensions = [Jpeg, Png, Webp];

    public static string ImageKey(Guid id, string extension) => $"{ImageFolder}/{id:N}.{extension}";

    public static string ThumbnailKey(Guid id) => $"{ImageFolder}/{id:N}-thumb.{Webp}";

    public static string ModelKey(Guid id) => $"{ModelFolder}/{id:N}.{Glb}";

    /// <summary>Khóa hợp lệ của một tệp trong kho (chỉ các dạng hệ thống sinh ra); dùng để chặn đường dẫn lạ trước khi chạm vào đĩa.</summary>
    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>Đường dẫn tương đối tới tệp trong kho, dạng <c>/files/images/{32 ký tự}.jpg</c>; hợp lệ làm đường dẫn ảnh của di sản.</summary>
    public static bool IsStoredPath(string? url) => url is not null && StoredPathPattern().IsMatch(url.Trim());

    /// <summary>
    /// Đường dẫn ảnh thu nhỏ của một ảnh đã tải lên (cùng nơi, đuôi <c>-thumb.webp</c>; trên Cloudinary là địa chỉ biến đổi <c>c_limit,w_320,q_auto</c>); null nếu đường dẫn không phải ảnh trong kho
    /// (ảnh dán đường dẫn ngoài thì không có ảnh thu nhỏ).
    /// </summary>
    public static string? ThumbnailUrlOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var trimmed = url.Trim();
        var match = StoredImageUrl().Match(trimmed);
        if (match.Success) return $"{match.Groups[1].Value}-thumb.{Webp}";

        // Ảnh trên Cloudinary: ảnh thu nhỏ là địa chỉ biến đổi của chính ảnh gốc (không có tệp riêng).
        var cloud = CloudinaryImageUrl().Match(trimmed);
        return cloud.Success ? $"{cloud.Groups[1].Value}{CloudinaryThumbnailTransformation}/{cloud.Groups[2].Value}.{Webp}" : null;
    }

    [GeneratedRegex(@"^(images/[0-9a-f]{32}(-thumb)?\.(jpg|png|webp)|models/[0-9a-f]{32}\.glb)$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^/files/(images/[0-9a-f]{32}\.(jpg|png|webp)|models/[0-9a-f]{32}\.glb)$")]
    private static partial Regex StoredPathPattern();

    [GeneratedRegex(@"^(https://res\.cloudinary\.com/[A-Za-z0-9_-]+/image/upload/)((?:v\d+/)?(?:[A-Za-z0-9_-]+/)*images/[0-9a-f]{32})\.(?:jpg|png|webp)$")]
    private static partial Regex CloudinaryImageUrl();

    [GeneratedRegex(@"^(.*/files/images/[0-9a-f]{32})\.(jpg|png|webp)$")]
    private static partial Regex StoredImageUrl();
}
