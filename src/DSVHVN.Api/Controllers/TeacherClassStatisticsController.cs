using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Recommendations;
using DSVHVN.Application.Statistics;
using DSVHVN.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khu Giáo viên: thống kê của một lớp (màn hình Chi tiết lớp, các tab Tiến độ, Thành thạo, Gợi ý ôn tập, và Hồ sơ học tập của học sinh). Chỉ tính lượt đã nộp của
/// học sinh đang hoạt động; giáo viên luôn thấy điểm ở mọi cấp. Lớp của giáo viên khác hoặc trường khác trả 404; quản trị trường, quản trị hệ thống và học sinh nhận 403.
/// </summary>
[Route("api/v1/teacher/classes/{classId:long}")]
[Authorize(Policy = Policies.Teacher)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
public sealed class TeacherClassStatisticsController(ClassStatisticsService statistics, ClassRecommendationService recommendations) : ApiControllerBase
{
    /// <summary>
    /// Tab Tiến độ: các bài kiểm tra đã phát hành của lớp kèm tỉ lệ đã nộp, bảng học sinh (đã nộp trên đã phát hành, bài học đã hoàn thành, số sao ở lớp cấp 1, điểm trung bình,
    /// huy hiệu, lần đăng nhập gần nhất) và các ô học sinh × bài kiểm tra; tiến độ lớp và số học sinh chưa đăng nhập lần nào.
    /// </summary>
    [HttpGet("progress")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<ClassProgressDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ClassProgressDto>>> Progress(long classId, CancellationToken ct)
        => OkEnvelope(await statistics.ProgressAsync(CurrentActor, classId, ct));

    /// <summary>
    /// Tab Thành thạo: tỉ lệ đúng của cả lớp theo di sản (lọc theo <c>provinceId</c> và <c>heritageType</c>), theo tỉnh, theo loại di sản và theo dạng câu, kèm bảng nhiệt học sinh × di sản.
    /// </summary>
    [HttpGet("mastery")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<ClassMasteryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ClassMasteryDto>>> Mastery(long classId, [FromQuery] int? provinceId, [FromQuery] HeritageType? heritageType, CancellationToken ct)
        => OkEnvelope(await statistics.MasteryAsync(CurrentActor, classId, provinceId, heritageType, ct));

    /// <summary>
    /// Tab Gợi ý ôn tập: di sản nên ôn (tỉ lệ sai từ 40%, ít nhất 10 câu trả lời, tối đa 5) và di sản liên quan (tối đa 5); lọc theo bài kiểm tra (<c>quizId</c>) và khoảng
    /// thời gian nộp (<c>from</c>, <c>to</c>, giờ UTC). Từ <c>suggestedHeritageIds</c> giáo viên tạo bài học ôn tập qua API tạo bài học.
    /// </summary>
    [HttpGet("recommendations")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<ClassRecommendationsDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<ClassRecommendationsDto>>> Recommendations(
        long classId, [FromQuery] long? quizId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
        => OkEnvelope(await recommendations.GetAsync(CurrentActor, classId, quizId, from?.ToUniversalTime(), to?.ToUniversalTime(), ct));

    /// <summary>
    /// Hồ sơ học tập của một học sinh: lịch sử làm bài kèm huy hiệu từng lượt, biểu đồ điểm theo thời gian, bài học đã hoàn thành, mức thành thạo theo di sản, tỉnh và loại di sản.
    /// Học sinh của lớp khác như không tồn tại (404).
    /// </summary>
    [HttpGet("students/{studentId:long}/profile")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<StudentLearningProfileDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<StudentLearningProfileDto>>> StudentProfile(long classId, long studentId, CancellationToken ct)
        => OkEnvelope(await statistics.StudentProfileAsync(CurrentActor, classId, studentId, ct));
}
