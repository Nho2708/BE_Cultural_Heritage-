using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Notifications;
using DSVHVN.Application.Quizzes;
using DSVHVN.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khu Giáo viên: bài kiểm tra của bài học (màn hình Bài kiểm tra của tôi, Soạn và phát hành bài kiểm tra, Chọn câu từ ngân hàng). Bài kiểm tra gắn một bài
/// học của chính giáo viên; lớp làm bài là lớp của bài học; phát hành lần đầu thì sinh mã bài kiểm tra (KT + 6 ký tự) để học sinh vào thẳng, mã bài học vẫn mở được. Giáo viên chỉ thấy và sửa bài kiểm tra do chính
/// mình tạo; bài của giáo viên khác hoặc trường khác trả 404. Quản trị trường, quản trị hệ thống và học sinh nhận 403.
/// </summary>
[Route("api/v1/teacher/quizzes")]
[Authorize(Policy = Policies.Teacher)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class TeacherQuizzesController(QuizService quizzes, StudentNotificationService notifications) : ApiControllerBase
{
    /// <summary>
    /// Danh sách bài kiểm tra của giáo viên: tên, bài học, lớp, trạng thái (Nháp, Đang phát hành, Đã đóng), giờ mở và đóng, số câu, tổng điểm, số đã nộp trên sĩ số.
    /// Lọc theo bài học, lớp, trạng thái, từ khóa trong tên; mới sửa trước.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<QuizPageDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<QuizPageDto>>> List(
        [FromQuery] long? lessonId, [FromQuery] long? classId, [FromQuery] QuizStatus? status, [FromQuery] string? keyword,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => OkEnvelope(await quizzes.ListAsync(CurrentActor, new QuizListQuery(lessonId, classId, status, keyword, page, pageSize), ct));

    /// <summary>
    /// Tạo bài kiểm tra nháp cho một bài học của giáo viên (bài Nháp hoặc Đã xuất bản, không phải bài Lưu trữ). Cần gói còn hiệu lực. Lớp là lớp của bài học.
    /// Thời gian làm theo cấp của lớp: cấp 1 phải để trống, cấp 2 tùy chọn, cấp 3 bắt buộc khi phát hành. Giờ không kèm múi giờ hiểu là giờ Việt Nam.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<QuizDetailDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<QuizDetailDto>>> Create(CreateQuizRequest request, CancellationToken ct)
        => CreatedEnvelope(await quizzes.CreateAsync(CurrentActor, request, ct), Messages.Success("Tạo bài kiểm tra").Text);

    /// <summary>
    /// Gửi thông báo cho cả lớp về bài đang phát hành và chưa hết hạn nhận bài: em thấy ở nút Thông báo và vào bài ngay. Lời nhắn tùy chọn, tối đa 500 ký tự.
    /// Bài Nháp, bài đã đóng hoặc lớp chưa có học sinh là 422.
    /// </summary>
    [HttpPost("{quizId:long}/notify-class")]
    [ProducesResponseType<ApiResponse<NotifyClassResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<NotifyClassResultDto>>> NotifyClass(long quizId, NotifyClassRequest request, CancellationToken ct)
    {
        var result = await notifications.NotifyQuizAsync(CurrentActor, quizId, request, ct);
        return OkEnvelope(result, result.Message);
    }

    /// <summary>
    /// Chi tiết để soạn: thiết lập, lớp và cấp của bài, quy tắc thời gian làm theo cấp, các câu theo thứ tự (câu không còn dùng được kèm lý do), tổng điểm,
    /// điều kiện phát hành (thiếu gì, cảnh báo gì), quyền bật tắt nút, số đã nộp trên sĩ số, lời xác nhận phát hành khi đủ điều kiện.
    /// </summary>
    [HttpGet("{quizId:long}")]
    [ProducesResponseType<ApiResponse<QuizDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<QuizDetailDto>>> Get(long quizId, CancellationToken ct)
        => OkEnvelope(await quizzes.GetAsync(CurrentActor, quizId, ct));

    /// <summary>
    /// Lưu thiết lập, gửi đủ mọi trường (trường để trống là xóa giá trị). Bài Nháp sửa tất cả. Bài đã phát hành chỉ sửa tên, mô tả, giờ đóng (không sớm hơn hiện tại);
    /// các trường khóa gửi lên phải giữ nguyên, khác thì 422. Bài đã đóng không sửa được (422).
    /// </summary>
    [HttpPut("{quizId:long}")]
    [ProducesResponseType<ApiResponse<QuizDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<QuizDetailDto>>> Update(long quizId, UpdateQuizRequest request, CancellationToken ct)
        => OkEnvelope(await quizzes.UpdateAsync(CurrentActor, quizId, request, ct), Messages.Success("Lưu bài kiểm tra").Text);

    /// <summary>Xóa mềm bài Nháp. Bài đã phát hành hoặc đã đóng không xóa được (422) để giữ kết quả.</summary>
    [HttpDelete("{quizId:long}")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(long quizId, CancellationToken ct)
    {
        await quizzes.DeleteAsync(CurrentActor, quizId, ct);
        return OkEnvelope<object>(null, Messages.Success("Xóa bài kiểm tra").Text);
    }

    /// <summary>
    /// Hộp "Chọn câu từ ngân hàng": câu đã duyệt, chưa xóa, dùng chung mọi trường, chỉ câu có dạng và cấp dùng được ở cấp của lớp. Mặc định lọc theo các di sản của bài học
    /// (<c>lessonHeritagesOnly=false</c> để xem mọi di sản); lọc thêm dạng câu, độ khó, di sản, từ khóa. Câu đã có trong bài có <c>inQuiz</c> = true.
    /// </summary>
    [HttpGet("{quizId:long}/question-candidates")]
    [ProducesResponseType<ApiResponse<QuizCandidatePageDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<QuizCandidatePageDto>>> Candidates(
        long quizId, [FromQuery] QuestionType? questionType, [FromQuery] DifficultyLevel? difficultyLevel, [FromQuery] long? heritageId,
        [FromQuery] bool? lessonHeritagesOnly, [FromQuery] string? keyword, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => OkEnvelope(await quizzes.CandidatesAsync(CurrentActor, quizId,
            new QuizCandidateQuery(questionType, difficultyLevel, heritageId, lessonHeritagesOnly, keyword, page, pageSize), ct));

    /// <summary>
    /// Thêm câu từ ngân hàng vào cuối bài Nháp, mỗi câu <c>point</c> điểm (mặc định 1, bước 0,25, tối đa 100). Chỉ nhận câu đã duyệt, chưa xóa, có dạng và cấp dùng được
    /// ở cấp của lớp, chưa có trong bài; tối đa 50 câu. Một câu sai thì không thêm câu nào (400 với câu không chọn được, 422 với câu không hợp cấp của lớp).
    /// </summary>
    [HttpPost("{quizId:long}/questions")]
    [ProducesResponseType<ApiResponse<QuizDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<QuizDetailDto>>> AddQuestions(long quizId, AddQuizQuestionsRequest request, CancellationToken ct)
        => OkEnvelope(await quizzes.AddQuestionsAsync(CurrentActor, quizId, request, ct), Messages.Success("Thêm câu hỏi").Text);

    /// <summary>
    /// Đặt lại điểm, thứ tự và bỏ câu của bài Nháp: danh sách đầy đủ theo thứ tự mới, câu không có trong danh sách bị bỏ; chỉ nhận câu đã có trong bài.
    /// Thứ tự đánh lại liên tục từ 1, tổng điểm tính lại. Bài đã phát hành thì khóa (422).
    /// </summary>
    [HttpPut("{quizId:long}/questions")]
    [ProducesResponseType<ApiResponse<QuizDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<QuizDetailDto>>> ReplaceQuestions(long quizId, ReplaceQuizQuestionsRequest request, CancellationToken ct)
        => OkEnvelope(await quizzes.ReplaceQuestionsAsync(CurrentActor, quizId, request, ct), Messages.Success("Lưu danh sách câu hỏi").Text);

    /// <summary>Bỏ một câu khỏi bài Nháp; các câu còn lại đánh lại thứ tự và tổng điểm tính lại.</summary>
    [HttpDelete("{quizId:long}/questions/{questionId:long}")]
    [ProducesResponseType<ApiResponse<QuizDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<QuizDetailDto>>> RemoveQuestion(long quizId, long questionId, CancellationToken ct)
        => OkEnvelope(await quizzes.RemoveQuestionAsync(CurrentActor, quizId, questionId, ct), Messages.Success("Bỏ câu hỏi").Text);

    /// <summary>
    /// Phát hành bài Nháp khi đủ điều kiện (xem <c>readiness</c> của chi tiết): khóa danh sách câu, ghi tổng điểm và thời điểm phát hành, ghi nhật ký. Không sinh mã,
    /// không chọn lớp. Cần gói còn hiệu lực. 422 kèm danh sách còn thiếu khi chưa đủ điều kiện. Phát hành bài đã phát hành không đổi gì.
    /// </summary>
    [HttpPost("{quizId:long}/publish")]
    [ProducesResponseType<ApiResponse<PublishQuizResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<PublishQuizResultDto>>> Publish(long quizId, CancellationToken ct)
    {
        var result = await quizzes.PublishAsync(CurrentActor, quizId, ct);
        return OkEnvelope(result, result.Message);
    }

    /// <summary>
    /// Đóng bài đã phát hành bất kỳ lúc nào (không mở lại): các lượt làm bài đang dở được tự nộp ở trạng thái hết giờ; ghi nhật ký.
    /// Bài quá giờ đóng còn được tác vụ nền tự đóng. Đóng bài đã đóng không đổi gì; bài Nháp không đóng được (422).
    /// </summary>
    [HttpPost("{quizId:long}/close")]
    [ProducesResponseType<ApiResponse<CloseQuizResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<CloseQuizResultDto>>> Close(long quizId, CancellationToken ct)
    {
        var result = await quizzes.CloseAsync(CurrentActor, quizId, ct);
        return OkEnvelope(result, result.Message);
    }

    /// <summary>
    /// Nhân bản bài kiểm tra trong cùng bài học thành bài Nháp mới: cùng câu, điểm, thứ tự, thiết lập; không chép giờ mở, giờ đóng, lượt làm bài. Câu đã bị ẩn hoặc xóa
    /// không được chép và được báo số câu. Cần gói còn hiệu lực. Dùng để đổi câu của bài đã phát hành hoặc dùng lại bài đã đóng. Muốn dùng cho lớp khác thì nhân bản bài
    /// học kèm bài kiểm tra.
    /// </summary>
    [HttpPost("{quizId:long}/duplicate")]
    [ProducesResponseType<ApiResponse<DuplicateQuizResultDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<DuplicateQuizResultDto>>> Duplicate(long quizId, DuplicateQuizRequest? request, CancellationToken ct)
    {
        var result = await quizzes.DuplicateAsync(CurrentActor, quizId, request ?? new DuplicateQuizRequest(), ct);
        return CreatedEnvelope(result, result.Message);
    }
}
