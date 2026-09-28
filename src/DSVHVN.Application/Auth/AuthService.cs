using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DSVHVN.Application.Auth;

/// <summary>Đăng nhập, đặt mật khẩu qua email (hai nhánh) và đổi mật khẩu trong hồ sơ.</summary>
public sealed class AuthService(
    IAppDbContext db,
    RequestValidator validator,
    IPasswordHasher hasher,
    ITokenIssuer tokenIssuer,
    IAuthThrottle throttle,
    PasswordTokenService passwordTokens,
    IAccountMailer mailer,
    IAuditLogger audit,
    TimeProvider clock,
    ILogger<AuthService> logger)
{
    /// <summary>
    /// Một ô nhận email hoặc username. Sai thông tin luôn trả thông điệp sai thông tin đăng nhập; sai 5 lần trong 15 phút thì
    /// tạm khóa 15 phút (không đăng nhập được, ghi LOGIN_LOCKED_OUT). Đúng mật khẩu mới xét trạng thái tài khoản và trường,
    /// để người đoán mật khẩu không biết tài khoản bị khóa. Thành công: ghi <c>last_login_at</c> và LOGIN.
    /// </summary>
    public async Task<LoginResultDto> LoginAsync(LoginRequest request, string? ipAddress, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var identifier = request.EmailOrUsername!.Trim().ToLowerInvariant();

        // Username không chứa '@' nên có '@' là email.
        var users = db.Users.Include(u => u.Organization).Where(u => u.DeletedAt == null);
        var user = identifier.Contains('@')
            ? await users.SingleOrDefaultAsync(u => u.Email == identifier, ct)
            : await users.SingleOrDefaultAsync(u => u.Username == identifier, ct);

        // Đếm theo tài khoản khi tìm thấy: xen kẽ email và username của cùng một tài khoản không được thêm lượt đoán.
        var throttleKey = user is null ? "login:" + identifier : "user:" + user.Id;
        if (throttle.GetLoginLockout(throttleKey) is { } remaining)
            throw AppException.Forbidden(Messages.CannotSignIn(Messages.SignInBlockedReasons.TemporaryLock(remaining)));

        var check = PasswordCheck.Failed;
        if (user?.PasswordHash is { } passwordHash) check = hasher.Verify(passwordHash, request.Password!);
        else hasher.VerifyAgainstDummy(request.Password!);

        if (user is null || check == PasswordCheck.Failed)
        {
            if (!throttle.RegisterFailedLogin(throttleKey)) throw AppException.Unauthorized(Messages.InvalidCredentials);

            if (user is not null)
            {
                audit.Record(AuditAction.LOGIN_LOCKED_OUT, AuditTargets.Users, user.Id, ActorOf(user, ipAddress),
                    newValue: new { lockedMinutes = (int)AccountRules.LoginLockoutDuration.TotalMinutes });
                await db.SaveChangesAsync(ct);
            }
            throw AppException.Forbidden(Messages.CannotSignIn(
                Messages.SignInBlockedReasons.TemporaryLock(AccountRules.LoginLockoutDuration)));
        }

        if (AccountAccess.BlockedReason(user) is { } reason)
            throw AppException.Forbidden(Messages.CannotSignIn(reason));

        throttle.ResetFailedLogins(throttleKey);
        if (check == PasswordCheck.SuccessRehashNeeded) user.PasswordHash = hasher.Hash(request.Password!);
        user.LastLoginAt = UtcNow();
        audit.Record(AuditAction.LOGIN, AuditTargets.Users, user.Id, ActorOf(user, ipAddress));
        await db.SaveChangesAsync(ct);

        var token = IssueAccessToken(user);
        return new LoginResultDto(token.TokenType, token.AccessToken, token.ExpiresAt, ProfileDto.From(user));
    }

    /// <summary>
    /// Nhánh quên mật khẩu: luôn trả cùng một câu (kể cả email không có, tài khoản bị khóa, vượt 3 yêu cầu/giờ).
    /// Liên kết mới hạn 30 phút và vô hiệu mọi liên kết cũ chưa dùng.
    /// </summary>
    public async Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var email = EmailAddress.Normalize(request.Email);

        if (!throttle.TryConsumeForgotPasswordRequest(email))
        {
            logger.LogWarning("Vượt giới hạn 3 yêu cầu quên mật khẩu mỗi giờ cho một email");
            return;
        }

        var user = await db.Users.Include(u => u.Organization)
            .SingleOrDefaultAsync(u => u.Email == email && u.DeletedAt == null, ct);
        if (user is null || AccountAccess.BlockedReason(user) is not null) return;

        var token = await passwordTokens.IssueAsync(user.Id, AccountRules.ForgotPasswordLinkLifetime, ct);
        await db.SaveChangesAsync(ct);

        try
        {
            await mailer.SendResetPasswordLinkAsync(email, user.FullName ?? string.Empty, token.RawToken, token.ExpiresAt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Không đổi câu trả lời: người gọi không được biết email có thuộc tài khoản nào hay không.
            logger.LogError(ex, "Gửi thư đặt lại mật khẩu thất bại cho tài khoản {UserId}", user.Id);
        }
    }

    /// <summary>Màn hình Đặt mật khẩu: kiểm liên kết trước khi hiện ô nhập mật khẩu. Hết hạn, đã dùng, bị thay → 422.</summary>
    public async Task<PasswordTokenInfoDto> CheckPasswordTokenAsync(PasswordTokenRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var token = await FindUsableTokenAsync(request.Token!, ct);
        return new PasswordTokenInfoDto(token.User!.Username, token.ExpiresAt);
    }

    /// <summary>
    /// Đặt mật khẩu bằng liên kết trong thư — cùng một thao tác cho tài khoản mới (48 giờ) và
    /// quên mật khẩu (30 phút). Token dùng một lần; đặt xong không tự đăng nhập; mọi phiên cũ mất hiệu lực (dấu bảo mật đổi).
    /// </summary>
    public async Task SetPasswordAsync(SetPasswordRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var token = await FindUsableTokenAsync(request.Token!, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await passwordTokens.TryConsumeAsync(token.Id, ct))
            throw AppException.BusinessRule(Messages.PasswordLinkInvalid);

        var user = token.User!;
        user.PasswordHash = hasher.Hash(request.NewPassword!);
        await db.SaveChangesAsync(ct);
        await passwordTokens.RevokeAllAsync(user.Id, ct);
        await tx.CommitAsync(ct);

        throttle.ResetFailedLogins("user:" + user.Id);
    }

    /// <summary>
    /// Tab Đổi mật khẩu của hồ sơ: phải nhập đúng mật khẩu hiện tại. Dấu bảo mật đổi nên mọi token cũ bị từ chối;
    /// phiên đang dùng nhận access token mới.
    /// </summary>
    public async Task<AccessTokenDto> ChangePasswordAsync(Actor actor, ChangePasswordRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == actor.UserId && u.DeletedAt == null, ct)
                   ?? throw AppException.Unauthorized(Messages.Forbidden);

        if (user.PasswordHash is null || hasher.Verify(user.PasswordHash, request.CurrentPassword!) == PasswordCheck.Failed)
            throw AppException.Validation("currentPassword",
                Messages.Invalid("Mật khẩu hiện tại", "không khớp với mật khẩu đang dùng"));

        user.PasswordHash = hasher.Hash(request.NewPassword!);
        await db.SaveChangesAsync(ct);
        return IssueAccessToken(user);
    }

    private async Task<PasswordResetToken> FindUsableTokenAsync(string rawToken, CancellationToken ct)
    {
        var token = await passwordTokens.FindUsableAsync(rawToken, ct);
        // Tài khoản đã xóa mềm, bị khóa, ngừng dùng hoặc thuộc trường đã ngừng: liên kết không còn dùng được.
        if (token?.User is null || token.User.DeletedAt is not null || AccountAccess.BlockedReason(token.User) is not null)
            throw AppException.BusinessRule(Messages.PasswordLinkInvalid);
        return token;
    }

    private AccessTokenDto IssueAccessToken(User user)
    {
        var issued = tokenIssuer.CreateAccessToken(new AccessTokenSubject(
            user.Id, user.Username ?? string.Empty, user.FullName ?? string.Empty, user.RoleCode, user.OrganizationId,
            SecurityStamps.From(user.PasswordHash)));
        return new AccessTokenDto("Bearer", issued.Token, issued.ExpiresAt);
    }

    private static Actor ActorOf(User user, string? ipAddress) => new(user.Id, user.RoleCode, user.OrganizationId, ipAddress);

    private DateTime UtcNow() => clock.GetUtcNow().UtcDateTime;
}
