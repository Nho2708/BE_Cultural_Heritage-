using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Attempts;
using DSVHVN.Application.Common;
using DSVHVN.Application.Questions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Làm bài kiểm tra của học sinh (web học tập và chế độ Học sinh của app dùng chung một lượt). Phải đăng nhập bằng tài khoản học sinh đã đổi mật khẩu;
/// em chỉ thấy và làm quiz của lớp mình, lượt của em khác như không tồn tại (404).
/// </summary>
[Route("api/v1/student")]
[Authorize(Policy = Policies.StudentReady)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class StudentAttemptsController(AttemptService attempts, StudentLearningService learning, AlbumService album) : ApiControllerBase
{
    /// <summary>
    /// Trang trước khi bắt đầu: tên, số câu, thời gian làm, giờ mở và đóng, và việc em làm được lúc này (Bắt đầu, Làm tiếp, Xem kết quả, chưa mở, đã đóng).
    /// Lớp cấp 1 không có điểm và thời gian làm.
    /// </summary>
    [HttpGet("quizzes/{quizId:long}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<StudentQuizIntroDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<StudentQuizIntroDto>>> Intro(long quizId, CancellationToken ct)
        => OkEnvelope(await attempts.GetIntroAsync(CurrentActor, quizId, ct));

    /// <summary>
    /// Bắt đầu làm bài (không cần nhập mã lần hai). Tạo lượt mới trả 201; đã có lượt đang làm thì trả đúng lượt đó với 200 (giờ vẫn tính từ lúc bắt đầu);
    /// đã nộp thì 409 (xem kết quả). Chưa tới giờ mở hoặc đã đóng là 422; quiz không thuộc lớp của em là 404.
    /// </summary>
    [HttpPost("quizzes/{quizId:long}/attempts")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<StudentAttemptDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<StudentAttemptDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<StudentAttemptDto>>> Start(long quizId, CancellationToken ct)
    {
        var (attempt, created) = await attempts.StartAsync(CurrentActor, quizId, ct);
        return created ? CreatedEnvelope(attempt) : OkEnvelope(attempt);
    }

    /// <summary>Lịch sử mọi lượt em đã nộp (nộp tay hoặc tự nộp khi hết giờ), mới nhất trước. Cấp 1 hiện sao thay điểm.</summary>
    [HttpGet("attempts")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<PagedResult<StudentAttemptRowDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedResult<StudentAttemptRowDto>>>> History([FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => OkEnvelope(await learning.HistoryAsync(CurrentActor, page, pageSize, ct));

    /// <summary>
    /// Lượt đang làm dở: các câu như em thấy (thẻ, cột, mục đã xáo cố định theo lượt), câu trả lời đã lưu, hạn nộp và số giây còn lại. Dùng để vào tiếp sau khi
    /// mất kết nối, tải lại hoặc đổi thiết bị. Lượt đã nộp (kể cả hết giờ tự nộp) trả 409 để chuyển sang xem kết quả.
    /// </summary>
    [HttpGet("attempts/{attemptId:long}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<StudentAttemptDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<StudentAttemptDto>>> Get(long attemptId, CancellationToken ct)
        => OkEnvelope(await attempts.GetAsync(CurrentActor, attemptId, ct));

    /// <summary>
    /// Lưu câu trả lời của một câu, ghi đè cả câu (thân rỗng <c>{}</c> là xóa câu trả lời) và chấm tạm; mọi vị trí tính theo thứ tự đang hiện trên màn hình em.
    /// Chọn một phương án: <c>selectedIndex</c>. Ghép cặp: <c>matches</c>, với mỗi hình bên trái (từ 0) là vị trí tên đã nối hoặc null. Sắp xếp: <c>order</c>,
    /// vị trí các mục theo thứ tự em đặt, phải đủ mọi mục. Điền từ: <c>blanks</c>, chữ ở từng chỗ trống. Nhiều mệnh đề: <c>statements</c>, đúng/sai từng mệnh đề.
    /// Sai cấu trúc là 400; lượt đã nộp hoặc quá hạn nhận bài (hạn nộp cộng 30 giây) là 409.
    /// </summary>
    [HttpPut("attempts/{attemptId:long}/answers/{questionId:long}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<SavedAnswerDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<SavedAnswerDto>>> SaveAnswer(long attemptId, long questionId, [FromBody] QuestionSubmission? answer, CancellationToken ct)
        => OkEnvelope(await attempts.SaveAnswerAsync(CurrentActor, attemptId, questionId, answer, ct));

    /// <summary>
    /// Cấp 1, bài chơi dạng luyện tập: kiểm một câu ngay khi em trả lời; thân như lưu câu trả lời (mọi vị trí tính theo thứ tự đang hiện trên màn hình em).
    /// Lần kiểm đầu tiên của câu được lưu làm câu trả lời để chấm (sao, thống kê của giáo viên) và không đổi nữa; các lần sau chỉ chấm câu gửi lên để em thử lại.
    /// Trả đúng sai từng phần, đáp án đúng và giải thích của câu. Câu trả lời trống hoặc ghép cặp chưa nối đủ mọi hình là 400; lượt đã nộp hoặc quá hạn nhận bài
    /// là 409; lớp không phải cấp 1 là 422.
    /// </summary>
    [HttpPost("attempts/{attemptId:long}/answers/{questionId:long}/check")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<AnswerCheckDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<AnswerCheckDto>>> CheckAnswer(long attemptId, long questionId, [FromBody] QuestionSubmission? answer, CancellationToken ct)
        => OkEnvelope(await attempts.CheckAnswerAsync(CurrentActor, attemptId, questionId, answer, ct));

    /// <summary>
    /// Nộp bài: chấm cố định từng câu (dạng nhiều phần chấm theo phần, câu bỏ trống 0 điểm), trả kết quả theo cấp của lớp. Nộp lần hai trả lại kết quả cũ.
    /// Quá hạn nhận bài thì lượt được chốt thành hết giờ với các câu đã lưu.
    /// </summary>
    [HttpPost("attempts/{attemptId:long}/submit")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<StudentAttemptResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<StudentAttemptResultDto>>> Submit(long attemptId, CancellationToken ct)
        => OkEnvelope(await attempts.SubmitAsync(CurrentActor, attemptId, ct), "Em đã nộp bài.");

    /// <summary>
    /// Kết quả của lượt đã nộp, kể cả khi bài đã đóng hay bài học đã lưu trữ, theo cấp hiện tại của lớp: cấp 1 sao, lời khen và huy hiệu (không điểm); cấp 2 điểm,
    /// số câu đúng, đáp án và giải thích từng câu nếu bài cho xem; cấp 3 thêm phân tích theo di sản và dạng câu. Lượt còn đang làm là 409.
    /// </summary>
    [HttpGet("attempts/{attemptId:long}/result")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<StudentAttemptResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<StudentAttemptResultDto>>> Result(long attemptId, CancellationToken ct)
        => OkEnvelope(await attempts.GetResultAsync(CurrentActor, attemptId, ct));

    /// <summary>
    /// Trang học của em: thông tin, lớp, giáo viên, bố cục theo cấp, lượt đang làm dở, bài kiểm tra còn mở em chưa làm (nhãn "Cần mã bài học" khi em chưa có lượt
    /// nào ở bài đó), bài học của em và huy hiệu gần đây. Cấp 1 có tổng số sao.
    /// </summary>
    [HttpGet("home")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<StudentHomeDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<StudentHomeDto>>> Home(CancellationToken ct)
        => OkEnvelope(await learning.HomeAsync(CurrentActor, ct));

    /// <summary>
    /// Album di sản của em (cấp 1): mỗi di sản của các bài thầy cô đã xuất bản cho lớp là một thẻ; thẻ đã có khi em học xong bài hoặc nộp bài chơi của bài đó.
    /// Thẻ đã có xếp trước (mới nhất trước), thẻ chưa có sau theo thứ tự bài học.
    /// </summary>
    [HttpGet("album")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<StudentAlbumDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<StudentAlbumDto>>> Album(CancellationToken ct)
        => OkEnvelope(await album.GetAsync(CurrentActor, ct));

    /// <summary>
    /// Tiến độ của em: số quiz đã nộp trên số quiz đã phát hành của lớp, bài học đã hoàn thành, mức thành thạo theo di sản (cấp 1 hiện thanh sao) và huy hiệu đã đạt.
    /// </summary>
    [HttpGet("progress")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<StudentProgressDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<StudentProgressDto>>> Progress(CancellationToken ct)
        => OkEnvelope(await learning.ProgressAsync(CurrentActor, ct));
}
