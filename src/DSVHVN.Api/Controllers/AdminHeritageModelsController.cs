using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Files;
using DSVHVN.Application.Models;
using DSVHVN.Domain.Rules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Biểu mẫu tải mô hình 3D lên và khai báo luôn (multipart/form-data): tệp <c>file</c> cùng tên, ghi công và các trường như khai báo bằng đường dẫn.
/// </summary>
public sealed class UploadHeritageModelForm
{
    /// <summary>Tệp <c>.glb</c> (glTF 2.0 đóng gói một tệp), tối đa 15 MB.</summary>
    public IFormFile? File { get; set; }

    public string? Title { get; set; }

    /// <summary>Ảnh xem trước trong lúc tải mô hình (đường dẫn http, https hoặc ảnh đã tải lên).</summary>
    public string? PosterUrl { get; set; }

    /// <summary>Tác giả (bắt buộc).</summary>
    public string? Author { get; set; }

    /// <summary>Giấy phép mở (bắt buộc): CC0, CC BY, CC BY-SA, phạm vi công cộng hoặc mô hình nhóm tự dựng.</summary>
    public string? License { get; set; }

    public string? LicenseUrl { get; set; }

    /// <summary>Trang nguồn nơi tải mô hình (bắt buộc, trừ mô hình nhóm tự dựng).</summary>
    public string? SourceUrl { get; set; }

    public int? SortOrder { get; set; }

    /// <summary>true để hiện luôn cho khách và học sinh (khi di sản đã xuất bản).</summary>
    public bool? Show { get; set; }

    public HeritageModelRequest ToRequest() => new(Title, FileUrl: null, PosterUrl, Author, License, LicenseUrl, SourceUrl, SortOrder, Show);
}

/// <summary>Biểu mẫu thay tệp của mô hình: chỉ gồm tệp <c>.glb</c>.</summary>
public sealed class ReplaceModelFileForm
{
    /// <summary>Tệp <c>.glb</c> (glTF 2.0 đóng gói một tệp), tối đa 15 MB.</summary>
    public IFormFile? File { get; set; }
}

/// <summary>
/// Tab Mô hình 3D của màn hình Biên tập di sản (quản trị hệ thống): khai báo mô hình bằng tệp tải lên hoặc đường dẫn tệp đã có trong kho, ghi công bắt buộc,
/// xem danh sách, sửa, thay tệp, hiện, ẩn, gỡ. Mỗi di sản chỉ một mô hình đang hiển thị. Mọi thay đổi ghi nhật ký thao tác.
/// </summary>
[Route("api/v1/admin")]
[Authorize(Policy = Policies.Admin)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
public sealed class AdminHeritageModelsController(HeritageModelService models) : ApiControllerBase
{
    // Giới hạn thân yêu cầu rộng hơn tệp một chút để chừa chỗ cho các trường chữ; tệp quá cỡ bị UploadSizeLimit chặn bằng câu rõ ràng.
    private const long ModelRequestLimit = StoredFileRules.MaxModelBytes + UploadSizeLimitAttribute.FormHeadroom;

    /// <summary>Mô hình 3D của di sản (chưa gỡ): đang hiển thị trước. <c>isVisibleToPublic</c> true khi mô hình đang hiển thị và di sản đã xuất bản.</summary>
    [HttpGet("heritages/{heritageId:long}/models")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<HeritageModelDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<HeritageModelDto>>>> List(long heritageId, CancellationToken ct)
        => OkEnvelope(await models.ListAsync(heritageId, ct));

    /// <summary>
    /// Khai báo mô hình bằng đường dẫn tệp <c>.glb</c> đã có trong kho tệp (tải trước ở <c>POST /admin/files/models</c>): trang ngoài kho bị từ chối, dung lượng do máy chủ đo.
    /// Ghi công bắt buộc (tác giả, giấy phép mở, đường dẫn nguồn). <c>show</c> true thì hiện luôn; mặc định lưu nháp. 422 khi di sản đã có mô hình khác đang hiển thị.
    /// </summary>
    [HttpPost("heritages/{heritageId:long}/models")]
    [ProducesResponseType<ApiResponse<HeritageModelDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<HeritageModelDto>>> Create(long heritageId, HeritageModelRequest request, CancellationToken ct)
        => CreatedEnvelope(await models.CreateAsync(CurrentActor, heritageId, request, ct), Messages.Success("Thêm mô hình 3D").Text);

    /// <summary>
    /// Tải tệp <c>.glb</c> lên (glTF 2.0 một tệp, tối đa 15 MB, kiểm bằng nội dung, không tham chiếu tệp ngoài) và khai báo luôn, cùng ghi công như khai báo bằng đường dẫn.
    /// </summary>
    [HttpPost("heritages/{heritageId:long}/models/upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ModelRequestLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = ModelRequestLimit)]
    [UploadSizeLimit("Tệp mô hình 3D", StoredFileRules.MaxModelBytes)]
    [ProducesResponseType<ApiResponse<HeritageModelDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<HeritageModelDto>>> Upload(long heritageId, [FromForm] UploadHeritageModelForm form, CancellationToken ct)
        => CreatedEnvelope(
            await models.UploadAsync(CurrentActor, heritageId, form.ToRequest(), await FormFiles.ReadAsync(form.File, StoredFileRules.MaxModelBytes, ct), ct),
            Messages.Success("Tải mô hình 3D lên").Text);

    /// <summary>Sửa thông tin và ghi công của mô hình (thay toàn bộ). Có <c>fileUrl</c> thì thay tệp bằng tệp khác đã có trong kho; bỏ trống thì giữ tệp.</summary>
    [HttpPut("models/{modelId:long}")]
    [ProducesResponseType<ApiResponse<HeritageModelDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<HeritageModelDto>>> Update(long modelId, UpdateHeritageModelRequest request, CancellationToken ct)
        => OkEnvelope(await models.UpdateAsync(CurrentActor, modelId, request, ct), Messages.Success("Lưu mô hình 3D").Text);

    /// <summary>Thay tệp của mô hình bằng tệp <c>.glb</c> tải lên (cùng quy tắc tệp); thông tin và ghi công giữ nguyên, tệp cũ vẫn nằm trong kho.</summary>
    [HttpPut("models/{modelId:long}/file")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ModelRequestLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = ModelRequestLimit)]
    [UploadSizeLimit("Tệp mô hình 3D", StoredFileRules.MaxModelBytes)]
    [ProducesResponseType<ApiResponse<HeritageModelDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<ApiResponse<HeritageModelDto>>> ReplaceFile(long modelId, [FromForm] ReplaceModelFileForm form, CancellationToken ct)
        => OkEnvelope(
            await models.ReplaceFileAsync(CurrentActor, modelId, await FormFiles.ReadAsync(form.File, StoredFileRules.MaxModelBytes, ct), ct),
            Messages.Success("Thay tệp mô hình 3D").Text);

    /// <summary>Hiện mô hình cho khách và học sinh (từ nháp hoặc đã ẩn). 422 khi di sản đã có mô hình khác đang hiển thị.</summary>
    [HttpPost("models/{modelId:long}/show")]
    [ProducesResponseType<ApiResponse<HeritageModelDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<HeritageModelDto>>> Show(long modelId, CancellationToken ct)
        => OkEnvelope(await models.ShowAsync(CurrentActor, modelId, ct), Messages.Success("Hiện mô hình 3D").Text);

    /// <summary>Ẩn mô hình đang hiển thị; tệp và thông tin vẫn lưu, hiện lại được.</summary>
    [HttpPost("models/{modelId:long}/hide")]
    [ProducesResponseType<ApiResponse<HeritageModelDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<HeritageModelDto>>> Hide(long modelId, CancellationToken ct)
        => OkEnvelope(await models.HideAsync(CurrentActor, modelId, ct), Messages.Success("Ẩn mô hình 3D").Text);

    /// <summary>Gỡ mô hình khỏi di sản (xóa mềm); tệp trong kho được giữ lại.</summary>
    [HttpDelete("models/{modelId:long}")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<object>>> Remove(long modelId, CancellationToken ct)
    {
        await models.RemoveAsync(CurrentActor, modelId, ct);
        return OkEnvelope<object>(null, Messages.Success("Gỡ mô hình 3D").Text);
    }
}

/// <summary>
/// Đọc tệp gửi lên vào bộ nhớ nhưng dừng sau <c>limit + 1</c> byte: tệp quá cỡ vẫn bị dịch vụ từ chối bằng thông báo rõ ràng mà máy chủ không phải nạp cả tệp khổng lồ.
/// </summary>
internal static class FormFiles
{
    public static async Task<UploadedFile?> ReadAsync(IFormFile? file, int limit, CancellationToken ct)
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
