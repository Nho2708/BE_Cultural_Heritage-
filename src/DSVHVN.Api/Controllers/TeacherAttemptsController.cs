using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Statistics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>Khu Giáo viên: chi tiết bài làm của một học sinh (hộp thoại mở từ kết quả bài kiểm tra và từ hồ sơ học tập).</summary>
[Route("api/v1/teacher/attempts")]
[Authorize(Policy = Policies.Teacher)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class TeacherAttemptsController(QuizResultsService results) : ApiControllerBase
{
    /// <summary>
    /// Chi tiết một lượt đã nộp: từng câu như em đã thấy, câu em trả lời (phương án đã chọn, cặp đã ghép, thứ tự đã sắp, từ đã điền, lựa chọn từng mệnh đề hoặc bỏ trống)
    /// đặt cạnh đáp án đúng, đúng hay sai, điểm đạt (câu nhiều phần có số phần đúng), giải thích; kèm điểm, thời gian, số sao (lớp cấp 1) và huy hiệu.
    /// Xem được kể cả khi bài không cho em xem đáp án. Lượt của lớp khác hoặc giáo viên khác trả 404; lượt còn đang làm trả 409.
    /// </summary>
    [HttpGet("{attemptId:long}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<TeacherAttemptDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<TeacherAttemptDetailDto>>> Get(long attemptId, CancellationToken ct)
        => OkEnvelope(await results.AttemptDetailAsync(CurrentActor, attemptId, ct));
}
