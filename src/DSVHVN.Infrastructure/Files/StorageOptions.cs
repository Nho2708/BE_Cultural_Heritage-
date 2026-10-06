using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Files;

/// <summary>Nhà cung cấp lưu trữ tệp.</summary>
public enum StorageProvider
{
    /// <summary>Lưu trên đĩa của máy chủ, trong thư mục <c>Storage:Local:RootPath</c>.</summary>
    Local,

    /// <summary>Cloudinary (dùng khi triển khai): ảnh và mô hình 3D nằm trên Cloudinary, địa chỉ lưu và hiển thị là URL HTTPS. Cần <c>Storage:Cloudinary</c>.</summary>
    Cloudinary,
}

/// <summary>Cấu hình mục <c>Storage</c>: nơi lưu ảnh và mô hình 3D tải lên.</summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    public StorageProvider Provider { get; set; } = StorageProvider.Local;

    /// <summary>
    /// Địa chỉ gốc để dựng đường dẫn đủ tới tệp (vd <c>https://api.example.vn</c>), dùng khi web học tập và app ở địa chỉ khác máy chủ API.
    /// Để trống thì đường dẫn tệp là tương đối (<c>/files/images/...</c>), phía giao diện ghép với địa chỉ API.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    public LocalStorageOptions Local { get; set; } = new();

    public CloudinaryStorageOptions Cloudinary { get; set; } = new();
}

/// <summary>
/// Cấu hình Cloudinary. Tên đám mây, khóa API và bí mật API chỉ đặt ở User Secrets hoặc biến môi trường
/// (<c>Storage__Cloudinary__CloudName</c>, <c>Storage__Cloudinary__ApiKey</c>, <c>Storage__Cloudinary__ApiSecret</c>), không bao giờ ghi vào tệp cấu hình đưa lên kho mã.
/// </summary>
public sealed class CloudinaryStorageOptions
{
    /// <summary>Thư mục (tiền tố mã công khai) chứa mọi tệp của nền tảng trên Cloudinary.</summary>
    public const string DefaultFolder = "dsvhvn";

    public string CloudName { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>Thư mục trên Cloudinary; mặc định <c>dsvhvn</c>. Lần thử thật dùng <c>dsvhvn-thu</c> để không lẫn với ảnh thật.</summary>
    public string Folder { get; set; } = DefaultFolder;
}

public sealed class LocalStorageOptions
{
    /// <summary>Thư mục chứa tệp. Đường dẫn tương đối tính từ thư mục chạy của Api. Thư mục này nên nằm ngoài mã nguồn và được sao lưu.</summary>
    public string RootPath { get; set; } = "storage";
}

public sealed partial class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        var problems = new List<string>();
        if (!Enum.IsDefined(options.Provider))
            problems.Add("Storage:Provider chỉ nhận Local hoặc Cloudinary");
        else if (options.Provider == StorageProvider.Cloudinary)
            ValidateCloudinary(options.Cloudinary, problems);
        else if (string.IsNullOrWhiteSpace(options.Local.RootPath))
            problems.Add("thiếu Storage:Local:RootPath (thư mục chứa tệp tải lên)");

        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl)
            && !(Uri.TryCreate(options.PublicBaseUrl.Trim(), UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
            problems.Add("Storage:PublicBaseUrl phải là địa chỉ http hoặc https đầy đủ");

        return problems.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Cấu hình lưu trữ tệp chưa đúng: " + string.Join("; ", problems) + ".");
    }

    // Chỉ nêu tên khóa cấu hình còn thiếu, không bao giờ in giá trị của khóa.
    private static void ValidateCloudinary(CloudinaryStorageOptions cloudinary, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(cloudinary.CloudName)) problems.Add("thiếu Storage:Cloudinary:CloudName");
        if (string.IsNullOrWhiteSpace(cloudinary.ApiKey)) problems.Add("thiếu Storage:Cloudinary:ApiKey");
        if (string.IsNullOrWhiteSpace(cloudinary.ApiSecret)) problems.Add("thiếu Storage:Cloudinary:ApiSecret");
        if (!string.IsNullOrWhiteSpace(cloudinary.CloudName) && !CloudNamePattern().IsMatch(cloudinary.CloudName.Trim()))
            problems.Add("Storage:Cloudinary:CloudName chỉ gồm chữ, số, gạch ngang và gạch dưới");
        if (!string.IsNullOrWhiteSpace(cloudinary.Folder) && !FolderPattern().IsMatch(cloudinary.Folder.Trim()))
            problems.Add("Storage:Cloudinary:Folder chỉ gồm chữ, số, gạch ngang, gạch dưới và dấu gạch chéo giữa các cấp");
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex CloudNamePattern();

    [GeneratedRegex("^[A-Za-z0-9_-]+(/[A-Za-z0-9_-]+)*$")]
    private static partial Regex FolderPattern();
}
