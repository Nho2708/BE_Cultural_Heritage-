using DSVHVN.Application.Audit;
using DSVHVN.Application.Auth;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;

namespace DSVHVN.Application.Accounts;

/// <summary>
/// Khóa, mở khóa, xóa mềm một tài khoản đã được người gọi tìm đúng phạm vi: ADMIN với ORG_ADMIN,
/// ORG_ADMIN với TEACHER của trường mình. Mỗi thao tác ghi audit_logs và có hiệu lực ngay vì mọi yêu cầu
/// đều kiểm lại trạng thái tài khoản. Khóa hoặc xóa thì vô hiệu các liên kết đặt mật khẩu chưa dùng.
/// </summary>
public sealed class AccountActions(IAppDbContext db, PasswordTokenService passwordTokens, IAuditLogger audit, TimeProvider clock)
{
    public async Task LockAsync(User user, Actor actor, CancellationToken ct)
    {
        EnsureNotSelf(user, actor, "khóa tài khoản");
        if (user.Status == UserStatus.LOCKED)
            throw AppException.BusinessRule(Messages.Blocked("khóa tài khoản", "Tài khoản đang bị khóa."));

        var old = user.Status;
        user.Status = UserStatus.LOCKED;
        await passwordTokens.RevokeAllAsync(user.Id, ct);
        audit.Record(AuditAction.ACCOUNT_LOCKED, AuditTargets.Users, user.Id, actor,
            oldValue: new { status = old }, newValue: new { status = user.Status });
        await db.SaveChangesAsync(ct);
    }

    public async Task UnlockAsync(User user, Actor actor, CancellationToken ct)
    {
        if (user.Status != UserStatus.LOCKED)
            throw AppException.BusinessRule(Messages.Blocked("mở khóa tài khoản", "Tài khoản không ở trạng thái bị khóa."));

        user.Status = UserStatus.ACTIVE;
        audit.Record(AuditAction.ACCOUNT_UNLOCKED, AuditTargets.Users, user.Id, actor,
            oldValue: new { status = UserStatus.LOCKED }, newValue: new { status = user.Status });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Xóa mềm: giữ lớp, bài học, câu hỏi, quiz, kết quả của tài khoản. Email và tên đăng nhập được dùng lại
    /// cho tài khoản mới vì unique index chỉ lọc dòng chưa xóa mềm.
    /// </summary>
    public async Task SoftDeleteAsync(User user, Actor actor, CancellationToken ct)
    {
        EnsureNotSelf(user, actor, "xóa tài khoản");
        user.DeletedAt = clock.GetUtcNow().UtcDateTime;
        await passwordTokens.RevokeAllAsync(user.Id, ct);
        audit.Record(AuditAction.ACCOUNT_DELETED, AuditTargets.Users, user.Id, actor,
            oldValue: new { username = user.Username, email = user.Email, status = user.Status },
            newValue: new { deletedAt = user.DeletedAt });
        await db.SaveChangesAsync(ct);
    }

    // Không ai tự khóa hoặc tự xóa mình. Theo phạm vi vai trò trường hợp này không xảy ra, kiểm lại cho chắc.
    private static void EnsureNotSelf(User user, Actor actor, string action)
    {
        if (user.Id == actor.UserId)
            throw AppException.BusinessRule(Messages.Blocked(action, "Bạn không thể thao tác trên chính tài khoản của mình."));
    }
}
