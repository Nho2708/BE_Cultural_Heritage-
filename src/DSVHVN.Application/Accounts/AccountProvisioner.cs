using DSVHVN.Application.Audit;
using DSVHVN.Application.Auth;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Rules;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DSVHVN.Application.Accounts;

/// <summary>Biểu mẫu tài khoản: họ tên, tên đăng nhập, email, số điện thoại. Vai trò cố định theo nơi mở.</summary>
public sealed record NewAccountRequest(string? FullName, string? Username, string? Email, string? Phone);

public sealed class NewAccountRequestValidator : AbstractValidator<NewAccountRequest>
{
    public NewAccountRequestValidator()
    {
        RuleFor(x => x.FullName).RequiredText("họ tên", "Họ tên", AccountRules.FullNameMaxLength);
        RuleFor(x => x.Username).Cascade(CascadeMode.Stop)
            .RequiredField("tên đăng nhập")
            .Must(UsernamePolicy.IsSatisfiedBy)
            .WithAppMessage(Messages.Invalid("Tên đăng nhập",
                "dài 3-50 ký tự, chỉ gồm chữ không dấu, chữ số, dấu chấm, dấu gạch dưới"));
        RuleFor(x => x.Email).RequiredEmail();
        RuleFor(x => x.Phone).OptionalPhone();
    }
}

/// <summary>Dòng tài khoản ở màn hình Giáo viên của trường và Chi tiết trường (quản trị trường).</summary>
public sealed record AccountDto(
    long Id,
    string Username,
    string Email,
    string FullName,
    string? Phone,
    RoleCode Role,
    UserStatus Status,
    DateTime? LastLoginAt,
    DateTime CreatedAt)
{
    public static AccountDto From(User u) =>
        new(u.Id, u.Username, u.Email, u.FullName, u.Phone, u.RoleCode, u.Status, u.LastLoginAt, u.CreatedAt);
}

/// <summary>Tài khoản vừa tạo và liên kết đặt mật khẩu lần đầu chưa gửi (gửi sau khi giao dịch đã commit).</summary>
public sealed record ProvisionedAccount(User User, IssuedPasswordToken Token);

/// <summary>
/// Tạo tài khoản ORG_ADMIN (ADMIN làm) hoặc TEACHER (ORG_ADMIN làm):
/// mật khẩu ngẫu nhiên không ai biết, liên kết đặt mật khẩu lần đầu 48 giờ, ghi ACCOUNT_CREATED.
/// Không tự mở giao dịch: người gọi bọc cùng các bước khác (tạo trường, kiểm hạn mức) trong một giao dịch.
/// </summary>
public sealed class AccountProvisioner(
    IAppDbContext db,
    RequestValidator validator,
    IPasswordHasher hasher,
    PasswordTokenService passwordTokens,
    IAccountMailer mailer,
    IAuditLogger audit,
    ILogger<AccountProvisioner> logger)
{
    public Task ValidateAsync(NewAccountRequest request, CancellationToken ct) => validator.EnsureValidAsync(request, ct);

    /// <summary>Kiểm trùng email, tên đăng nhập trong các tài khoản chưa xóa mềm (so khớp không phân biệt hoa thường).</summary>
    public async Task EnsureUniqueAsync(NewAccountRequest request, CancellationToken ct)
    {
        var email = EmailAddress.Normalize(request.Email);
        var username = UsernamePolicy.Normalize(request.Username);
        if (await db.Users.AnyAsync(u => u.DeletedAt == null && u.Email == email, ct))
            throw AppException.Conflict("email", Messages.Taken("Email", email));
        if (await db.Users.AnyAsync(u => u.DeletedAt == null && u.Username == username, ct))
            throw AppException.Conflict("username", Messages.Taken("Tên đăng nhập", username));
    }

    /// <param name="fieldPrefix">Tiền tố tên trường cho lỗi trùng khi biểu mẫu lồng nhau (vd "orgAdmin.").</param>
    public async Task<ProvisionedAccount> CreateAsync(NewAccountRequest request, RoleCode role, long organizationId,
        Actor actor, CancellationToken ct, string fieldPrefix = "")
    {
        try
        {
            await EnsureUniqueAsync(request, ct);
        }
        catch (AppException ex) when (ex.StatusCode == 409 && fieldPrefix.Length > 0)
        {
            throw Prefixed(ex, fieldPrefix);
        }

        var user = new User
        {
            OrganizationId = organizationId,
            RoleId = Roles.IdOf(role),
            Username = UsernamePolicy.Normalize(request.Username),
            Email = EmailAddress.Normalize(request.Email),
            PasswordHash = hasher.Hash(SecureTokens.UnknownPassword()),
            FullName = request.FullName!.Trim(),
            Phone = ValidationRules.TrimToNull(request.Phone),
            Status = UserStatus.ACTIVE,
        };
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Hai yêu cầu cùng email/tên đăng nhập chạy song song: unique index có lọc chặn yêu cầu thứ hai.
            db.Users.Entry(user).State = EntityState.Detached;
            try
            {
                await EnsureUniqueAsync(request, ct);
            }
            catch (AppException ex) when (ex.StatusCode == 409)
            {
                throw fieldPrefix.Length > 0 ? Prefixed(ex, fieldPrefix) : ex;
            }
            throw;
        }

        var token = await passwordTokens.IssueAsync(user.Id, AccountRules.FirstPasswordLinkLifetime, ct);
        audit.Record(AuditAction.ACCOUNT_CREATED, AuditTargets.Users, user.Id, actor, newValue: new
        {
            username = user.Username,
            email = user.Email,
            role = role.ToString(),
            organizationId,
        });
        await db.SaveChangesAsync(ct);
        return new ProvisionedAccount(user, token);
    }

    /// <summary>Gửi thư đặt mật khẩu lần đầu; gọi sau khi giao dịch đã commit. Lỗi gửi thư chỉ ghi log.</summary>
    public async Task SendFirstPasswordLinkAsync(ProvisionedAccount account, string? organizationName, CancellationToken ct)
    {
        try
        {
            await mailer.SendFirstPasswordLinkAsync(account.User.Email, account.User.FullName, account.User.Username,
                organizationName, account.Token.RawToken, account.Token.ExpiresAt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tài khoản đã tạo; người dùng lấy liên kết mới ở nhánh quên mật khẩu.
            logger.LogError(ex, "Gửi thư đặt mật khẩu lần đầu thất bại cho tài khoản {UserId}", account.User.Id);
        }
    }

    private static AppException Prefixed(AppException ex, string prefix) =>
        new(ex.StatusCode, ex.AppMessage, ex.Errors?.ToDictionary(e => prefix + e.Key, e => e.Value));
}
