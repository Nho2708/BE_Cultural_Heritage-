using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Classes;

/// <summary>
/// Biểu mẫu tạo và sửa lớp (màn hình Lớp học): tên lớp, khối lớp 1-12 (bắt buộc), năm học dạng YYYY-YYYY.
/// <see cref="ConfirmLevelChange"/> là lời xác nhận của giáo viên khi sửa khối lớp sang cấp khác trong lúc lớp đã có bài học.
/// </summary>
public sealed record ClassRequest(string? Name, int? Grade, string? SchoolYear, bool ConfirmLevelChange = false);

public sealed record ClassListQuery(string? Keyword, string? SchoolYear, int? Grade, int? Page, int? PageSize);

/// <summary>
/// Dòng và chi tiết lớp. Cấp học suy từ khối lớp, không lưu. Sĩ số tính học sinh đang hoạt động (chưa xóa, chưa vô hiệu).
/// </summary>
public sealed record ClassDto(
    long Id,
    string? Name,
    int Grade,
    int SchoolLevel,
    string SchoolLevelName,
    string? SchoolYear,
    int ActiveStudentCount,
    int MaxStudents,
    int LessonCount,
    int NeverLoggedInCount,
    DateTime? CreatedAt)
{
    public static ClassDto From(SchoolClass c, int activeStudents, int lessons, int neverLoggedIn)
    {
        var level = SchoolClass.SchoolLevelOf(c.Grade);
        return new ClassDto(c.Id, c.Name, c.Grade, level, SchoolLevels.NameOf(level), c.SchoolYear, activeStudents,
            ClassRules.MaxActiveStudents, lessons, neverLoggedIn, c.CreatedAt);
    }
}

public sealed record ClassPageDto(IReadOnlyList<ClassDto> Items, int Page, int PageSize, int TotalItems, int TotalPages);

/// <summary>Số liệu tổng hợp của trường cho quản trị trường: chỉ có số đếm, không có danh sách lớp hay học sinh.</summary>
public sealed record SchoolSummaryDto(int ClassCount, int ActiveStudentCount, IReadOnlyList<SchoolLevelSummaryDto> Levels);

public sealed record SchoolLevelSummaryDto(int SchoolLevel, string SchoolLevelName, int ClassCount, int ActiveStudentCount);
