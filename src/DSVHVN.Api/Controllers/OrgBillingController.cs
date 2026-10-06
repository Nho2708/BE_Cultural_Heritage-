using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Gói và thanh toán của quản trị trường (tab Gói và hạn mức, tab Hóa đơn và giao dịch, màn hình Chọn gói và thanh toán, trang Kết quả thanh toán).
/// Trường luôn là trường của người gọi; hóa đơn, đăng ký, giao dịch của trường khác trả 404. Không có endpoint nào kích hoạt gói trả phí:
/// gói chỉ ACTIVE khi VNPay gửi IPN hợp lệ về <c>/api/v1/payments/vnpay/ipn</c>.
/// </summary>
[Route("api/v1/org")]
[Authorize(Policy = Policies.OrgAdmin)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class OrgBillingController(BillingQueries queries, BillingService billing) : ApiControllerBase
{
    /// <summary>Gói hiện tại, gói gia hạn chờ bắt đầu, yêu cầu mua chưa thanh toán, số giáo viên và lượt AI, và các nút nào được bấm.</summary>
    [HttpGet("billing")]
    [ProducesResponseType<ApiResponse<BillingOverviewDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<BillingOverviewDto>>> Overview(CancellationToken ct)
        => OkEnvelope(await queries.GetOverviewAsync(CurrentActor, ct));

    /// <summary>Các gói đang bán (không gồm gói Miễn phí), kèm loại thao tác (mua, gia hạn, nâng cấp) và có mua được không.</summary>
    [HttpGet("plans")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<PlanOfferDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PlanOfferDto>>>> Plans(CancellationToken ct)
        => OkEnvelope(await queries.ListOffersAsync(CurrentActor, ct));

    /// <summary>Khối xác nhận sau khi chọn gói: loại, số tiền, ngày hiệu lực dự kiến, câu xác nhận, cảnh báo vượt hạn mức giáo viên. Gói không mua được thì 422.</summary>
    [HttpGet("plans/{planId:int}/quote")]
    [ProducesResponseType<ApiResponse<PurchaseQuoteDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<PurchaseQuoteDto>>> Quote(int planId, CancellationToken ct)
        => OkEnvelope(await queries.GetQuoteAsync(CurrentActor, planId, ct));

    /// <summary>
    /// "Thanh toán qua VNPay": tạo đăng ký chờ thanh toán, hóa đơn chưa trả và giao dịch chờ, trả địa chỉ VNPay sandbox để chuyển trình duyệt tới.
    /// Trường đã có yêu cầu mua chưa trả thì 422. Chưa cấu hình khóa VNPay thì 503.
    /// </summary>
    [HttpPost("billing/checkout")]
    [ProducesResponseType<ApiResponse<PaymentStartDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<PaymentStartDto>>> Checkout(CheckoutRequest request, CancellationToken ct)
        => CreatedEnvelope(await billing.StartCheckoutAsync(CurrentActor, request, ct));

    /// <summary>"Kích hoạt gói Miễn phí": ngay, không qua VNPay, không hóa đơn; chỉ khi trường không có gói hiệu lực.</summary>
    [HttpPost("billing/free-plan")]
    [ProducesResponseType<ApiResponse<BillingOverviewDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<BillingOverviewDto>>> ActivateFreePlan(CancellationToken ct)
        => OkEnvelope(await billing.ActivateFreePlanAsync(CurrentActor, ct), Messages.Success("Kích hoạt gói Miễn phí").Text);

    /// <summary>
    /// "Hủy gói": hủy gói trả phí đang hiệu lực (kết thúc ngay, không hoàn tiền) hoặc hủy yêu cầu mua chưa thanh toán (hóa đơn bị hủy).
    /// Gói Miễn phí và gói gia hạn đã thanh toán không hủy được.
    /// </summary>
    [HttpPost("subscriptions/{id:long}/cancel")]
    [ProducesResponseType<ApiResponse<BillingOverviewDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<BillingOverviewDto>>> Cancel(long id, CancellationToken ct)
        => OkEnvelope(await billing.CancelAsync(CurrentActor, id, ct), Messages.Success("Hủy").Text);

    /// <summary>Hóa đơn của trường, mới nhất trước; lọc theo trạng thái và khoảng ngày xuất.</summary>
    [HttpGet("invoices")]
    [ProducesResponseType<ApiResponse<PagedResult<InvoiceDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedResult<InvoiceDto>>>> Invoices(
        [FromQuery] InvoiceStatus? status, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => OkEnvelope(await queries.ListInvoicesAsync(CurrentActor.RequireOrganizationId(),
            new InvoiceListQuery(status, null, from, to, page, pageSize), ct));

    /// <summary>Một hóa đơn kèm các giao dịch (mở dòng trong tab Hóa đơn và giao dịch).</summary>
    [HttpGet("invoices/{id:long}")]
    [ProducesResponseType<ApiResponse<InvoiceDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<InvoiceDetailDto>>> Invoice(long id, CancellationToken ct)
        => OkEnvelope(await queries.GetInvoiceAsync(CurrentActor.RequireOrganizationId(), id, ct));

    /// <summary>"Thanh toán" hóa đơn chưa trả (kể cả "Thanh toán lại" sau khi thất bại): giao dịch mới và địa chỉ VNPay. Hóa đơn quá hạn, đã trả, đã hủy thì 422.</summary>
    [HttpPost("invoices/{id:long}/pay")]
    [ProducesResponseType<ApiResponse<PaymentStartDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<PaymentStartDto>>> Pay(long id, CancellationToken ct)
        => OkEnvelope(await billing.PayInvoiceAsync(CurrentActor, id, ct));

    /// <summary>
    /// Trang Kết quả thanh toán: chỉ đọc trạng thái do IPN ghi nhận (đã thanh toán, chưa thanh toán, đang chờ xác nhận); trang tự tải lại khi đang chờ.
    /// Giao dịch của trường khác trả 404.
    /// </summary>
    [HttpGet("payments/{id:long}")]
    [ProducesResponseType<ApiResponse<PaymentResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<PaymentResultDto>>> PaymentResult(long id, CancellationToken ct)
        => OkEnvelope(await queries.GetPaymentResultAsync(CurrentActor, id, ct));
}
