using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application.Common;
using DSVHVN.Application.Students;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSVHVN.Api.Controllers;

/// <summary>
/// Khu Giáo viên: tab Học sinh trong chi tiết lớp và các hộp thoại Thêm học sinh, Phiếu đăng nhập. Chỉ giáo viên phụ trách lớp
/// thao tác được; học sinh của lớp khác trả 404. Quản trị trường và quản trị hệ thống nhận 403 (không xem lớp, học sinh).
/// Mật khẩu tạm chỉ xuất hiện trong phản hồi tạo hoặc đặt lại, đúng một lần.
/// </summary>
[Route("api/v1/teacher/classes/{classId:long}/students")]
[Authorize(Policy = Policies.Teacher)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ApiResponse<object>>(StatusCodes.Status404NotFound)]
public sealed class TeacherStudentsController(StudentService students, StudentImportService imports) : ApiControllerBase
{
    /// <summary>
    /// Học sinh của lớp: mã, họ tên, trạng thái, lần đăng nhập gần nhất, đã đổi mật khẩu lần đầu chưa. Lọc <c>status</c> = active,
    /// inactive hoặc all (mặc định); tìm theo họ tên hoặc mã học sinh.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<StudentPageDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<StudentPageDto>>> List(
        long classId, [FromQuery] string? keyword, [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize,
        CancellationToken ct)
        => OkEnvelope(await students.ListAsync(CurrentActor, classId, new StudentListQuery(keyword, status, page, pageSize), ct));

    /// <summary>Thêm từng em: tạo mã học sinh, mật khẩu tạm và tài khoản; trả phiếu đăng nhập (mật khẩu tạm chỉ hiện lần này).</summary>
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [HttpPost]
    [ProducesResponseType<ApiResponse<StudentsCreatedDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<StudentsCreatedDto>>> Add(long classId, AddStudentRequest request, CancellationToken ct)
        => CreatedEnvelope(await students.AddAsync(CurrentActor, classId, request, ct), Messages.Success("Thêm học sinh").Text);

    /// <summary>Tệp mẫu danh sách học sinh (một cột "Họ và tên"): <c>format</c> = xlsx (mặc định) hoặc csv (UTF-8 có BOM).</summary>
    [HttpGet("import/template")]
    [Produces("application/octet-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Template(long classId, [FromQuery] string? format)
    {
        var parsed = (format ?? "xlsx").Trim().ToLowerInvariant() switch
        {
            "xlsx" => StudentListFormat.Xlsx,
            "csv" => StudentListFormat.Csv,
            _ => throw AppException.Validation("format", Messages.Invalid("Định dạng tệp mẫu", "chỉ nhận xlsx hoặc csv")),
        };
        var file = imports.Template(parsed);
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>
    /// Bước xem trước nhập danh sách: gửi <c>file</c> (.xlsx hoặc .csv UTF-8, tối đa 1 MB) hoặc <c>text</c> (dán, mỗi dòng một họ tên),
    /// tối đa 60 dòng. Báo từng dòng hợp lệ hay lỗi kèm lý do, cảnh báo trùng họ tên. Chưa lưu gì.
    /// </summary>
    [HttpPost("import/preview")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    [ProducesResponseType<ApiResponse<ImportPreviewDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<ImportPreviewDto>>> Preview(long classId, [FromForm] StudentImportForm form, CancellationToken ct)
    {
        if (form.File is not null && !string.IsNullOrWhiteSpace(form.Text))
            throw AppException.Validation("file", Messages.Invalid("Danh sách học sinh", "chỉ chọn một trong hai: tệp hoặc văn bản dán"));
        if (form.File is not null)
        {
            await using var content = form.File.OpenReadStream();
            return OkEnvelope(await imports.PreviewFileAsync(CurrentActor, classId, content, form.File.FileName, ct));
        }
        return OkEnvelope(await imports.PreviewTextAsync(CurrentActor, classId, form.Text, ct));
    }

    /// <summary>
    /// Lưu danh sách sau khi xem trước: gửi lại các dòng (số dòng và họ tên). Dòng hợp lệ được lưu trong một giao dịch,
    /// dòng lỗi bị bỏ qua và báo đủ; trả phiếu đăng nhập của các em vừa thêm.
    /// </summary>
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [HttpPost("import")]
    [ProducesResponseType<ApiResponse<ImportResultDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<ImportResultDto>>> Import(long classId, ImportStudentsRequest request, CancellationToken ct)
    {
        var result = await imports.ImportAsync(CurrentActor, classId, request, ct);
        return CreatedEnvelope(result, result.Message);
    }

    /// <summary>Đặt lại mật khẩu một hoặc nhiều học sinh đang hoạt động của lớp; trả phiếu đăng nhập mới (mật khẩu tạm chỉ hiện lần này).</summary>
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [HttpPost("reset-password")]
    [ProducesResponseType<ApiResponse<LoginSheetDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<LoginSheetDto>>> ResetPasswords(long classId, ResetPasswordsRequest request, CancellationToken ct)
        => OkEnvelope(await students.ResetPasswordsAsync(CurrentActor, classId, request, ct), Messages.Success("Đặt lại mật khẩu").Text);

    /// <summary>Sửa họ tên tại chỗ; mã học sinh và lớp không sửa được.</summary>
    [HttpPut("{studentId:long}")]
    [ProducesResponseType<ApiResponse<StudentDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<StudentDto>>> Rename(long classId, long studentId, RenameStudentRequest request, CancellationToken ct)
        => OkEnvelope(await students.RenameAsync(CurrentActor, classId, studentId, request, ct), Messages.Success("Lưu học sinh").Text);

    [HttpPost("{studentId:long}/deactivate")]
    [ProducesResponseType<ApiResponse<StudentDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<StudentDto>>> Deactivate(long classId, long studentId, CancellationToken ct)
        => OkEnvelope(await students.DeactivateAsync(CurrentActor, classId, studentId, ct), Messages.Success("Vô hiệu học sinh").Text);

    [HttpPost("{studentId:long}/reactivate")]
    [ProducesResponseType<ApiResponse<StudentDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<StudentDto>>> Reactivate(long classId, long studentId, CancellationToken ct)
        => OkEnvelope(await students.ReactivateAsync(CurrentActor, classId, studentId, ct), Messages.Success("Mở lại học sinh").Text);

    /// <summary>Xóa mềm học sinh cùng tài khoản; không mở lại được, mã học sinh không cấp lại.</summary>
    [HttpDelete("{studentId:long}")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(long classId, long studentId, CancellationToken ct)
    {
        await students.DeleteAsync(CurrentActor, classId, studentId, ct);
        return OkEnvelope<object>(null, Messages.Success("Xóa học sinh").Text);
    }
}

/// <summary>Biểu mẫu xem trước nhập danh sách: một tệp hoặc một khối văn bản dán.</summary>
public sealed class StudentImportForm
{
    public IFormFile? File { get; set; }

    public string? Text { get; set; }
}
