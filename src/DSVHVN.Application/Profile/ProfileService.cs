using DSVHVN.Application.Auth;
using DSVHVN.Application.Common;
using DSVHVN.Application.Files;
using DSVHVN.Domain.Rules;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Profile;

/// <summary>Tab Thông tin của hồ sơ: họ tên, số điện thoại sửa được bằng yêu cầu này; ảnh đại diện đổi bằng cách tải ảnh lên hoặc gỡ ảnh; email, tên đăng nhập, vai trò, trường chỉ đọc.</summary>
public sealed record UpdateProfileRequest(string? FullName, string? Phone);

public sealed class UpdateProfileRequestValidator : AbstractValidator<UpdateProfileRequest>
{
    public UpdateProfileRequestValidator()
    {
        RuleFor(x => x.FullName).RequiredText("họ tên", "Họ tên", AccountRules.FullNameMaxLength);
        RuleFor(x => x.Phone).OptionalPhone();
    }
}

/// <summary>
/// Quản lý hồ sơ cá nhân trên web quản trị, dùng chung cho ba vai trò người lớn. Ảnh đại diện là ảnh tải lên kho tệp của nền tảng
/// (<see cref="IFileStorage"/>), kiểm bằng nội dung, bỏ EXIF, cắt vuông, thu nhỏ, đặt tên ngẫu nhiên; ảnh cũ được giữ lại trong kho (xóa mềm).
/// </summary>
public sealed class ProfileService(IAppDbContext db, RequestValidator validator, IFileStorage storage, IImageProcessor images)
{
    public async Task<ProfileDto> GetAsync(Actor actor, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Organization)
                       .SingleOrDefaultAsync(u => u.Id == actor.UserId && u.DeletedAt == null, ct)
                   ?? throw AppException.Unauthorized(Messages.Forbidden);
        return ProfileDto.From(user);
    }

    /// <summary>Thay họ tên và số điện thoại; để trống số điện thoại nghĩa là xóa giá trị. Không đụng tới ảnh đại diện.</summary>
    public async Task<ProfileDto> UpdateAsync(Actor actor, UpdateProfileRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var user = await db.Users.Include(u => u.Organization)
                       .SingleOrDefaultAsync(u => u.Id == actor.UserId && u.DeletedAt == null, ct)
                   ?? throw AppException.Unauthorized(Messages.Forbidden);

        user.FullName = request.FullName!.Trim();
        user.Phone = ValidationRules.TrimToNull(request.Phone);
        await db.SaveChangesAsync(ct);
        return ProfileDto.From(user);
    }

    /// <summary>Đổi ảnh đại diện bằng ảnh tải lên. Lưu tệp rồi mới ghi đường dẫn; ghi không được thì tệp vừa lưu bị gỡ lại.</summary>
    public async Task<ProfileDto> UploadAvatarAsync(Actor actor, UploadedFile? file, CancellationToken ct)
    {
        var image = UploadedImages.Process(file, images.ProcessAvatar);
        var user = await db.Users.Include(u => u.Organization)
                       .SingleOrDefaultAsync(u => u.Id == actor.UserId && u.DeletedAt == null, ct)
                   ?? throw AppException.Unauthorized(Messages.Forbidden);

        var key = StoredFileRules.ImageKey(Guid.NewGuid(), image.Extension);
        await storage.SaveAsync(key, image.Content, ct);
        try
        {
            user.AvatarUrl = storage.PublicUrl(key);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            try
            {
                await storage.DeleteAsync(key, CancellationToken.None);
            }
            catch (Exception)
            {
                // Tệp thừa không được làm mất lỗi gốc.
            }
            throw;
        }
        return ProfileDto.From(user);
    }

    /// <summary>Gỡ ảnh đại diện (về chữ cái đầu tên). Tệp ảnh cũ vẫn nằm trong kho. Chưa có ảnh thì không đổi gì.</summary>
    public async Task<ProfileDto> RemoveAvatarAsync(Actor actor, CancellationToken ct)
    {
        var user = await db.Users.Include(u => u.Organization)
                       .SingleOrDefaultAsync(u => u.Id == actor.UserId && u.DeletedAt == null, ct)
                   ?? throw AppException.Unauthorized(Messages.Forbidden);

        if (user.AvatarUrl is not null)
        {
            user.AvatarUrl = null;
            await db.SaveChangesAsync(ct);
        }
        return ProfileDto.From(user);
    }
}
