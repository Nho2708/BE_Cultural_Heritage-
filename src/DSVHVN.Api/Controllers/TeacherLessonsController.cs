using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khu Giáo viên: bài học cho lớp theo cấp (màn hình Bài học của tôi, Tạo bài học, Soạn bài học, Mã bài học, Nhân bản bài học sang lớp khác).
/// Giáo viên chỉ thấy và sửa bài do chính mình tạo ở lớp của mình; bài của giáo viên khác hoặc trường khác trả 404. Quản trị trường và
/// quản trị hệ thống nhận 403. Bài dành cho đúng một lớp; cấp của bài là cấp của lớp.
/// </summary>
[Route("api/v1/teacher/lessons")]
[Authorize(Policy = Policies.Teacher)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class TeacherLessonsController(LessonService lessons, LessonAiService ai, StudentLessonService studentView) : ApiControllerBase
{
    /// <summary>Danh sách bài học của giáo viên ở mọi lớp: tiêu đề, lớp, di sản, trạng thái, mã bài học, số bài kiểm tra. Lọc theo lớp, trạng thái, loại di sản, từ khóa trong tiêu đề.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<LessonPageDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<LessonPageDto>>> List(
        [FromQuery] long? classId, [FromQuery] ContentStatus? status, [FromQuery] HeritageType? heritageType, [FromQuery] string? keyword,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => OkEnvelope(await lessons.ListAsync(CurrentActor, new LessonListQuery(classId, status, heritageType, keyword, page, pageSize), ct));

    /// <summary>
    /// Tạo bài học nháp cho một lớp của giáo viên với 1-10 di sản đã xuất bản. Cần gói còn hiệu lực. Cấp của bài là cấp của lớp,
    /// không chọn tay; lớp không đổi sau khi tạo.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<LessonDetailDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<LessonDetailDto>>> Create(CreateLessonRequest request, CancellationToken ct)
        => CreatedEnvelope(await lessons.CreateAsync(CurrentActor, request, ct), Messages.Success("Tạo bài học").Text);

    /// <summary>Lượt AI đã dùng, tối đa và còn lại của trường trong kỳ gói hiện tại (hiện trên nút gọi AI).</summary>
    [HttpGet("ai-quota")]
    [ProducesResponseType<ApiResponse<AiQuotaDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<AiQuotaDto>>> AiQuota(CancellationToken ct)
        => OkEnvelope(await ai.GetQuotaAsync(CurrentActor, ct));

    /// <summary>
    /// Chi tiết bài để soạn: nội dung theo khung của cấp, các phần, di sản, điều kiện xuất bản (thiếu gì, cảnh báo gì), số bài kiểm tra,
    /// mã bài học nếu đã xuất bản, hạn mức lượt AI.
    /// </summary>
    [HttpGet("{lessonId:long}")]
    [ProducesResponseType<ApiResponse<LessonDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<LessonDetailDto>>> Get(long lessonId, CancellationToken ct)
        => OkEnvelope(await lessons.GetAsync(CurrentActor, lessonId, ct));

    /// <summary>
    /// Xem như học sinh (nút của Soạn bài học): trả bài đúng khung, luồng và dữ liệu mà học sinh lớp này thấy (cùng cấu trúc <c>StudentLessonDto</c> của API học sinh),
    /// kể cả bài Nháp chưa có mã. Chỉ giáo viên của bài; bài của người khác 404, quản trị trường và quản trị hệ thống 403. Không ghi gì (không lượt làm bài,
    /// tiến độ, lịch sử), không gọi Dịch vụ AI, không tính lượt AI; bài kiểm tra chỉ để xem câu hỏi.
    /// </summary>
    [HttpGet("{lessonId:long}/preview")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<LessonPreviewDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<LessonPreviewDto>>> PreviewAsStudent(long lessonId, CancellationToken ct)
        => OkEnvelope(await studentView.PreviewAsync(CurrentActor, lessonId, ct));

    /// <summary>
    /// Lưu bài (Nháp hoặc Đã xuất bản). Trường null thì giữ nguyên, có giá trị thì thay toàn bộ. Loại phần phải thuộc khung của cấp lớp, hình và
    /// sự kiện phải thuộc di sản của bài. Bài Đã xuất bản phải giữ đủ điều kiện xuất bản (422 nếu không). Di sản chỉ đổi được khi bài là Nháp.
    /// </summary>
    [HttpPut("{lessonId:long}")]
    [ProducesResponseType<ApiResponse<LessonDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<LessonDetailDto>>> Update(long lessonId, UpdateLessonRequest request, CancellationToken ct)
        => OkEnvelope(await lessons.UpdateAsync(CurrentActor, lessonId, request, ct), Messages.Success("Lưu bài học").Text);

    /// <summary>Xóa mềm bài chưa có bài kiểm tra nào; bài đã có bài kiểm tra chỉ lưu trữ được.</summary>
    [HttpDelete("{lessonId:long}")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(long lessonId, CancellationToken ct)
    {
        await lessons.DeleteAsync(CurrentActor, lessonId, ct);
        return OkEnvelope<object>(null, Messages.Success("Xóa bài học").Text);
    }

    /// <summary>
    /// Xuất bản bài Nháp hoặc xuất bản lại bài Lưu trữ khi đủ điều kiện. Lần đầu sinh MỘT mã bài học (BH + 6 ký tự) để đưa cho lớp;
    /// xuất bản lại giữ nguyên mã. Không cần gói hiệu lực.
    /// </summary>
    [HttpPost("{lessonId:long}/publish")]
    [ProducesResponseType<ApiResponse<PublishLessonResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<PublishLessonResultDto>>> Publish(long lessonId, CancellationToken ct)
    {
        var result = await lessons.PublishAsync(CurrentActor, lessonId, ct);
        return OkEnvelope(result, result.Message);
    }

    /// <summary>Lưu trữ bài Đã xuất bản: học sinh không mở bằng mã được nữa. Bị chặn khi bài còn bài kiểm tra đang mở.</summary>
    [HttpPost("{lessonId:long}/archive")]
    [ProducesResponseType<ApiResponse<LessonDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<LessonDetailDto>>> Archive(long lessonId, CancellationToken ct)
        => OkEnvelope(await lessons.ArchiveAsync(CurrentActor, lessonId, ct), Messages.Success("Lưu trữ bài học").Text);

    /// <summary>
    /// Nhân bản bài sang một lớp khác của chính giáo viên: tạo bài Nháp mới chưa có mã, chép nội dung, các phần và di sản đang xuất bản.
    /// Cần gói hiệu lực, không tốn lượt AI. Lớp đích khác cấp thì vẫn chép kèm lời nhắc xem lại.
    /// </summary>
    [HttpPost("{lessonId:long}/duplicate")]
    [ProducesResponseType<ApiResponse<DuplicateLessonResultDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<DuplicateLessonResultDto>>> Duplicate(long lessonId, DuplicateLessonRequest request, CancellationToken ct)
    {
        var result = await lessons.DuplicateAsync(CurrentActor, lessonId, request, ct);
        return CreatedEnvelope(result, result.Message);
    }

    /// <summary>Ảnh (kèm ghi công) và sự kiện trên dòng thời gian của các di sản đang xuất bản của bài, để chọn hình và mốc thời gian.</summary>
    [HttpGet("{lessonId:long}/materials")]
    [ProducesResponseType<ApiResponse<LessonMaterialsDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<LessonMaterialsDto>>> Materials(long lessonId, CancellationToken ct)
        => OkEnvelope(await lessons.GetMaterialsAsync(CurrentActor, lessonId, ct));

    /// <summary>
    /// AI soạn nội dung bài đúng cấp của lớp (cấp học lấy từ lớp, không gửi lên), độ dài Ngắn / Vừa / Chi tiết trong khoảng của cấp. Mỗi lần
    /// gọi, kể cả tạo lại, là một lượt AI của trường. Trả bản nháp để giáo viên xem và sửa; AI không tự lưu hay xuất bản. 422 khi trường không có
    /// gói hiệu lực hoặc hết lượt; 503 khi dịch vụ AI lỗi sau các lần thử (lượt không bị tính, nội dung bài giữ nguyên).
    /// </summary>
    [HttpPost("{lessonId:long}/ai/compose")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<LessonDraftDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<LessonDraftDto>>> Compose(long lessonId, ComposeLessonRequest request, CancellationToken ct)
    {
        var draft = await ai.ComposeAsync(CurrentActor, lessonId, request, HttpContext.TraceIdentifier, ct);
        return OkEnvelope(draft, draft.Message);
    }
}
