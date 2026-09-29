using System.Text.RegularExpressions;

namespace DSVHVN.Domain.Rules;

/// <summary>Hằng số nghiệp vụ về tài khoản, đăng nhập và liên kết đặt mật khẩu. Đổi giá trị ở đây là đổi quy tắc nghiệp vụ.</summary>
public static class AccountRules
{
    /// <summary>Sai 5 lần liên tiếp trong 15 phút thì tạm khóa đăng nhập 15 phút.</summary>
    public const int MaxFailedLogins = 5;

    public static readonly TimeSpan FailedLoginWindow = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan LoginLockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>Liên kết quên mật khẩu hạn 30 phút.</summary>
    public static readonly TimeSpan ForgotPasswordLinkLifetime = TimeSpan.FromMinutes(30);

    /// <summary>Liên kết đặt mật khẩu lần đầu hạn 48 giờ.</summary>
    public static readonly TimeSpan FirstPasswordLinkLifetime = TimeSpan.FromHours(48);

    /// <summary>Tối đa 3 yêu cầu quên mật khẩu mỗi giờ cho mỗi email.</summary>
    public const int MaxForgotPasswordRequestsPerHour = 3;

    // Độ dài theo cột của DBML v2.
    public const int UsernameMinLength = 3;
    public const int UsernameMaxLength = 50;
    public const int EmailMaxLength = 255;
    public const int FullNameMaxLength = 255;
    public const int PhoneMaxLength = 20;
    public const int AvatarUrlMaxLength = 500;
    public const int OrganizationNameMaxLength = 255;
    public const int OrganizationAddressMaxLength = 500;
}

/// <summary>Username 3-50 ký tự, gồm chữ không dấu, chữ số, dấu chấm, gạch dưới; so khớp không phân biệt hoa thường.</summary>
public static partial class UsernamePolicy
{
    public static bool IsSatisfiedBy(string? username)
    {
        var value = username?.Trim();
        return value is { Length: >= AccountRules.UsernameMinLength and <= AccountRules.UsernameMaxLength }
               && Allowed().IsMatch(value);
    }

    /// <summary>Lưu và so sánh ở dạng chữ thường, đã cắt khoảng trắng.</summary>
    public static string Normalize(string? username) => (username ?? string.Empty).Trim().ToLowerInvariant();

    [GeneratedRegex("^[A-Za-z0-9._]+$")]
    private static partial Regex Allowed();
}

/// <summary>Giờ Việt Nam (UTC+7, không có giờ mùa hè) để tính "hôm nay" cho ngày của gói.</summary>
public static class VietnamTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(7);

    public static DateOnly Today(DateTimeOffset utcNow) => DateOnly.FromDateTime(utcNow.ToOffset(Offset).DateTime);
}
