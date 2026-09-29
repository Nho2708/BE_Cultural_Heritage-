using DSVHVN.Application.Auth;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Rules;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Profile;

/// <summary>Tab Thông tin của hồ sơ: họ tên, số điện thoại, ảnh đại diện sửa được; email, tên đăng nhập, vai trò, trường chỉ đọc.</summary>
public sealed record UpdateProfileRequest(string? FullName, string? Phone, string? AvatarUrl);

public sealed class UpdateProfileRequestValidator : AbstractValidator<UpdateProfileRequest>
{
    public UpdateProfileRequestValidator()
    {
        RuleFor(x => x.FullName).RequiredText("họ tên", "Họ tên", AccountRules.FullNameMaxLength);
        RuleFor(x => x.Phone).OptionalPhone();
        RuleFor(x => x.AvatarUrl)
            .Must(v => string.IsNullOrWhiteSpace(v)
                       || (v.Trim().Length <= AccountRules.AvatarUrlMaxLength && ValidationRules.IsHttpUrl(v)))
            .WithAppMessage(Messages.Invalid("Ảnh đại diện",
                $"phải là đường dẫn http hoặc https, tối đa {AccountRules.AvatarUrlMaxLength} ký tự"));
    }
}

/// <summary>Quản lý hồ sơ cá nhân, dùng chung cho 3 vai trò.</summary>
public sealed class ProfileService(IAppDbContext db, RequestValidator validator)
{
    public async Task<ProfileDto> GetAsync(Actor actor, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Organization)
                       .SingleOrDefaultAsync(u => u.Id == actor.UserId && u.DeletedAt == null, ct)
                   ?? throw AppException.Unauthorized(Messages.Forbidden);
        return ProfileDto.From(user);
    }

    /// <summary>Thay cả ba trường sửa được; để trống số điện thoại hoặc ảnh đại diện nghĩa là xóa giá trị.</summary>
    public async Task<ProfileDto> UpdateAsync(Actor actor, UpdateProfileRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var user = await db.Users.Include(u => u.Organization)
                       .SingleOrDefaultAsync(u => u.Id == actor.UserId && u.DeletedAt == null, ct)
                   ?? throw AppException.Unauthorized(Messages.Forbidden);

        user.FullName = request.FullName!.Trim();
        user.Phone = ValidationRules.TrimToNull(request.Phone);
        user.AvatarUrl = ValidationRules.TrimToNull(request.AvatarUrl);
        await db.SaveChangesAsync(ct);
        return ProfileDto.From(user);
    }
}
