using DSVHVN.Application.Common;
using DSVHVN.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Auth;

public sealed record IssuedPasswordToken(string RawToken, DateTime ExpiresAt);

/// <summary>
/// Phát và thu hồi token trên <c>password_reset_tokens</c>, dùng chung cho hai nhánh (đặt mật khẩu lần đầu 48 giờ,
/// quên mật khẩu 30 phút). Phát token mới thì mọi token chưa dùng của tài khoản bị vô hiệu (ghi <c>used_at</c>).
/// </summary>
public sealed class PasswordTokenService(IAppDbContext db, TimeProvider clock)
{
    /// <summary>Thu hồi token cũ ngay và thêm token mới vào DbContext; token mới được lưu ở lần SaveChanges kế tiếp.</summary>
    public async Task<IssuedPasswordToken> IssueAsync(long userId, TimeSpan lifetime, CancellationToken ct)
    {
        var now = UtcNow();
        await RevokeAllAsync(userId, ct);

        var raw = SecureTokens.Create();
        var expiresAt = now + lifetime;
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = userId,
            TokenHash = SecureTokens.Hash(raw),
            ExpiresAt = expiresAt,
        });
        return new IssuedPasswordToken(raw, expiresAt);
    }

    /// <summary>Vô hiệu mọi token chưa dùng của tài khoản (khi khóa, xóa mềm, ngừng trường, đặt mật khẩu xong).</summary>
    public Task<int> RevokeAllAsync(long userId, CancellationToken ct)
    {
        var now = UtcNow();
        return db.PasswordResetTokens
            .Where(t => t.UserId == userId && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
    }

    /// <summary>Vô hiệu mọi token chưa dùng của các tài khoản thuộc một trường (ngừng trường).</summary>
    public Task<int> RevokeAllOfOrganizationAsync(long organizationId, CancellationToken ct)
    {
        var now = UtcNow();
        return db.PasswordResetTokens
            .Where(t => t.UsedAt == null && t.User!.OrganizationId == organizationId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
    }

    /// <summary>Tìm token còn dùng được theo token gốc; null nếu sai, hết hạn hoặc đã dùng.</summary>
    public async Task<PasswordResetToken?> FindUsableAsync(string rawToken, CancellationToken ct)
    {
        var hash = SecureTokens.Hash(rawToken.Trim());
        var token = await db.PasswordResetTokens.Include(t => t.User).ThenInclude(u => u!.Organization)
            .SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        return token?.User is not null && token.IsUsable(UtcNow()) ? token : null;
    }

    /// <summary>Đánh dấu đã dùng bằng cập nhật có điều kiện: hai yêu cầu song song không cùng dùng được một token.</summary>
    public async Task<bool> TryConsumeAsync(long tokenId, CancellationToken ct)
    {
        var now = UtcNow();
        var consumed = await db.PasswordResetTokens
            .Where(t => t.Id == tokenId && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        return consumed == 1;
    }

    private DateTime UtcNow() => clock.GetUtcNow().UtcDateTime;
}
