using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khu Giáo viên: Ngân hàng câu hỏi 8 dạng dùng chung mọi trường (màn hình Ngân hàng câu hỏi, Biểu mẫu câu hỏi theo dạng, duyệt câu AI).
/// Câu đã duyệt hiện cho mọi giáo viên; câu chờ duyệt và bị loại chỉ người tạo thấy (người khác nhận 404). Chỉ người tạo sửa, xóa, duyệt câu của mình,
/// và chỉ khi câu chưa nằm trong bài kiểm tra nào; câu khác chỉ đọc, muốn sửa thì nhân bản. Quản trị trường, học sinh nhận 403.
/// </summary>
[Route("api/v1/teacher/questions")]
[Authorize(Policy = Policies.Teacher)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class TeacherQuestionsController(QuestionService questions) : ApiControllerBase
{
    /// <summary>
    /// Tìm và lọc ngân hàng. <c>scope</c> = SHARED (mặc định, câu đã duyệt của mọi trường), MINE (câu của mình, lọc thêm theo <c>status</c>) hoặc PENDING
    /// (câu AI chờ duyệt của mình). Lọc theo dạng, cấp, độ khó, di sản, nguồn, người tạo (<c>createdBy</c>, tab dùng chung) và từ khóa trong lời câu.
    /// Mới tạo trước; mỗi dòng có tên người tạo, tên trường, số bài kiểm tra đang dùng câu.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<QuestionPageDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<QuestionPageDto>>> List(
        [FromQuery] QuestionScope? scope, [FromQuery] QuestionType? questionType, [FromQuery] int? level,
        [FromQuery] DifficultyLevel? difficultyLevel, [FromQuery] long? heritageId, [FromQuery] ReviewStatus? status,
        [FromQuery] QuestionSource? source, [FromQuery] long? createdBy, [FromQuery] string? keyword,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => OkEnvelope(await questions.ListAsync(CurrentActor,
            new QuestionListQuery(scope, questionType, level, difficultyLevel, heritageId, status, source, createdBy, keyword, page, pageSize), ct));

    /// <summary>Số đếm các tab: dùng chung, của tôi, chờ duyệt, bị loại.</summary>
    [HttpGet("counts")]
    [ProducesResponseType<ApiResponse<QuestionCountsDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<QuestionCountsDto>>> Counts(CancellationToken ct)
        => OkEnvelope(await questions.CountsAsync(CurrentActor, ct));

    /// <summary>Bảng dạng câu theo cấp: 8 dạng, cấp dùng được của từng dạng và các dạng của cấp 1, 2, 3.</summary>
    [HttpGet("types")]
    [ProducesResponseType<ApiResponse<QuestionTypesDto>>(StatusCodes.Status200OK)]
    public ActionResult<ApiResponse<QuestionTypesDto>> Types() => OkEnvelope(QuestionService.Types());

    /// <summary>
    /// Tạo câu thủ công, duyệt ngay. Phần đáp án theo dạng (xem <see cref="SaveQuestionRequest"/>), một di sản đã xuất bản, cấp phù hợp trong các cấp
    /// của dạng, mọi ảnh phải nằm trong thư viện ảnh của di sản. 400 kèm lỗi theo trường khi cấu trúc chưa đúng.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<QuestionDetailDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<QuestionDetailDto>>> Create(SaveQuestionRequest request, CancellationToken ct)
        => CreatedEnvelope(await questions.CreateAsync(CurrentActor, request, ct), Messages.Success("Tạo câu hỏi").Text);

    /// <summary>
    /// Kiểm hợp lệ câu đang soạn mà không lưu: luôn trả 200 với <c>isValid</c>, lỗi theo trường, số phần chấm, cách học sinh của cấp thấy câu
    /// (<c>previewLevel</c>) và kết quả chấm thử nếu gửi kèm cách làm (<c>submission</c>).
    /// </summary>
    [HttpPost("validate")]
    [ProducesResponseType<ApiResponse<ValidateQuestionResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<ValidateQuestionResultDto>>> Validate(ValidateQuestionRequest request, CancellationToken ct)
        => OkEnvelope(await questions.ValidateAsync(CurrentActor, request, ct));

    /// <summary>Duyệt nhiều câu của mình một lần (tối đa 100); câu không duyệt được nằm ở <c>skipped</c> kèm lý do.</summary>
    [HttpPost("approve")]
    [ProducesResponseType<ApiResponse<ApproveQuestionsResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<ApproveQuestionsResultDto>>> ApproveMany(ApproveQuestionsRequest request, CancellationToken ct)
    {
        var result = await questions.ApproveManyAsync(CurrentActor, request, ct);
        return OkEnvelope(result, result.Message);
    }

    /// <summary>
    /// Chi tiết câu có đáp án, ảnh kèm ghi công, số bài kiểm tra đang dùng và quyền của người xem (<c>access</c>: chỉ đọc vì lý do gì, nút nào bật).
    /// Câu đã duyệt của mọi trường xem được; câu chờ duyệt, bị loại của người khác trả 404.
    /// </summary>
    [HttpGet("{questionId:long}")]
    [ProducesResponseType<ApiResponse<QuestionDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<QuestionDetailDto>>> Get(long questionId, CancellationToken ct)
        => OkEnvelope(await questions.GetAsync(CurrentActor, questionId, ct));

    /// <summary>
    /// Sửa câu của mình chưa nằm trong bài kiểm tra nào (thay toàn bộ nội dung và đáp án). Trạng thái giữ nguyên. 403 khi không phải người tạo;
    /// 422 khi câu đã nằm trong bài kiểm tra, đã bị ẩn, hoặc đổi dạng của câu do AI sinh.
    /// </summary>
    [HttpPut("{questionId:long}")]
    [ProducesResponseType<ApiResponse<QuestionDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<QuestionDetailDto>>> Update(long questionId, SaveQuestionRequest request, CancellationToken ct)
        => OkEnvelope(await questions.UpdateAsync(CurrentActor, questionId, request, ct), Messages.Success("Lưu câu hỏi").Text);

    /// <summary>Xóa mềm câu của mình chưa nằm trong bài kiểm tra nào.</summary>
    [HttpDelete("{questionId:long}")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(long questionId, CancellationToken ct)
    {
        await questions.DeleteAsync(CurrentActor, questionId, ct);
        return OkEnvelope<object>(null, Messages.Success("Xóa câu hỏi").Text);
    }

    /// <summary>Duyệt câu AI chờ duyệt hoặc câu mình đã loại (kiểm lại cấu trúc, ghi người và thời điểm duyệt).</summary>
    [HttpPost("{questionId:long}/approve")]
    [ProducesResponseType<ApiResponse<QuestionDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<QuestionDetailDto>>> Approve(long questionId, CancellationToken ct)
        => OkEnvelope(await questions.ApproveAsync(CurrentActor, questionId, ct), Messages.Success("Duyệt câu hỏi").Text);

    /// <summary>Loại câu AI đang chờ duyệt của mình. Câu đã loại sửa được và duyệt lại.</summary>
    [HttpPost("{questionId:long}/reject")]
    [ProducesResponseType<ApiResponse<QuestionDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<QuestionDetailDto>>> Reject(long questionId, CancellationToken ct)
        => OkEnvelope(await questions.RejectAsync(CurrentActor, questionId, ct), Messages.Success("Loại câu hỏi").Text);

    /// <summary>
    /// Nhân bản câu đã duyệt (của mình hoặc dùng chung) thành câu mới của mình: tự tạo, duyệt ngay, chép dạng, đáp án, giải thích, độ khó, di sản,
    /// cấp phù hợp. 422 với câu bị quản trị hệ thống ẩn.
    /// </summary>
    [HttpPost("{questionId:long}/duplicate")]
    [ProducesResponseType<ApiResponse<QuestionDetailDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<QuestionDetailDto>>> Duplicate(long questionId, CancellationToken ct)
        => CreatedEnvelope(await questions.DuplicateAsync(CurrentActor, questionId, ct), Messages.Success("Nhân bản câu hỏi").Text);

    /// <summary>Xem trước như học sinh của một cấp thấy (<c>level</c> mặc định là cấp thấp nhất của câu): không có đáp án, các cột đã xáo.</summary>
    [HttpGet("{questionId:long}/preview")]
    [ProducesResponseType<ApiResponse<QuestionPreviewDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<QuestionPreviewDto>>> Preview(long questionId, [FromQuery] int? level, CancellationToken ct)
        => OkEnvelope(await questions.PreviewAsync(CurrentActor, questionId, level, ct));

    /// <summary>Chấm thử một cách làm với câu đã lưu theo đúng phép chấm từng phần của bài kiểm tra; không ghi gì.</summary>
    [HttpPost("{questionId:long}/try-grade")]
    [ProducesResponseType<ApiResponse<GradeResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<GradeResultDto>>> TryGrade(long questionId, QuestionSubmission? submission, CancellationToken ct)
        => OkEnvelope(await questions.TryGradeAsync(CurrentActor, questionId, submission, ct));
}
