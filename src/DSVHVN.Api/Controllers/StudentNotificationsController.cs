using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>Thông báo của học sinh (nút Thông báo trên web học tập và app): thầy cô giao bài học hoặc bài kiểm tra cho lớp. Chỉ thấy thông báo của chính em.</summary>
[Route("api/v1/student/notifications")]
[Authorize(Policy = Policies.StudentReady)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class StudentNotificationsController(StudentNotificationService notifications) : ApiControllerBase
{
    /// <summary>Thông báo của em, mới nhất trước, kèm số chưa đọc.</summary>
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<StudentNotificationPageDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<StudentNotificationPageDto>>> List([FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => OkEnvelope(await notifications.ListAsync(CurrentActor, page, pageSize, ct));

    /// <summary>Đánh dấu một thông báo đã đọc; gọi lại nhiều lần không đổi gì. Thông báo của em khác như không tồn tại (404).</summary>
    [HttpPost("{id:long}/read")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<object>>> MarkRead(long id, CancellationToken ct)
    {
        await notifications.MarkReadAsync(CurrentActor, id, ct);
        return OkEnvelope<object>(null);
    }

    /// <summary>Đánh dấu mọi thông báo của em đã đọc.</summary>
    [HttpPost("read-all")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<object>>> MarkAllRead(CancellationToken ct)
    {
        await notifications.MarkAllReadAsync(CurrentActor, ct);
        return OkEnvelope<object>(null);
    }
}
