using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Màn hình Gói dịch vụ của quản trị hệ thống: bảng gói kèm số trường đang dùng; tạo, sửa trong khung bên phải; bật, tắt bán.
/// Gói Miễn phí (gói giá 0 duy nhất) không tắt, không đổi tên, không đổi giá; không có thao tác xóa gói. Mọi thay đổi ghi nhật ký thao tác.
/// </summary>
[Route("api/v1/admin/plans")]
[Authorize(Policy = Policies.Admin)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class AdminPlansController(PlanService plans) : ApiControllerBase
{
    /// <summary>Mọi gói (cả gói đã tắt bán), giá tăng dần.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<IReadOnlyList<PlanDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PlanDto>>>> List(CancellationToken ct)
        => OkEnvelope(await plans.ListAsync(ct));

    [HttpGet("{id:int}")]
    [ProducesResponseType<ApiResponse<PlanDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<PlanDto>>> Get(int id, CancellationToken ct)
        => OkEnvelope(await plans.GetAsync(id, ct));

    /// <summary>Tạo gói trả phí (giá nguyên đồng lớn hơn 0), đang bán. Tên trùng thì 409.</summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<PlanDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<PlanDto>>> Create(PlanRequest request, CancellationToken ct)
        => CreatedEnvelope(await plans.CreateAsync(request, CurrentActor, ct), Messages.Success("Tạo gói").Text);

    /// <summary>Sửa tên, giá, thời hạn, số giáo viên tối đa, số lượt AI mỗi kỳ. Giá mới chỉ áp cho hóa đơn tạo sau đó.</summary>
    [HttpPut("{id:int}")]
    [ProducesResponseType<ApiResponse<PlanDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<PlanDto>>> Update(int id, PlanRequest request, CancellationToken ct)
        => OkEnvelope(await plans.UpdateAsync(id, request, CurrentActor, ct), Messages.Success("Lưu gói").Text);

    /// <summary>Bật bán lại.</summary>
    [HttpPost("{id:int}/enable")]
    [ProducesResponseType<ApiResponse<PlanDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<PlanDto>>> Enable(int id, CancellationToken ct)
        => OkEnvelope(await plans.EnableAsync(id, CurrentActor, ct), Messages.Success("Bật bán gói").Text);

    /// <summary>Tắt bán: không bán mới và không gia hạn, đăng ký đang có dùng tới hết hạn. Gói Miễn phí thì 422.</summary>
    [HttpPost("{id:int}/disable")]
    [ProducesResponseType<ApiResponse<PlanDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<PlanDto>>> Disable(int id, CancellationToken ct)
        => OkEnvelope(await plans.DisableAsync(id, CurrentActor, ct), Messages.Success("Tắt bán gói").Text);
}

/// <summary>Hóa đơn và giao dịch của mọi trường (chỉ đọc), cho quản trị hệ thống đối soát; hóa đơn là biên nhận nội bộ, không sửa, không xóa.</summary>
[Route("api/v1/admin/invoices")]
[Authorize(Policy = Policies.Admin)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class AdminInvoicesController(BillingQueries queries) : ApiControllerBase
{
    /// <summary>Hóa đơn mới nhất trước; lọc theo trường, trạng thái, khoảng ngày xuất.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<PagedResult<InvoiceDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedResult<InvoiceDto>>>> List(
        [FromQuery] InvoiceStatus? status, [FromQuery] long? organizationId, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => OkEnvelope(await queries.ListInvoicesAsync(null, new InvoiceListQuery(status, organizationId, from, to, page, pageSize), ct));

    /// <summary>Một hóa đơn kèm các giao dịch.</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType<ApiResponse<InvoiceDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<InvoiceDetailDto>>> Get(long id, CancellationToken ct)
        => OkEnvelope(await queries.GetInvoiceAsync(null, id, ct));
}
