using DSVHVN.Domain.Common;

namespace DSVHVN.Domain.Identity;

/// <summary>
/// Bảng <c>password_reset_tokens</c>: hai nhánh trên cùng bảng — đặt mật khẩu lần đầu cho tài khoản mới
/// (hạn 48 giờ) và quên mật khẩu (hạn 30 phút). Chỉ lưu SHA-256 dạng hex chữ thường của token; dùng một lần.
/// Bảng không có <c>updated_at</c>; vô hiệu một token = ghi <c>used_at</c>.
/// </summary>
public class PasswordResetToken : IHasCreatedAt
{
    public long Id { get; set; }

    public long UserId { get; set; }
    public User? User { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    /// <summary>Khác NULL: đã dùng hoặc đã bị thay bằng token mới, không dùng lại được.</summary>
    public DateTime? UsedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool IsUsable(DateTime utcNow) => UsedAt is null && ExpiresAt > utcNow;
}
