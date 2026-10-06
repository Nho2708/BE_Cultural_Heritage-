namespace DSVHVN.Application.Files;

/// <summary>
/// Kho tệp của nền tảng (ảnh di sản, ảnh sự kiện, mô hình 3D). Giao diện này để đổi nhà cung cấp lưu trữ mà không đổi nghiệp vụ:
/// hai bản cài, chọn bằng <c>Storage:Provider</c>: <c>Local</c> lưu trên đĩa của máy chủ (phát triển, kiểm thử) và <c>Cloudinary</c> (triển khai).
/// Khóa (<c>key</c>) là tên tương đối do hệ thống sinh, ví dụ <c>images/{32 ký tự}.jpg</c>.
/// </summary>
public interface IFileStorage
{
    /// <summary>Ghi tệp. Khóa đã có thì ghi đè (khóa ngẫu nhiên nên không xảy ra trong luồng thường).</summary>
    Task SaveAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);

    /// <summary>Dung lượng (byte) của tệp đã lưu; null nếu không có tệp. Dùng khi khai báo mô hình 3D bằng đường dẫn đã lưu, để máy chủ tự đo thay vì tin số khách gửi.</summary>
    Task<long?> SizeOfAsync(string key, CancellationToken cancellationToken);

    /// <summary>Chỉ để hoàn tác một lần ghi chưa kịp ghi nhận vào CSDL. Tệp của ảnh đã bị gỡ khỏi di sản được giữ lại, không xóa.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>Đường dẫn công khai để trình duyệt mở tệp: tương đối (<c>/files/...</c>) hoặc đủ địa chỉ nếu cấu hình <c>Storage:PublicBaseUrl</c>.</summary>
    string PublicUrl(string key);
}

/// <summary>
/// Kho tệp ở xa (Cloudinary) không ghi hoặc không xóa được. Api đổi thành 503 kèm thông báo cho tải lại; người gọi (<see cref="IFileStorage"/>) không đưa
/// thông tin kết nối, khóa hay bí mật vào thông điệp.
/// </summary>
public sealed class FileStorageException(string message, Exception? inner = null) : Exception(message, inner);

public enum ImageProblem
{
    None,

    /// <summary>Nội dung không phải ảnh, hoặc ảnh hỏng không đọc được.</summary>
    Unreadable,

    /// <summary>Là ảnh nhưng không phải JPG, PNG hoặc WEBP (xét theo nội dung, không theo đuôi tệp).</summary>
    UnsupportedFormat,

    /// <summary>Số điểm ảnh vượt giới hạn.</summary>
    TooLarge,
}

/// <summary>Ảnh đã xử lý: bản lưu (đã bỏ dữ liệu EXIF, xoay đúng chiều) cùng đuôi theo định dạng thật, và ảnh thu nhỏ WEBP.</summary>
public sealed record ProcessedImage(string Extension, byte[] Content, byte[] Thumbnail, int Width, int Height);

public sealed record ImageProcessResult(ProcessedImage? Image, ImageProblem Problem)
{
    public static ImageProcessResult Success(ProcessedImage image) => new(image, ImageProblem.None);

    public static ImageProcessResult Failure(ImageProblem problem) => new(null, problem);
}

/// <summary>
/// Kiểm và xử lý ảnh tải lên: xác định định dạng thật bằng nội dung (không tin đuôi tệp hay loại nội dung khai báo), chỉ nhận JPG, PNG, WEBP,
/// giải mã lại rồi mã hóa lại để bỏ hết dữ liệu EXIF (vị trí GPS, thiết bị...), xoay đúng chiều theo EXIF, và sinh ảnh thu nhỏ.
/// </summary>
public interface IImageProcessor
{
    ImageProcessResult Process(byte[] content);
}

/// <summary>Tệp người dùng gửi lên: tên gốc (chỉ để ghi log, không dùng làm tên lưu) và nội dung đã đọc vào bộ nhớ.</summary>
public sealed record UploadedFile(string? FileName, byte[] Content);

/// <summary>Tệp đã lưu vào kho: đường dẫn công khai, ảnh thu nhỏ (nếu là ảnh) và dung lượng.</summary>
public sealed record StoredFileDto(string Url, string? PreviewUrl, long Size, string ContentType);
