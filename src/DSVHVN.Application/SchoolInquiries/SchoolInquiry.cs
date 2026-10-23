using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DSVHVN.Application.SchoolInquiries;

/// <summary>
/// Biểu mẫu "Liên hệ để được tạo tài khoản cho trường" của trang Dành cho nhà trường (web học tập). <see cref="Website"/> là ô bẫy ẩn: người thật không thấy
/// nên không nhập; có giá trị thì coi là chương trình tự động, máy chủ trả như thành công nhưng không gửi thư.
/// </summary>
public sealed record SchoolInquiryRequest(
    string? SchoolName,
    string? Province,
    string? ContactName,
    string? Position,
    string? Email,
    string? Phone,
    string? Message,
    string? Website);

/// <summary>Thông tin liên hệ đã kiểm hợp lệ và làm sạch, đưa vào thư gửi quản trị hệ thống.</summary>
public sealed record SchoolInquiry(
    string SchoolName,
    string? Province,
    string ContactName,
    string? Position,
    string Email,
    string? Phone,
    string Message);

/// <summary>Kết quả gửi tới quản trị: số người nhận khác nhau và số thư gửi đi được.</summary>
public sealed record SchoolInquiryDelivery(int Recipients, int Delivered);

/// <summary>Gửi thông tin liên hệ của trường tới quản trị hệ thống. Thư lỗi từng người nhận được ghi log, không ném ngoại lệ.</summary>
public interface ISchoolInquiryMailer
{
    /// <param name="adminEmails">Email của các tài khoản quản trị hệ thống đang hoạt động; bản cài đặt chỉ dùng khi cấu hình chưa khai hộp thư nhận riêng.</param>
    Task<SchoolInquiryDelivery> SendAsync(SchoolInquiry inquiry, IReadOnlyCollection<string> adminEmails, CancellationToken cancellationToken = default);
}

public static class SchoolInquiryRules
{
    public const int SchoolNameMaxLength = 200;
    public const int ProvinceMaxLength = 100;
    public const int ContactNameMaxLength = 100;
    public const int PositionMaxLength = 100;
    public const int MessageMaxLength = 2000;
}

public sealed class SchoolInquiryRequestValidator : AbstractValidator<SchoolInquiryRequest>
{
    public SchoolInquiryRequestValidator()
    {
        RuleFor(x => x.SchoolName).RequiredText("tên trường", "Tên trường", SchoolInquiryRules.SchoolNameMaxLength);
        RuleFor(x => x.Province).OptionalText("Tỉnh/thành", SchoolInquiryRules.ProvinceMaxLength);
        RuleFor(x => x.ContactName).RequiredText("họ tên người liên hệ", "Họ tên người liên hệ", SchoolInquiryRules.ContactNameMaxLength);
        RuleFor(x => x.Position).OptionalText("Chức vụ", SchoolInquiryRules.PositionMaxLength);
        RuleFor(x => x.Email).RequiredEmail();
        RuleFor(x => x.Phone).OptionalPhone();
        RuleFor(x => x.Message).RequiredText("nội dung", "Nội dung", SchoolInquiryRules.MessageMaxLength);
    }
}

/// <summary>
/// Nhận liên hệ từ trang Dành cho nhà trường và chuyển thành thư gửi mọi quản trị hệ thống đang hoạt động. Không lưu CSDL, không gửi thư xác nhận về địa chỉ người nhập
/// (tránh bị lợi dụng gửi thư rác tới người khác). Không có người nhận hoặc không gửi được cho ai thì 503.
/// </summary>
public sealed class SchoolInquiryService(
    IAppDbContext db,
    RequestValidator validator,
    ISchoolInquiryMailer mailer,
    ILogger<SchoolInquiryService> logger)
{
    private const int Unavailable = 503;

    public async Task SubmitAsync(SchoolInquiryRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.Website))
        {
            logger.LogInformation("Bỏ qua biểu mẫu liên hệ của trường có ô bẫy được điền");
            return;
        }

        await validator.EnsureValidAsync(request, ct);

        var inquiry = new SchoolInquiry(
            OneLine(request.SchoolName)!,
            OneLine(request.Province),
            OneLine(request.ContactName)!,
            OneLine(request.Position),
            EmailAddress.Normalize(request.Email),
            OneLine(request.Phone),
            request.Message!.Trim());

        var admins = await db.Users
            .Where(u => u.RoleId == Roles.AdminId && u.DeletedAt == null && u.Status == UserStatus.ACTIVE && u.Email != null)
            .Select(u => u.Email!)
            .ToListAsync(ct);

        SchoolInquiryDelivery delivery;
        try
        {
            delivery = await mailer.SendAsync(inquiry, admins, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Gửi thư liên hệ của trường thất bại");
            throw new AppException(Unavailable, Messages.SchoolInquiryUnavailable);
        }

        if (delivery.Recipients == 0)
        {
            logger.LogWarning("Không có người nhận thư liên hệ của trường: danh sách SchoolInquiry:Recipients để trống và chưa có quản trị hệ thống nào có email");
            throw new AppException(Unavailable, Messages.SchoolInquiryUnavailable);
        }

        if (delivery.Delivered == 0)
        {
            logger.LogError("Thư liên hệ của trường không gửi được cho người nhận nào (tổng {Recipients})", delivery.Recipients);
            throw new AppException(Unavailable, Messages.SchoolInquiryUnavailable);
        }

        logger.LogInformation("Đã chuyển thư liên hệ của trường tới {Delivered}/{Recipients} người nhận", delivery.Delivered, delivery.Recipients);
    }

    /// <summary>Gộp mọi khoảng trắng và ký tự điều khiển thành một dấu cách để giá trị một dòng không chèn thêm dòng vào tiêu đề thư.</summary>
    private static string? OneLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => new string(p.Where(c => !char.IsControl(c)).ToArray()))
            .Where(p => p.Length > 0);
        var text = string.Join(' ', parts);
        return text.Length == 0 ? null : text;
    }
}
