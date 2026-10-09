using DSVHVN.Api.Common;
using DSVHVN.Application.Common;
using DSVHVN.Application.Heritages;
using DSVHVN.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khám phá di sản văn hóa: phục vụ Bản đồ số, Dòng thời gian, bộ lọc vùng miền và trang chi tiết di sản.
/// Mở công khai cho mọi đối tượng: khách tham quan, học sinh, giáo viên.
/// </summary>
[Route("api/v1/heritages")]
public sealed class HeritagesController(HeritageService heritages) : ApiControllerBase
{
    /// <summary>
    /// Danh sách di sản đã xuất bản: lọc theo từ khóa, tỉnh thành, vùng miền, loại di sản, cấp xếp hạng; phân trang.
    /// Dùng cho Bản đồ số và màn hình Khám phá di sản.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<PagedResult<HeritageSummaryDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedResult<HeritageSummaryDto>>>> List(
        [FromQuery] string? keyword,
        [FromQuery] int? provinceId,
        [FromQuery] string? region,
        [FromQuery] HeritageType? heritageType,
        [FromQuery] RecognitionLevel? recognitionLevel,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken ct)
    {
        var query = new HeritageListQuery(keyword, provinceId, region, heritageType, recognitionLevel, page, pageSize);
        var result = await heritages.ListAsync(query, ct);
        return OkEnvelope(result);
    }

    /// <summary>
    /// Danh sách 34 tỉnh/thành kèm số lượng di sản: dùng cho bộ lọc và bản đồ phân bố.
    /// </summary>
    [HttpGet("provinces")]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<List<ProvinceSummaryDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<List<ProvinceSummaryDto>>>> ListProvinces(CancellationToken ct)
    {
        var result = await heritages.ListProvincesAsync(ct);
        return OkEnvelope(result);
    }

    /// <summary>
    /// Chi tiết di sản theo slug (đường dẫn thân thiện): kèm các mốc lịch sử, sự kiện, hình ảnh và nguồn trích dẫn.
    /// </summary>
    [HttpGet("{slug}")]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<HeritageDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<HeritageDetailDto>>> GetBySlug(string slug, CancellationToken ct)
    {
        var result = await heritages.GetBySlugAsync(slug, ct);
        return OkEnvelope(result);
    }

    /// <summary>
    /// Chi tiết di sản theo ID.
    /// </summary>
    [HttpGet("by-id/{id:long}")]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<HeritageDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<HeritageDetailDto>>> GetById(long id, CancellationToken ct)
    {
        var result = await heritages.GetByIdAsync(id, ct);
        return OkEnvelope(result);
    }
}
