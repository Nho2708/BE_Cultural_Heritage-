using DSVHVN.Domain.Classes;

namespace DSVHVN.Application.Students;

/// <summary>Cấu hình mục <c>App</c> dùng cho phiếu đăng nhập: địa chỉ web học tập và đường dẫn tải app.</summary>
public sealed class StudentSheetOptions
{
    public const string Section = "App";

    /// <summary>Địa chỉ gốc của web học tập (khu học của học sinh).</summary>
    public string LearningWebBaseUrl { get; set; } = "http://localhost:5174";

    /// <summary>Route màn hình đăng nhập học sinh trên web học tập.</summary>
    public string LearningLoginPath { get; set; } = "/dang-nhap";

    /// <summary>Đường dẫn tải tệp cài app mobile (tệp APK cho Android); để trống thì phiếu chỉ hướng dẫn chung.</summary>
    public string? MobileAppDownloadUrl { get; set; }
}

/// <summary>Dòng học sinh ở tab Học sinh của chi tiết lớp. <see cref="PasswordChanged"/> là đã đổi mật khẩu lần đầu chưa.</summary>
public sealed record StudentDto(
    long Id,
    string? StudentCode,
    string? FullName,
    bool IsActive,
    DateTime? LastLoginAt,
    bool PasswordChanged,
    DateTime? CreatedAt)
{
    public static StudentDto From(Student s) => new(s.Id, s.StudentCode, s.FullName, s.IsActive, s.User?.LastLoginAt,
        s.User is { MustChangePassword: false }, s.CreatedAt);
}

/// <summary><see cref="Status"/>: <c>active</c> (mặc định tất cả còn trong lớp), <c>inactive</c>, hoặc <c>all</c>.</summary>
public sealed record StudentListQuery(string? Keyword, string? Status, int? Page, int? PageSize);

public sealed record StudentPageDto(
    IReadOnlyList<StudentDto> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages,
    int ActiveCount,
    int MaxStudents);

/// <summary>Thêm từng em: chỉ nhập họ tên.</summary>
public sealed record AddStudentRequest(string? FullName);

/// <summary>Sửa họ tên tại chỗ. Mã học sinh và lớp không sửa được.</summary>
public sealed record RenameStudentRequest(string? FullName);

/// <summary>Một dòng của danh sách nhập; <see cref="RowNumber"/> là số dòng trong tệp hoặc văn bản gốc (dòng 1 là dòng đầu).</summary>
public sealed record ImportRowRequest(int? RowNumber, string? FullName);

public sealed record ImportStudentsRequest(IReadOnlyList<ImportRowRequest>? Rows);

public sealed record ResetPasswordsRequest(IReadOnlyList<long>? StudentIds);

/// <summary>Một phiếu: mật khẩu tạm chỉ có ở đây, chỉ hiện đúng một lần ngay sau khi tạo hoặc đặt lại, không lưu bản gốc.</summary>
public sealed record LoginSheetEntryDto(
    long StudentId,
    string? FullName,
    string? ClassName,
    string? StudentCode,
    string TemporaryPassword);

/// <summary>
/// Dữ liệu màn hình Phiếu đăng nhập của giáo viên (in nhiều phiếu mỗi trang A4 hoặc tải về): các câu chung của mọi phiếu
/// và từng phiếu của mỗi học sinh. <see cref="PasswordIsTemporary"/> bật với lớp cấp 2, 3 (mật khẩu tạm, em phải đổi ở lần đăng nhập đầu) và tắt với lớp cấp 1
/// (mật khẩu đơn giản 6 chữ số em dùng luôn); giao diện gọi là "Mật khẩu" thay cho "Mật khẩu tạm" khi tắt. Phiếu không chứa thông tin nào khác của học sinh.
/// </summary>
public sealed record LoginSheetDto(
    string? ClassName,
    string LoginUrl,
    string? AppDownloadUrl,
    IReadOnlyList<string> Instructions,
    string FirstLoginReminder,
    string Notice,
    bool PasswordIsTemporary,
    IReadOnlyList<LoginSheetEntryDto> Entries);

/// <summary>Kết quả thêm từng em: học sinh vừa tạo và phiếu đăng nhập.</summary>
public sealed record StudentsCreatedDto(IReadOnlyList<StudentDto> Students, LoginSheetDto Sheet);

public sealed record ImportPreviewRowDto(
    int RowNumber,
    string? FullName,
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

/// <summary>Bước xem trước: từng dòng hợp lệ hay lỗi kèm lý do, cảnh báo trùng họ tên, số chỗ còn lại của lớp.</summary>
public sealed record ImportPreviewDto(
    IReadOnlyList<ImportPreviewRowDto> Rows,
    int TotalRows,
    int ValidCount,
    int ErrorCount,
    int WarningCount,
    int RemainingCapacity,
    int MaxStudents);

public sealed record ImportSkippedRowDto(int RowNumber, string? FullName, IReadOnlyList<string> Errors);

/// <summary>Kết quả lưu danh sách: các dòng hợp lệ đã lưu trong một giao dịch, dòng lỗi bị bỏ qua và báo đủ.</summary>
public sealed record ImportResultDto(
    int AddedCount,
    int SkippedCount,
    IReadOnlyList<ImportSkippedRowDto> Skipped,
    string Message,
    StudentsCreatedDto Created);

/// <summary>Một học sinh vừa tạo cùng mật khẩu tạm gốc (chỉ tồn tại trong bộ nhớ lúc tạo).</summary>
public sealed record NewStudentAccount(Student Student, string TemporaryPassword);
