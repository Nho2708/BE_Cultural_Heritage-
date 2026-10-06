using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Files;

/// <summary>Nhà cung cấp lưu trữ tệp.</summary>
public enum StorageProvider
{
    /// <summary>Lưu trên đĩa của máy chủ, trong thư mục <c>Storage:Local:RootPath</c>.</summary>
    Local,

    /// <summary>Dịch vụ lưu trữ đám mây: chỗ để sẵn cho sau này, chưa có bản cài đặt; chọn giá trị này thì khởi động báo lỗi rõ ràng.</summary>
    Cloud,
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
}

public sealed class LocalStorageOptions
{
    /// <summary>Thư mục chứa tệp. Đường dẫn tương đối tính từ thư mục chạy của Api. Thư mục này nên nằm ngoài mã nguồn và được sao lưu.</summary>
    public string RootPath { get; set; } = "storage";
}

public sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        var problems = new List<string>();
        if (options.Provider == StorageProvider.Cloud)
            problems.Add("Storage:Provider = Cloud chưa có bản cài đặt, hãy dùng Local");
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
}
