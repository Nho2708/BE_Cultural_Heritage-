using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Attempts;
using DSVHVN.Application.Lessons;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khu học của học sinh: mở bài học bằng mã bài học (web học tập và chế độ Học sinh của app). Phải đăng nhập bằng tài khoản học sinh đã đổi
/// mật khẩu; mã chỉ có tác dụng với học sinh thuộc lớp của bài.
/// </summary>
[Route("api/v1/student/lessons")]
[Authorize(Policy = Policies.StudentReady)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
public sealed class StudentLessonsController(StudentLessonService lessons, LessonProgressService progress) : ApiControllerBase
{
    /// <summary>
    /// Mở bài học bằng mã (gõ "bh 7f3k-2q" cũng nhận). Hợp lệ khi mã tồn tại, bài đang Đã xuất bản, thuộc lớp của em. Sai mọi điều kiện đều
    /// trả cùng một thông báo 404 (không cho biết mã có ở lớp khác hay không) và tính vào giới hạn nhập sai: 10 lần mỗi tài khoản và 30 lần mỗi IP
    /// trong 10 phút, vượt thì 429. Hợp lệ thì trả bài theo khung và luồng của cấp lớp em, thẻ di sản và bài kiểm tra kèm trạng thái với em.
    /// Mở bài không ghi vào CSDL.
    /// </summary>
    [HttpGet("{code}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<StudentLessonDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<StudentLessonDto>>> Open(string code, CancellationToken ct)
        => OkEnvelope(await lessons.OpenByCodeAsync(CurrentActor, code, ct));

    /// <summary>
    /// Cấp 1: ghi em đã tới bước Nhận thẻ của bài. Kiểm mã như mở bài bằng mã (mã sai, bài chưa xuất bản hoặc khác lớp đều là 404 cùng một thông báo và tính vào
    /// giới hạn nhập sai). Gọi lại nhiều lần vẫn chỉ ghi một lần (<c>firstTime</c> chỉ bật ở lần đầu). Trả các thẻ di sản của bài đã vào album, số thẻ em đã có
    /// trên tổng số thẻ của lớp và một câu mang về nhà.
    /// </summary>
    [HttpPost("{code}/complete")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<LessonCompletionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<LessonCompletionDto>>> Complete(string code, CancellationToken ct)
        => OkEnvelope(await progress.CompleteAsync(CurrentActor, code, ct));

    /// <summary>
    /// Cấp 1, bước Nhớ lại: tối đa 3 câu ôn (Đúng/Sai hoặc Chọn hình đúng) lấy từ các bài chơi em đã nộp ở bài khác của lớp, ưu tiên câu em làm chưa đúng. Có sẵn
    /// đáp án đúng; chỉ đọc, không lưu gì, không tính sao. Kiểm mã như mở bài bằng mã. Em chưa nộp bài nào thì <c>questions</c> rỗng.
    /// </summary>
    [HttpGet("{code}/recall")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ApiResponse<StudentRecallDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<StudentRecallDto>>> Recall(string code, CancellationToken ct)
        => OkEnvelope(await progress.RecallAsync(CurrentActor, code, ct));
}
