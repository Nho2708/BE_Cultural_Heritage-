namespace DSVHVN.Domain.Rules;

/// <summary>
/// Mật khẩu dài 8-64 ký tự, có ít nhất một chữ hoa, một chữ thường và một chữ số. Học sinh cấp 1 dùng quy tắc nhẹ hơn: chỉ cần dài 6-64 ký tự
/// (<see cref="IsSatisfiedByLevelOne"/>).
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 64;

    /// <summary>Độ dài tối thiểu của mật khẩu học sinh cấp 1 (bằng độ dài mật khẩu đơn giản hệ thống sinh).</summary>
    public const int LevelOneMinLength = 6;

    /// <summary>Mật khẩu của học sinh cấp 1: chỉ cần dài 6-64 ký tự.</summary>
    public static bool IsSatisfiedByLevelOne(string? password) =>
        !string.IsNullOrEmpty(password) && password.Length is >= LevelOneMinLength and <= MaxLength;

    /// <summary>Quy tắc mật khẩu của học sinh theo cấp học: cấp 1 nhẹ hơn, cấp 2 và 3 theo quy tắc chung.</summary>
    public static bool IsSatisfiedByStudent(string? password, int level) =>
        level == 1 ? IsSatisfiedByLevelOne(password) : IsSatisfiedBy(password);

    public static bool IsSatisfiedBy(string? password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        if (password.Length is < MinLength or > MaxLength) return false;

        bool upper = false, lower = false, digit = false;
        foreach (var c in password)
        {
            if (char.IsUpper(c)) upper = true;
            else if (char.IsLower(c)) lower = true;
            else if (char.IsDigit(c)) digit = true;
        }
        return upper && lower && digit;
    }
}
