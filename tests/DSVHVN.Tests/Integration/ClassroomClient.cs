using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DSVHVN.Application.Accounts;
using DSVHVN.Application.Classes;
using DSVHVN.Application.StudentAuth;
using DSVHVN.Application.Students;

namespace DSVHVN.Tests.Integration;

/// <summary>Các bước lặp lại của luồng lớp và học sinh qua HTTP: trường có giáo viên, tạo lớp, thêm học sinh, đăng nhập học sinh.</summary>
public sealed class ClassroomClient(ApiFactory factory)
{
    public PlatformClient Platform { get; } = new(factory);
    public HttpClient Http => Platform.Http;

    public sealed record School(long OrgId, string OrgAdminToken, AccountDto Teacher, string TeacherToken);

    /// <summary>Trường mới có quản trị trường và một giáo viên đã đăng nhập.</summary>
    public async Task<School> SchoolAsync()
    {
        var (org, orgAdmin) = await Platform.OrgWithAdminAsync();
        var (teacher, token) = await Platform.TeacherAsync(orgAdmin);
        return new School(org.Id, orgAdmin, teacher, token);
    }

    /// <summary>Thêm một giáo viên nữa vào trường đã có (gói Miễn phí của kiểm thử cho 2 giáo viên).</summary>
    public async Task<(AccountDto Teacher, string Token)> AnotherTeacherAsync(School school) =>
        await Platform.TeacherAsync(school.OrgAdminToken);

    public async Task<ClassDto> CreateClassAsync(string teacherToken, string name = "8A1", int grade = 8, string schoolYear = "2026-2027")
    {
        var (res, body) = await Http.PostAsync<ClassDto>("/api/v1/teacher/classes", new { name, grade, schoolYear }, teacherToken);
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"Tạo lớp {name}: {(int)res.StatusCode} {body.Message}");
        return body.Data!;
    }

    public async Task<StudentsCreatedDto> AddStudentAsync(string teacherToken, long classId, string fullName = "Nguyễn Văn An")
    {
        var (res, body) = await Http.PostAsync<StudentsCreatedDto>($"/api/v1/teacher/classes/{classId}/students", new { fullName }, teacherToken);
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"Thêm học sinh {fullName}: {(int)res.StatusCode} {body.Message}");
        return body.Data!;
    }

    public async Task<ImportResultDto> ImportAsync(string teacherToken, long classId, IEnumerable<string> names)
    {
        var rows = names.Select((n, i) => new { rowNumber = i + 1, fullName = n }).ToList();
        var (res, body) = await Http.PostAsync<ImportResultDto>($"/api/v1/teacher/classes/{classId}/students/import", new { rows }, teacherToken);
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"Nhập danh sách: {(int)res.StatusCode} {body.Message}");
        return body.Data!;
    }

    public async Task<(HttpResponseMessage Response, Envelope<StudentSessionDto> Body)> StudentLoginRawAsync(string code, string password) =>
        await Http.PostAsync<StudentSessionDto>("/api/v1/student/auth/login", new { studentCode = code, password });

    public async Task<StudentSessionDto> StudentLoginAsync(string code, string password)
    {
        var (res, body) = await StudentLoginRawAsync(code, password);
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"Đăng nhập {code}: {(int)res.StatusCode} {body.Message}");
        return body.Data!;
    }

    /// <summary>
    /// Học sinh đã đăng nhập bằng mật khẩu trên phiếu và đổi sang mật khẩu của riêng em; trả token để gọi khu học. Lớp cấp 2, 3 bị buộc đổi ở lần đăng nhập đầu;
    /// lớp cấp 1 dùng luôn mật khẩu đơn giản nên em tự đổi (nhập mật khẩu hiện tại).
    /// </summary>
    public async Task<string> ReadyStudentTokenAsync(LoginSheetEntryDto entry, string newPassword = "MatKhauMoi2026")
    {
        var first = await StudentLoginAsync(entry.StudentCode!, entry.TemporaryPassword);
        var (res, body) = await Http.PutAsync<StudentSessionDto>("/api/v1/student/me/password",
            new { currentPassword = first.Student.MustChangePassword ? null : entry.TemporaryPassword, newPassword, confirmNewPassword = newPassword },
            first.AccessToken);
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"Đổi mật khẩu {entry.StudentCode}: {(int)res.StatusCode} {body.Message}");
        return body.Data!.AccessToken;
    }

    public async Task<(HttpResponseMessage Response, Envelope<T> Body)> PreviewFormAsync<T>(string teacherToken, long classId,
        string? text = null, (string FileName, byte[] Content)? file = null)
    {
        using var form = new MultipartFormDataContent();
        if (text is not null) form.Add(new StringContent(text, Encoding.UTF8), "text");
        if (file is { } f)
        {
            var part = new ByteArrayContent(f.Content);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "file", f.FileName);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/teacher/classes/{classId}/students/import/preview") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", teacherToken);
        var response = await Http.SendAsync(request);
        var envelope = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<Envelope<T>>(response.Content, HttpTestExtensions.Json);
        return (response, envelope!);
    }
}
