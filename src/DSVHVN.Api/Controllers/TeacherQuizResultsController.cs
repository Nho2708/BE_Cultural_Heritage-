using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Recommendations;
using DSVHVN.Application.Statistics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khu Giáo viên: kết quả của một bài kiểm tra của lớp (màn hình Kết quả bài kiểm tra, các tab Tổng quan, Từng câu, Từng học sinh, Chủ đề, Gợi ý). Chỉ tính lượt đã
/// nộp (đã nộp hoặc hết giờ tự nộp) của học sinh đang hoạt động; giáo viên luôn thấy điểm ở mọi cấp. Bài kiểm tra của giáo viên khác hoặc trường khác trả 404;
/// quản trị trường, quản trị hệ thống và học sinh nhận 403.
/// </summary>
[Route("api/v1/teacher/quizzes/{quizId:long}/results")]
[Authorize(Policy = Policies.Teacher)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
public sealed class TeacherQuizResultsController(QuizResultsService results, ClassRecommendationService recommendations) : ApiControllerBase
{
    /// <summary>
    /// Tab Tổng quan: sĩ số, số đã nộp, hết giờ tự nộp, đang làm, chưa làm; điểm trung bình, cao nhất, thấp nhất (điểm và phần trăm) và phổ điểm 10 khoảng.
    /// Chưa có lượt nộp thì các số điểm là null kèm lời báo.
    /// </summary>
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<QuizResultsOverviewDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<QuizResultsOverviewDto>>> Overview(long quizId, CancellationToken ct)
        => OkEnvelope(await results.OverviewAsync(CurrentActor, quizId, ct));

    /// <summary>
    /// Tab Từng câu: tỉ lệ đúng từng câu (câu nhiều phần thêm tỉ lệ đúng từng phần), phương án bị chọn nhiều nhất (dạng một đáp án đúng, Đúng/Sai, chọn hình đúng,
    /// đọc tư liệu), lỗi hay gặp (ghép cặp, sắp xếp, điền từ, nhiều mệnh đề) và tỉ lệ đúng theo dạng câu.
    /// </summary>
    [HttpGet("questions")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<QuizQuestionStatsDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<QuizQuestionStatsDto>>> Questions(long quizId, CancellationToken ct)
        => OkEnvelope(await results.QuestionsAsync(CurrentActor, quizId, ct));

    /// <summary>
    /// Tab Từng học sinh: mỗi học sinh đang hoạt động một dòng (Đã nộp, Hết giờ tự nộp, Đang làm, Chưa làm) kèm điểm, thời gian làm, số sao (lớp cấp 1) và huy hiệu;
    /// <c>attemptId</c> để mở chi tiết bài làm.
    /// </summary>
    [HttpGet("students")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<QuizStudentsDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<QuizStudentsDto>>> Students(long quizId, CancellationToken ct)
        => OkEnvelope(await results.StudentsAsync(CurrentActor, quizId, ct));

    /// <summary>Tỉ lệ đúng của lớp theo từng di sản (chủ đề) và từng dạng câu trong riêng bài kiểm tra này, kèm mức thành thạo và cờ nên ôn (dưới 50%).</summary>
    [HttpGet("topics")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<QuizTopicStatsDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<QuizTopicStatsDto>>> Topics(long quizId, CancellationToken ct)
        => OkEnvelope(await results.TopicsAsync(CurrentActor, quizId, ct));

    /// <summary>
    /// Tab Gợi ý: di sản nên ôn (tỉ lệ sai từ 40%, ít nhất 10 câu trả lời, tối đa 5) và di sản liên quan (tối đa 5) tính riêng từ bài kiểm tra này; lọc thêm theo
    /// khoảng thời gian nộp (<c>from</c>, <c>to</c>, giờ UTC). Từ <c>suggestedHeritageIds</c> giáo viên tạo bài học ôn tập qua API tạo bài học.
    /// </summary>
    [HttpGet("recommendations")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<ClassRecommendationsDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<ClassRecommendationsDto>>> Recommendations(long quizId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
        => OkEnvelope(await recommendations.GetForQuizAsync(CurrentActor, quizId, from?.ToUniversalTime(), to?.ToUniversalTime(), ct));
}
