using DSVHVN.Domain.Common;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Organizations;

namespace DSVHVN.Domain.Classes;

/// <summary>
/// Bảng <c>classes</c>: lớp học do một giáo viên phụ trách trong trường. Giáo viên tạo bài học (kèm quiz) dành riêng cho lớp;
/// học sinh của lớp mở bài bằng một mã. Khối lớp quyết định cấp học và bộ giao diện học của cả lớp.
/// </summary>
public class SchoolClass : IHasCreatedAt, IHasUpdatedAt, ISoftDeletable
{
    public const byte MinGrade = 1;
    public const byte MaxGrade = 12;

    public long Id { get; set; }

    public long OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Giáo viên phụ trách lớp.</summary>
    public long TeacherId { get; set; }
    public User? Teacher { get; set; }

    /// <summary>Tên lớp, ví dụ 8A1.</summary>
    public string? Name { get; set; }

    /// <summary>Khối lớp 1-12 (NOT NULL, CHECK). 1-5 cấp 1, 6-9 cấp 2, 10-12 cấp 3.</summary>
    public byte Grade { get; set; }

    /// <summary>Năm học dạng YYYY-YYYY, ví dụ 2026-2027.</summary>
    public string? SchoolYear { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    /// <summary>Cấp học suy từ khối lớp (không lưu): 1, 2 hoặc 3.</summary>
    public static int SchoolLevelOf(byte grade) => grade switch
    {
        <= 5 => 1,
        <= 9 => 2,
        _ => 3,
    };
}

/// <summary>
/// Bảng <c>students</c>: học sinh của lớp. Mỗi học sinh có đúng một tài khoản vai trò học sinh (<see cref="UserId"/>, không trùng)
/// do giáo viên tạo khi thêm vào lớp, và một mã học sinh (<see cref="StudentCode"/>) dùng để đăng nhập cùng mật khẩu
/// và phân biệt học sinh trùng tên.
/// </summary>
public class Student : IHasCreatedAt, IHasUpdatedAt, ISoftDeletable
{
    public long Id { get; set; }

    public long ClassId { get; set; }
    public SchoolClass? Class { get; set; }

    /// <summary>Tài khoản đăng nhập của học sinh; quan hệ một-một nhờ ràng buộc không trùng.</summary>
    public long UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Mã học sinh do hệ thống sinh (ví dụ HS8K2QX7), không trùng toàn hệ thống; cũng là tên đăng nhập.</summary>
    public string? StudentCode { get; set; }

    public string? FullName { get; set; }

    /// <summary>Học sinh còn hoạt động không (bit NOT NULL DEFAULT 1). FALSE thì không đăng nhập, không làm bài được.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
