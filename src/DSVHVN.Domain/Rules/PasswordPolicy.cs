namespace DSVHVN.Domain.Rules;

/// <summary>Mật khẩu dài 8-64 ký tự, có ít nhất một chữ hoa, một chữ thường và một chữ số.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 64;

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
