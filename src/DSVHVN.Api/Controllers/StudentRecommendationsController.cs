using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Recommendations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// "Gợi ý cho em" của học sinh (web học tập và chế độ Học sinh của app dùng chung): đủ năm phần, tính riêng cho từng em khi hiển thị từ cấp học, tiến độ và
/// kết quả làm bài, chỉ trong các bài học đã xuất bản của lớp em. Phải đăng nhập bằng tài khoản học sinh đã đổi mật khẩu; giáo viên, quản trị trường và
/// quản trị hệ thống nhận 403.
/// </summary>
[Route("api/v1/student")]
[Authorize(Policy = Policies.StudentReady)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class StudentRecommendationsController(StudentRecommendationService recommendations) : ApiControllerBase
{
    /// <summary>
    /// Gợi ý ở trang học, đủ năm phần: quiz nên làm tiếp (tối đa 3, quiz của bài em chưa có lượt nào gắn nhãn "Cần mã bài học"), bài học nên ôn lại (tối đa 3),
    /// di sản nên ôn (tối đa 3, từ mọi lượt đã nộp), di sản liên quan (tối đa 3) và bài học mới nên học (tối đa 3, chỉ có tiêu đề và câu "Nhờ thầy cô đưa mã bài học",
    /// không có mã). Em chưa có lượt nào thì có lời mời nhập mã bài học. Cấp 1 không có số liệu trong câu lý do.
    /// </summary>
    [HttpGet("recommendations")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<StudentRecommendationsDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<StudentRecommendationsDto>>> ForHome(CancellationToken ct)
        => OkEnvelope(await recommendations.GetAsync(CurrentActor, null, ct));

    /// <summary>
    /// Gợi ý ở trang kết quả của một lượt: cùng năm phần, nhưng "di sản nên ôn" chỉ tính từ các câu sai hoặc bỏ trống của lượt đang xem. Lượt của em khác như không
    /// tồn tại (404); lượt còn đang làm là 409.
    /// </summary>
    [HttpGet("attempts/{attemptId:long}/recommendations")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<StudentRecommendationsDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<StudentRecommendationsDto>>> ForAttempt(long attemptId, CancellationToken ct)
        => OkEnvelope(await recommendations.GetAsync(CurrentActor, attemptId, ct));
}
