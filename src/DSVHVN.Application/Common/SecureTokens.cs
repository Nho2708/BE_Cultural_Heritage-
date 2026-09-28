using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace DSVHVN.Application.Common;

/// <summary>Token ngẫu nhiên cho liên kết đặt mật khẩu.</summary>
public static class SecureTokens
{
    /// <summary>32 byte ngẫu nhiên, mã hóa base64url (43 ký tự, an toàn trên URL). Token gốc chỉ đi ra thư một lần.</summary>
    public static string Create() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 của token gốc, dạng hex chữ thường (64 ký tự) — giá trị lưu ở <c>password_reset_tokens.token_hash</c>.</summary>
    public static string Hash(string rawToken) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    /// <summary>Mật khẩu ngẫu nhiên không ai biết cho tài khoản mới: chỉ băm rồi bỏ, người dùng tự đặt qua liên kết.</summary>
    public static string UnknownPassword() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(48));
}

/// <summary>
/// Dấu bảo mật gắn vào access token, suy ra từ <c>password_hash</c> (không thêm cột). Đổi hoặc đặt lại mật khẩu làm
/// dấu đổi theo, nên mọi token cũ bị từ chối ở lần kiểm mỗi yêu cầu dù chưa hết hạn 8 giờ.
/// Chỉ lấy 16 ký tự hex của SHA-256 trên chuỗi băm có muối: không suy ngược được mật khẩu.
/// </summary>
public static class SecurityStamps
{
    public static string From(string? passwordHash) => SecureTokens.Hash(passwordHash ?? string.Empty)[..16];
}

public static class EmailAddress
{
    /// <summary>Email lưu và so sánh ở dạng chữ thường, đã cắt khoảng trắng.</summary>
    public static string Normalize(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();
}
