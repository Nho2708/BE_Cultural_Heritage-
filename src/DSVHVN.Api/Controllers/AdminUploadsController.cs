using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Files;
using DSVHVN.Application.Heritages;
using DSVHVN.Domain.Rules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>Biểu mẫu tải ảnh lên (multipart/form-data): tệp <c>file</c> cùng ghi công và thông tin như thêm ảnh bằng đường dẫn.</summary>
public sealed class UploadMediaForm
{
    /// <summary>Tệp ảnh JPG, PNG hoặc WEBP, tối đa 5 MB (định dạng xét theo nội dung tệp, không theo đuôi).</summary>
    public IFormFile? File { get; set; }

    public string? Caption { get; set; }

    /// <summary>Tác giả (bắt buộc).</summary>
    public string? Author { get; set; }

    /// <summary>Giấy phép: CC0, CC BY, CC BY-SA, phạm vi công cộng hoặc ảnh nhóm tự chụp (bắt buộc).</summary>
    public string? License { get; set; }

    /// <summary>Nguồn ảnh (bắt buộc).</summary>
    public string? Source { get; set; }

    public int? SortOrder { get; set; }

    /// <summary>Chọn luôn làm ảnh đại diện (chỉ ảnh của di sản).</summary>
    public bool? SetAsThumbnail { get; set; }

    public UploadMediaRequest ToRequest() => new(Caption, Author, License, Source, SortOrder, SetAsThumbnail);
}

/// <summary>Biểu mẫu tải mô hình 3D lên.</summary>
public sealed class UploadModelForm
{
    /// <summary>Tệp <c>.glb</c> (glTF 2.0 đóng gói một tệp), tối đa 15 MB.</summary>
    public IFormFile? File { get; set; }
}

/// <summary>
/// Tải tệp lên kho tệp của nền tảng (quản trị hệ thống): ảnh cho di sản và sự kiện, mô hình 3D. Tệp được kiểm bằng nội dung,
/// ảnh bỏ EXIF và có ảnh thu nhỏ, tên lưu là mã ngẫu nhiên. Đường dẫn trả về dạng <c>/files/...</c> (hoặc đủ địa chỉ nếu cấu hình
/// <c>Storage:PublicBaseUrl</c>) mở được trực tiếp, không cần đăng nhập.
/// </summary>
[Route("api/v1/admin")]
[Authorize(Policy = Policies.Admin)]
[Consumes("multipart/form-data")]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status413PayloadTooLarge)]
public sealed class AdminUploadsController(HeritageMediaService media, ModelFileService models) : ApiControllerBase
{
    // Giới hạn thân yêu cầu rộng hơn tệp một chút để chừa chỗ cho các trường văn bản; dung lượng tệp do dịch vụ kiểm lại.
    private const long ImageRequestLimit = StoredFileRules.MaxImageBytes + 512 * 1024;
    private const long ModelRequestLimit = StoredFileRules.MaxModelBytes + 512 * 1024;

    /// <summary>
    /// Tải ảnh lên thư viện của di sản. Ghi công bắt buộc (<c>author</c>, <c>license</c>, <c>source</c>); <c>setAsThumbnail</c> true để chọn luôn làm ảnh đại diện.
    /// Phản hồi có <c>url</c> (ảnh gốc đã xử lý) và <c>previewUrl</c> (ảnh thu nhỏ).
    /// </summary>
    [HttpPost("heritages/{heritageId:long}/media/upload")]
    [RequestSizeLimit(ImageRequestLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = ImageRequestLimit)]
    [ProducesResponseType<ApiResponse<MediaDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MediaDto>>> UploadHeritageImage(
        long heritageId, [FromForm] UploadMediaForm form, CancellationToken ct)
        => CreatedEnvelope(
            await media.UploadToHeritageAsync(heritageId, form.ToRequest(), await ReadAsync(form.File, StoredFileRules.MaxImageBytes, ct), ct),
            Messages.Success("Tải ảnh lên").Text);

    /// <summary>Tải ảnh minh họa lên cho sự kiện (cùng quy tắc; không chọn làm ảnh đại diện được).</summary>
    [HttpPost("events/{eventId:long}/media/upload")]
    [RequestSizeLimit(ImageRequestLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = ImageRequestLimit)]
    [ProducesResponseType<ApiResponse<MediaDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MediaDto>>> UploadEventImage(
        long eventId, [FromForm] UploadMediaForm form, CancellationToken ct)
        => CreatedEnvelope(
            await media.UploadToEventAsync(eventId, form.ToRequest(), await ReadAsync(form.File, StoredFileRules.MaxImageBytes, ct), ct),
            Messages.Success("Tải ảnh lên").Text);

    /// <summary>
    /// Tải mô hình 3D (<c>.glb</c>, glTF 2.0 đóng gói một tệp, tối đa 15 MB, mọi dữ liệu nằm trong tệp) lên kho tệp; trả đường dẫn trong kho.
    /// Việc gắn mô hình vào di sản kèm ghi công thuộc luồng Xem 3D.
    /// </summary>
    [HttpPost("files/models")]
    [RequestSizeLimit(ModelRequestLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = ModelRequestLimit)]
    [ProducesResponseType<ApiResponse<StoredFileDto>>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ApiResponse<StoredFileDto>>> UploadModel([FromForm] UploadModelForm form, CancellationToken ct)
        => CreatedEnvelope(await models.UploadAsync(await ReadAsync(form.File, StoredFileRules.MaxModelBytes, ct), ct),
            Messages.Success("Tải mô hình 3D lên").Text);

    /// <summary>
    /// Đọc tệp vào bộ nhớ nhưng dừng sau <paramref name="limit"/> + 1 byte: tệp quá cỡ vẫn bị dịch vụ từ chối bằng thông báo rõ ràng
    /// mà máy chủ không phải nạp cả tệp khổng lồ.
    /// </summary>
    private static async Task<UploadedFile?> ReadAsync(IFormFile? file, int limit, CancellationToken ct)
    {
        if (file is null) return null;
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (buffer.Length <= limit && (read = await stream.ReadAsync(chunk, ct)) > 0)
            buffer.Write(chunk, 0, Math.Min(read, (int)(limit + 1 - buffer.Length)));
        return new UploadedFile(file.FileName, buffer.ToArray());
    }
}
