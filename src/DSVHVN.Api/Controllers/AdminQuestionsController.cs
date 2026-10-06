using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khu Quản trị hệ thống: xem ngân hàng câu hỏi chỉ đọc và ẩn câu kém chất lượng. Chỉ thấy câu đã duyệt và câu đã bị ẩn; không sửa, không duyệt.
/// Ẩn ghi nhật ký thao tác. Giáo viên, quản trị trường nhận 403.
/// </summary>
[Route("api/v1/admin/questions")]
[Authorize(Policy = Policies.Admin)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class AdminQuestionsController(AdminQuestionService questions) : ApiControllerBase
{
    /// <summary>Câu đã duyệt và câu đã bị ẩn (<c>status</c> = APPROVED hoặc REJECTED); lọc theo dạng, cấp, độ khó, di sản, nguồn, người tạo, từ khóa.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<QuestionPageDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<QuestionPageDto>>> List(
        [FromQuery] QuestionType? questionType, [FromQuery] int? level, [FromQuery] DifficultyLevel? difficultyLevel,
        [FromQuery] long? heritageId, [FromQuery] ReviewStatus? status, [FromQuery] QuestionSource? source,
        [FromQuery] long? createdBy, [FromQuery] string? keyword, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => OkEnvelope(await questions.ListAsync(CurrentActor,
            new QuestionListQuery(null, questionType, level, difficultyLevel, heritageId, status, source, createdBy, keyword, page, pageSize), ct));

    /// <summary>
    /// Danh sách kiểm duyệt: câu đã duyệt và câu đã bị ẩn kèm số lượt trả lời, số lượt đúng, tỉ lệ đúng và dấu hiệu "cần xem lại" (đủ 10 lượt trả lời mà tỉ lệ
    /// đúng dưới 30%). <c>lowQualityOnly</c> = true chỉ giữ câu cần xem lại; <c>sort</c> = NEWEST (mặc định) hoặc LOWEST_RATE; lọc thêm theo trường của người tạo
    /// (<c>organizationId</c>). Kèm ba số đếm của cả ngân hàng: đã duyệt, đã ẩn, cần xem lại.
    /// </summary>
    [HttpGet("quality")]
    [ProducesResponseType<ApiResponse<QuestionQualityPageDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<QuestionQualityPageDto>>> Quality(
        [FromQuery] ReviewStatus? status, [FromQuery] bool? lowQualityOnly, [FromQuery] QuestionQualitySort? sort,
        [FromQuery] QuestionType? questionType, [FromQuery] int? level, [FromQuery] DifficultyLevel? difficultyLevel,
        [FromQuery] long? heritageId, [FromQuery] long? organizationId, [FromQuery] long? createdBy, [FromQuery] string? keyword,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => OkEnvelope(await questions.ListQualityAsync(CurrentActor,
            new QuestionQualityQuery(status, lowQualityOnly ?? false, sort, questionType, level, difficultyLevel, heritageId, organizationId, createdBy, keyword, page, pageSize), ct));

    /// <summary>Chi tiết câu chỉ đọc (có đáp án), kèm quyền: ẩn được khi câu đang được duyệt, hiện lại được khi câu đã bị ẩn.</summary>
    [HttpGet("{questionId:long}")]
    [ProducesResponseType<ApiResponse<QuestionDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<QuestionDetailDto>>> Get(long questionId, CancellationToken ct)
        => OkEnvelope(await questions.GetAsync(CurrentActor, questionId, ct));

    /// <summary>
    /// Ẩn câu đã duyệt kém chất lượng: chuyển sang bị loại với người duyệt là quản trị hệ thống, ghi nhật ký thao tác. Câu bị ẩn không chọn được vào
    /// bài kiểm tra mới, không nhân bản được; bài kiểm tra đã phát hành và kết quả cũ giữ nguyên. 422 khi câu chưa được duyệt hoặc đã bị ẩn.
    /// </summary>
    [HttpPost("{questionId:long}/hide")]
    [ProducesResponseType<ApiResponse<QuestionDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<QuestionDetailDto>>> Hide(long questionId, AdminHideQuestionRequest? request, CancellationToken ct)
        => OkEnvelope(await questions.HideAsync(CurrentActor, questionId, request ?? new AdminHideQuestionRequest(null), ct),
            Messages.Success("Ẩn câu hỏi").Text);

    /// <summary>
    /// Hiện lại câu đã bị ẩn: câu về trạng thái đã duyệt của người tạo, chọn lại được vào bài kiểm tra mới và hiện lại cho mọi giáo viên; lý do (tùy chọn)
    /// ghi vào nhật ký thao tác. 422 khi câu chưa bị ẩn.
    /// </summary>
    [HttpPost("{questionId:long}/restore")]
    [ProducesResponseType<ApiResponse<QuestionDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<QuestionDetailDto>>> Restore(long questionId, AdminHideQuestionRequest? request, CancellationToken ct)
        => OkEnvelope(await questions.RestoreAsync(CurrentActor, questionId, request ?? new AdminHideQuestionRequest(null), ct),
            Messages.Success("Hiện lại câu hỏi").Text);
}
