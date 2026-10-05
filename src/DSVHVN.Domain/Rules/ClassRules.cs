using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DSVHVN.Domain.Rules;

/// <summary>Hằng số nghiệp vụ về lớp học, sĩ số và danh sách học sinh. Đổi giá trị ở đây là đổi quy tắc nghiệp vụ.</summary>
public static class ClassRules
{
    /// <summary>Mỗi lớp tối đa 60 học sinh đang hoạt động (chưa xóa, chưa vô hiệu).</summary>
    public const int MaxActiveStudents = 60;

    /// <summary>Mỗi lần nhập danh sách tối đa 60 dòng (giới hạn của tệp, của văn bản dán và của lần lưu).</summary>
    public const int MaxImportRows = 60;

    /// <summary>Tệp danh sách học sinh tối đa 1 MB: đủ rộng cho 60 dòng, đủ hẹp để không đọc tệp lạ quá lớn.</summary>
    public const int MaxImportFileBytes = 1024 * 1024;

    public const int NameMinLength = 1;
    public const int NameMaxLength = 100;
    public const int StudentNameMaxLength = 255;
}

/// <summary>Cấp học suy từ khối lớp: khối 1-5 cấp 1, khối 6-9 cấp 2, khối 10-12 cấp 3. Không lưu riêng.</summary>
public static class SchoolLevels
{
    public static string NameOf(int level) => level switch
    {
        1 => "Cấp 1",
        2 => "Cấp 2",
        3 => "Cấp 3",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };
}

/// <summary>Năm học dạng YYYY-YYYY gồm hai năm liên tiếp, ví dụ 2026-2027.</summary>
public static partial class SchoolYearPolicy
{
    public static bool IsSatisfiedBy(string? schoolYear)
    {
        var match = Format().Match(schoolYear?.Trim() ?? string.Empty);
        return match.Success
               && int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture)
               == int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture) + 1;
    }

    [GeneratedRegex(@"^(\d{4})-(\d{4})$")]
    private static partial Regex Format();
}

/// <summary>
/// Cách sinh mã và mật khẩu tạm bằng bộ sinh số ngẫu nhiên an toàn. Mã gồm tiền tố hai chữ cho biết loại mã và 6 ký tự lấy từ
/// bảng 32 ký tự (chữ số 2-9, chữ in hoa A-Z bỏ O và I) nên không có 0, O, 1, I dễ nhầm.
/// </summary>
public static class StudentCredentials
{
    public const string StudentCodePrefix = "HS";
    public const int CodeSuffixLength = 6;

    /// <summary>Bảng 32 ký tự của mã: chữ số 2-9 và chữ A-Z bỏ O, I.</summary>
    public const string CodeAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    public const int TemporaryPasswordLength = 8;

    // Mật khẩu tạm bỏ các ký tự dễ nhầm 0, O, o, 1, I, l để học sinh chép từ phiếu không sai.
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnpqrstuvwxyz";
    private const string Digits = "23456789";

    public static string NewStudentCode()
    {
        var suffix = new StringBuilder(CodeSuffixLength);
        for (var i = 0; i < CodeSuffixLength; i++)
            suffix.Append(CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]);
        return StudentCodePrefix + suffix;
    }

    /// <summary>
    /// 8 ký tự, luôn có ít nhất một chữ hoa, một chữ thường và một chữ số (đạt quy tắc mật khẩu): ba ký tự đầu mỗi loại một,
    /// năm ký tự còn lại lấy từ cả ba nhóm, rồi xáo trộn bằng bộ sinh số an toàn.
    /// </summary>
    public static string NewTemporaryPassword()
    {
        const string all = Upper + Lower + Digits;
        var chars = new char[TemporaryPasswordLength];
        chars[0] = Pick(Upper);
        chars[1] = Pick(Lower);
        chars[2] = Pick(Digits);
        for (var i = 3; i < chars.Length; i++) chars[i] = Pick(all);
        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }

    /// <summary>
    /// Chuẩn hóa mã do người dùng gõ: bỏ khoảng trắng và dấu gạch, đổi thành chữ hoa ("hs 8k2-qx7" thành "HS8K2QX7").
    /// </summary>
    public static string NormalizeStudentCode(string? input)
    {
        var sb = new StringBuilder(input?.Length ?? 0);
        foreach (var c in input ?? string.Empty)
        {
            if (char.IsWhiteSpace(c) || c is '-' or '–' or '—' or '‐') continue;
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}
