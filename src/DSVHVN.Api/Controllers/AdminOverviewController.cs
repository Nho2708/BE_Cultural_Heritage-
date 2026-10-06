using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Overview;
using DSVHVN.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khu Quản trị hệ thống: tổng quan toàn nền tảng, biểu đồ theo thời gian và doanh thu từ giao dịch VNPay thành công kèm danh sách giao dịch.
/// Chỉ đọc, chỉ ADMIN; giáo viên, quản trị trường, học sinh nhận 403.
/// </summary>
[Route("api/v1/admin/overview")]
[Authorize(Policy = Policies.Admin)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class AdminOverviewController(SystemOverviewService overview) : ApiControllerBase
{
    /// <summary>
    /// Số trường (có và không có gói hiệu lực), giáo viên, học sinh đang hoạt động, nội dung (di sản, bài học, bài kiểm tra, lượt nộp, ngân hàng câu hỏi),
    /// lượt AI toàn hệ thống, số trường theo từng gói; doanh thu tổng, tháng này, hôm nay và số giao dịch thành công.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<SystemOverviewDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<SystemOverviewDto>>> Get(CancellationToken ct)
        => OkEnvelope(await overview.GetAsync(CurrentActor, ct));

    /// <summary>
    /// Số liệu theo thời gian trong khoảng <c>from</c> đến <c>to</c> (ngày Việt Nam <c>yyyy-MM-dd</c>, mặc định 30 ngày gần nhất, tối đa 366 ngày):
    /// trường, giáo viên, học sinh tạo mới, bài học tạo mới, bài kiểm tra phát hành, lượt nộp, lượt AI, doanh thu. <c>granularity</c> = DAY hoặc MONTH;
    /// bỏ trống thì khoảng tới 92 ngày gom theo ngày, dài hơn gom theo tháng.
    /// </summary>
    [HttpGet("trend")]
    [ProducesResponseType<ApiResponse<SystemTrendDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<SystemTrendDto>>> Trend(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] TrendGranularity? granularity, CancellationToken ct)
        => OkEnvelope(await overview.TrendAsync(CurrentActor, from, to, granularity, ct));

    /// <summary>
    /// Danh sách giao dịch thanh toán của mọi trường (tab Doanh thu), mới nhất trước. Lọc <c>status</c> (PENDING, SUCCEEDED, FAILED, REFUNDED),
    /// <c>organizationId</c>, <c>from</c> và <c>to</c> (ngày tạo giao dịch theo giờ Việt Nam, <c>yyyy-MM-dd</c>), <c>page</c>, <c>pageSize</c>.
    /// </summary>
    [HttpGet("payments")]
    [ProducesResponseType<ApiResponse<PagedResult<SystemPaymentDto>>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<PagedResult<SystemPaymentDto>>>> Payments(
        [FromQuery] PaymentStatus? status, [FromQuery] long? organizationId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => OkEnvelope(await overview.ListPaymentsAsync(CurrentActor, new SystemPaymentListQuery(status, organizationId, from, to, page, pageSize), ct));
}
