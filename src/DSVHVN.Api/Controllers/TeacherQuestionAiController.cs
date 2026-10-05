using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Questions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khu Giáo viên: AI sinh câu hỏi theo dạng và cấp của lớp từ một bài học (màn hình Sinh và duyệt câu hỏi bằng AI). Chỉ giáo viên có bài (404 với bài
/// của người khác); quản trị trường, quản trị hệ thống và học sinh nhận 403.
/// </summary>
[Route("api/v1/teacher/lessons/{lessonId:long}/ai/questions")]
[Authorize(Policy = Policies.Teacher)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class TeacherQuestionAiController(QuestionAiService ai) : ApiControllerBase
{
    /// <summary>
    /// Sinh 1-20 câu (mặc định 5) theo độ khó (mặc định vừa), các dạng trong dạng dùng được ở cấp của lớp (để trống thì lấy mọi dạng của cấp) và di sản
    /// trong các di sản của bài (để trống thì dùng cả bài). Mỗi lần gọi, kể cả tạo lại, là một lượt AI của trường. Câu đạt quy tắc vào ngân hàng ở trạng
    /// thái chờ duyệt; câu không đạt bị bỏ và <c>received</c>, <c>dropped</c> báo số câu. 422 khi trường không có gói hiệu lực hoặc hết lượt, bài lưu trữ,
    /// bài không có di sản đã xuất bản; 503 khi dịch vụ AI lỗi sau các lần thử (lượt không bị tính, không thêm câu nào).
    /// </summary>
    [HttpPost]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<QuestionBatchDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<QuestionBatchDto>>> Generate(long lessonId, GenerateQuestionsRequest request, CancellationToken ct)
    {
        var batch = await ai.GenerateAsync(CurrentActor, lessonId, request, HttpContext.TraceIdentifier, ct);
        return OkEnvelope(batch, batch.Message);
    }

    /// <summary>Lượt AI đã dùng, tối đa và còn lại của trường trong kỳ gói hiện tại.</summary>
    [HttpGet("quota")]
    [ProducesResponseType<ApiResponse<AiQuotaDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<AiQuotaDto>>> Quota(long lessonId, CancellationToken ct)
        => OkEnvelope(await ai.GetQuotaAsync(CurrentActor, ct));
}
