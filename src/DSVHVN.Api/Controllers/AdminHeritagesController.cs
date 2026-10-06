using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Discovery;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>Danh mục 34 tỉnh/thành cho bộ lọc và ô chọn tỉnh của màn hình Quản lý di sản, Biên tập di sản. Chỉ đọc.</summary>
[Route("api/v1/admin/provinces")]
[Authorize(Policy = Policies.Admin)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class AdminProvincesController(HeritageService heritages) : ApiControllerBase
{
    /// <summary>Toàn bộ tỉnh/thành theo mã, kèm vùng miền (Bắc, Trung, Nam).</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<IReadOnlyList<ProvinceDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ProvinceDto>>>> List(CancellationToken ct)
        => OkEnvelope(await heritages.ListProvincesAsync(ct));
}

/// <summary>Quản lý di sản phía quản trị hệ thống: màn hình Quản lý di sản và tab Thông tin chung của Biên tập di sản.</summary>
[Route("api/v1/admin/heritages")]
[Authorize(Policy = Policies.Admin)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class AdminHeritagesController(HeritageService heritages, HeritagePreviewService previews) : ApiControllerBase
{
    /// <summary>
    /// Danh sách di sản chưa xóa: tìm theo tên (không phân biệt hoa thường và dấu), lọc tỉnh, loại, cấp xếp hạng, trạng thái
    /// (DRAFT | PUBLISHED | ARCHIVED), tình trạng bản dịch (NONE | PENDING | APPROVED | OUTDATED), phân trang; di sản sửa gần nhất đứng trước.
    /// Mỗi dòng có <c>translationState</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<PagedResult<HeritageSummaryDto>>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<PagedResult<HeritageSummaryDto>>>> List(
        [FromQuery] string? keyword, [FromQuery] int? provinceId, [FromQuery] HeritageType? heritageType,
        [FromQuery] RecognitionLevel? recognitionLevel, [FromQuery] ContentStatus? status,
        [FromQuery] int? page, [FromQuery] int? pageSize, [FromQuery] TranslationState? translationState, CancellationToken ct)
        => OkEnvelope(await heritages.ListAsync(
            new HeritageListQuery(keyword, provinceId, heritageType, recognitionLevel, status, page, pageSize, translationState), ct));

    /// <summary>Đường dẫn gợi ý từ tên di sản (thêm -2, -3... nếu đã có di sản dùng); <c>heritageId</c> để loại chính di sản đang sửa.</summary>
    [HttpGet("suggest-slug")]
    [ProducesResponseType<ApiResponse<string>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<string>>> SuggestSlug([FromQuery] string? name, [FromQuery] long? heritageId, CancellationToken ct)
        => OkEnvelope(await heritages.SuggestSlugAsync(name, heritageId, ct));

    /// <summary>Tạo di sản trạng thái DRAFT (chỉ tên bắt buộc). Đường dẫn bỏ trống thì sinh từ tên.</summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<HeritageDetailDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<HeritageDetailDto>>> Create(HeritageRequest request, CancellationToken ct)
        => CreatedEnvelope(await heritages.CreateAsync(request, ct), Messages.Success("Tạo di sản").Text);

    /// <summary>Chi tiết đầy đủ để biên tập: thông tin chung, timeline kèm sự kiện, thư viện ảnh, nguồn, điều kiện xuất bản còn thiếu.</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType<ApiResponse<HeritageDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<HeritageDetailDto>>> Get(long id, CancellationToken ct)
        => OkEnvelope(await heritages.GetAsync(id, ct));

    /// <summary>Sửa thông tin chung. Di sản đang xuất bản sửa xong phải còn đủ điều kiện, không thì 422.</summary>
    [HttpPut("{id:long}")]
    [ProducesResponseType<ApiResponse<HeritageDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<HeritageDetailDto>>> Update(long id, HeritageRequest request, CancellationToken ct)
        => OkEnvelope(await heritages.UpdateAsync(id, request, ct), Messages.Success("Lưu di sản").Text);

    /// <summary>Xóa mềm di sản DRAFT hoặc ARCHIVED chưa gắn bài học, câu hỏi; ngược lại 422 (chỉ được ẩn).</summary>
    [HttpDelete("{id:long}")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(long id, CancellationToken ct)
    {
        await heritages.DeleteAsync(id, ct);
        return OkEnvelope<object>(null, Messages.Success("Xóa di sản").Text);
    }

    /// <summary>Chọn ảnh đại diện trong thư viện ảnh của di sản (<c>mediaId</c> là ảnh IMAGE của di sản); null để bỏ chọn khi chưa xuất bản.</summary>
    [HttpPut("{id:long}/thumbnail")]
    [ProducesResponseType<ApiResponse<HeritageDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<HeritageDetailDto>>> SetThumbnail(long id, ThumbnailRequest request, CancellationToken ct)
        => OkEnvelope(await heritages.SetThumbnailAsync(id, request, ct), Messages.Success("Chọn ảnh đại diện").Text);

    /// <summary>
    /// Cấp liên kết xem trước bản nháp (nút "Xem trước" của Biên tập di sản): mã ký có hạn 30 phút (cấu hình <c>Preview:CodeMinutes</c>), gắn đúng di sản này.
    /// Trả <c>path</c> dạng <c>/xem-truoc/di-san/{mã}</c> để web học tập mở ở tab mới; bấm lại thì cấp mã mới. Di sản đã xuất bản thì 422 (mở trang công khai).
    /// Ghi nhật ký HERITAGE_PREVIEW_ISSUED. Không đổi gì ở di sản.
    /// </summary>
    [HttpPost("{id:long}/preview")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<HeritagePreviewLinkDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<HeritagePreviewLinkDto>>> IssuePreview(long id, CancellationToken ct)
        => OkEnvelope(await previews.IssueAsync(CurrentActor, id, ct));

    /// <summary>Xuất bản (DRAFT sang PUBLISHED) khi đủ điều kiện; thiếu thì 422 NOT_READY kèm danh sách còn thiếu. Ghi nhật ký HERITAGE_PUBLISHED.</summary>
    [HttpPost("{id:long}/publish")]
    [ProducesResponseType<ApiResponse<HeritageDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<HeritageDetailDto>>> Publish(long id, CancellationToken ct)
        => OkEnvelope(await heritages.PublishAsync(id, CurrentActor, ct), Messages.Success("Xuất bản di sản").Text);

    /// <summary>Ẩn (PUBLISHED sang ARCHIVED): rời mọi trang công khai. Ghi nhật ký HERITAGE_ARCHIVED.</summary>
    [HttpPost("{id:long}/archive")]
    [ProducesResponseType<ApiResponse<HeritageDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<HeritageDetailDto>>> Archive(long id, CancellationToken ct)
        => OkEnvelope(await heritages.ArchiveAsync(id, CurrentActor, ct), Messages.Success("Ẩn di sản").Text);

    /// <summary>Hiện lại (ARCHIVED sang PUBLISHED), kiểm lại điều kiện như xuất bản. Ghi nhật ký HERITAGE_PUBLISHED.</summary>
    [HttpPost("{id:long}/restore")]
    [ProducesResponseType<ApiResponse<HeritageDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<HeritageDetailDto>>> Restore(long id, CancellationToken ct)
        => OkEnvelope(await heritages.RestoreAsync(id, CurrentActor, ct), Messages.Success("Hiện lại di sản").Text);
}
