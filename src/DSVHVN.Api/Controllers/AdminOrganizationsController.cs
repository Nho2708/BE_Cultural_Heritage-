using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Accounts;
using DSVHVN.Application.Common;
using DSVHVN.Application.Organizations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>Quản lý trường phía ADMIN: màn hình Quản lý trường, Chi tiết trường và biểu mẫu tài khoản quản trị trường.</summary>
[Route("api/v1/admin/organizations")]
[Authorize(Policy = Policies.Admin)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class AdminOrganizationsController(OrganizationService organizations) : ApiControllerBase
{
    /// <summary>Danh sách trường: tìm theo tên/email, lọc trạng thái ACTIVE | DEACTIVATED, phân trang.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<PagedResult<OrganizationSummaryDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedResult<OrganizationSummaryDto>>>> List(
        [FromQuery] string? keyword, [FromQuery] OrganizationState? status, [FromQuery] int? page, [FromQuery] int? pageSize,
        CancellationToken ct)
        => OkEnvelope(await organizations.ListAsync(new OrganizationListQuery(keyword, status, page, pageSize), ct));

    /// <summary>Tạo trường + tài khoản quản trị trường đầu tiên; gán gói "Miễn phí"; gửi email đặt mật khẩu.</summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<OrganizationDetailDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<OrganizationDetailDto>>> Create(CreateOrganizationRequest request, CancellationToken ct)
    {
        var created = await organizations.CreateAsync(request, CurrentActor, ct);
        return CreatedEnvelope(created.Data, Messages.PasswordLinkSent(created.NotifiedEmail).Text);
    }

    [HttpGet("{id:long}")]
    [ProducesResponseType<ApiResponse<OrganizationDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<OrganizationDetailDto>>> Get(long id, CancellationToken ct)
        => OkEnvelope(await organizations.GetAsync(id, ct));

    /// <summary>Sửa thông tin trường (cả tên). Trường đã ngừng thì 422 (thao tác bị chặn).</summary>
    [HttpPut("{id:long}")]
    [ProducesResponseType<ApiResponse<OrganizationDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<OrganizationDetailDto>>> Update(long id, OrganizationInfoRequest request, CancellationToken ct)
        => OkEnvelope(await organizations.UpdateAsync(id, request, ct), Messages.Success("Lưu thông tin trường").Text);

    /// <summary>Ngừng trường (xóa mềm): mọi tài khoản của trường không đăng nhập được, dữ liệu giữ nguyên.</summary>
    [HttpDelete("{id:long}")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<object>>> Deactivate(long id, CancellationToken ct)
    {
        await organizations.DeactivateAsync(id, CurrentActor, ct);
        return OkEnvelope<object>(null, Messages.Success("Ngừng trường").Text);
    }

    /// <summary>Thêm quản trị trường (không tính vào max_teachers); gửi email đặt mật khẩu.</summary>
    [HttpPost("{id:long}/org-admins")]
    [ProducesResponseType<ApiResponse<AccountDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<AccountDto>>> AddOrgAdmin(long id, NewAccountRequest request, CancellationToken ct)
    {
        var created = await organizations.AddOrgAdminAsync(id, request, CurrentActor, ct);
        return CreatedEnvelope(created.Data, Messages.PasswordLinkSent(created.NotifiedEmail).Text);
    }

    [HttpPost("{id:long}/org-admins/{userId:long}/lock")]
    [ProducesResponseType<ApiResponse<AccountDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<AccountDto>>> LockOrgAdmin(long id, long userId, CancellationToken ct)
        => OkEnvelope(await organizations.LockOrgAdminAsync(id, userId, CurrentActor, ct), Messages.Success("Khóa tài khoản").Text);

    [HttpPost("{id:long}/org-admins/{userId:long}/unlock")]
    [ProducesResponseType<ApiResponse<AccountDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<AccountDto>>> UnlockOrgAdmin(long id, long userId, CancellationToken ct)
        => OkEnvelope(await organizations.UnlockOrgAdminAsync(id, userId, CurrentActor, ct), Messages.Success("Mở khóa tài khoản").Text);
}
