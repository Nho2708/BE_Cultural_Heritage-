using DSVHVN.Domain.Common;

namespace DSVHVN.Domain.Organizations;

/// <summary>
/// Bảng <c>organizations</c>: trường học / đơn vị dùng nền tảng. Mỗi giáo viên, lớp và gói đều thuộc một trường.
/// Ngừng trường = xóa mềm (<c>deleted_at</c>): mọi tài khoản của trường không đăng nhập được, dữ liệu giữ nguyên.
/// </summary>
public class Organization : IHasCreatedAt, IHasUpdatedAt, ISoftDeletable
{
    public long Id { get; set; }

    /// <summary>Tên trường. Bắt buộc. ORG_ADMIN không sửa được tên, chỉ ADMIN sửa.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Address { get; set; }

    public string? Phone { get; set; }

    /// <summary>Email liên hệ của đơn vị (không phải email đăng nhập).</summary>
    public string? Email { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
