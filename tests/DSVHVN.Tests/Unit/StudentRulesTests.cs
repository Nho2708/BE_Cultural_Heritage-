using DSVHVN.Application.Common;
using DSVHVN.Application.Students;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy tắc thuần của lớp và học sinh: sinh mã, mật khẩu tạm, chuẩn hóa mã, năm học, cấp học, đọc danh sách dán.</summary>
public sealed class StudentRulesTests
{
    [Fact]
    public void Code_alphabet_has_32_characters_without_ambiguous_ones()
    {
        var alphabet = StudentCredentials.CodeAlphabet;
        Assert.Equal(32, alphabet.Length);
        Assert.Equal(32, alphabet.Distinct().Count());
        Assert.DoesNotContain(alphabet, c => c is '0' or '1' or 'O' or 'I');
        Assert.Equal("23456789", new string(alphabet.Where(char.IsDigit).ToArray()));
        Assert.Equal(24, alphabet.Count(char.IsLetter));
    }

    [Fact]
    public void Student_code_is_HS_plus_six_characters_and_codes_differ()
    {
        var codes = Enumerable.Range(0, 3000).Select(_ => StudentCredentials.NewStudentCode()).ToList();
        Assert.All(codes, c => Assert.Matches("^HS[2-9A-HJ-NP-Z]{6}$", c));
        Assert.True(codes.Distinct().Count() > 2990);   // ~1,07 tỉ tổ hợp: 3000 mã gần như không trùng
        Assert.All(codes, c => Assert.True(c.Length <= AccountRules.UsernameMaxLength));
    }

    [Fact]
    public void Temporary_password_has_8_characters_meets_the_password_policy_and_avoids_ambiguous_characters()
    {
        for (var i = 0; i < 3000; i++)
        {
            var password = StudentCredentials.NewTemporaryPassword();
            Assert.Equal(8, password.Length);
            Assert.True(PasswordPolicy.IsSatisfiedBy(password), password);
            Assert.DoesNotContain(password, c => c is '0' or 'O' or 'o' or '1' or 'I' or 'l');
        }
        Assert.True(Enumerable.Range(0, 200).Select(_ => StudentCredentials.NewTemporaryPassword()).Distinct().Count() > 190);
    }

    [Fact]
    public void Simple_password_has_6_digits_and_only_level_one_uses_it()
    {
        for (var i = 0; i < 3000; i++) Assert.Matches("^[0-9]{6}$", StudentCredentials.NewSimplePassword());
        Assert.True(Enumerable.Range(0, 200).Select(_ => StudentCredentials.NewSimplePassword()).Distinct().Count() > 150);
        Assert.True(StudentCredentials.UsesSimplePassword(1));
        Assert.False(StudentCredentials.UsesSimplePassword(2));
        Assert.False(StudentCredentials.UsesSimplePassword(3));
    }

    [Theory]
    [InlineData("HS8K2QX7", "HS8K2QX7")]
    [InlineData("hs8k2qx7", "HS8K2QX7")]
    [InlineData("  hs 8k2-qx7 ", "HS8K2QX7")]
    [InlineData("bh 7f3k2q", "BH7F3K2Q")]
    [InlineData("hs–8k2‐qx7", "HS8K2QX7")]
    [InlineData(null, "")]
    public void Student_code_typed_by_users_is_normalized(string? typed, string expected) =>
        Assert.Equal(expected, StudentCredentials.NormalizeStudentCode(typed));

    [Theory]
    [InlineData("2026-2027", true)]
    [InlineData(" 2026-2027 ", true)]
    [InlineData("1999-2000", true)]
    [InlineData("2026-2028", false)]
    [InlineData("2027-2026", false)]
    [InlineData("2026-2026", false)]
    [InlineData("2026/2027", false)]
    [InlineData("26-27", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void School_year_is_two_consecutive_years(string? value, bool valid) =>
        Assert.Equal(valid, SchoolYearPolicy.IsSatisfiedBy(value));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(9, 2)]
    [InlineData(10, 3)]
    [InlineData(12, 3)]
    public void School_level_follows_the_grade(int grade, int level)
    {
        Assert.Equal(level, SchoolClass.SchoolLevelOf((byte)grade));
        Assert.Equal($"Cấp {level}", SchoolLevels.NameOf(level));
    }

    [Fact]
    public void Student_names_are_cleaned_and_compared_without_case_or_accents()
    {
        Assert.Equal("Nguyễn Văn An", StudentNames.Clean("  Nguyễn \t Văn   An \n"));
        Assert.Equal(StudentNames.Key("Nguyễn Văn An"), StudentNames.Key("nguyen van an"));
        Assert.Equal(StudentNames.Clean("Nguyễn Văn An"), StudentNames.Clean("Nguyễn Văn An"));   // dạng tổ hợp và dựng sẵn
    }

    [Fact]
    public void Pasted_text_keeps_source_line_numbers_skips_the_header_and_trims_trailing_blank_lines()
    {
        var rows = StudentListInput.Prepare(StudentListInput.FromText("Họ và tên\r\nAn\r\n\r\nBình\r\n\r\n  \r\n"));
        Assert.Equal([(2, "An"), (3, ""), (4, "Bình")], rows.Select(r => (r.RowNumber, r.Value)));

        // Chỉ dòng đầu được xét là tiêu đề; một học sinh tên "Name" ở dòng sau vẫn là học sinh.
        var later = StudentListInput.Prepare(StudentListInput.FromText("An\nname"));
        Assert.Equal(["An", "name"], later.Select(r => r.Value));
        Assert.Equal("An", StudentListInput.Prepare(StudentListInput.FromText("HỌ TÊN\nAn")).Single().Value);
    }

    [Fact]
    public void Import_needs_one_to_sixty_rows()
    {
        var empty = Assert.Throws<AppException>(() => StudentListInput.Prepare(StudentListInput.FromText("Họ và tên\n\n")));
        Assert.Equal(("REQUIRED", 400), (empty.AppMessage.Code, empty.StatusCode));
        Assert.Equal("Vui lòng nhập ít nhất một họ tên học sinh.", empty.AppMessage.Text);

        Assert.Equal(60, StudentListInput.Prepare(StudentListInput.FromText(string.Join('\n', Enumerable.Range(1, 60).Select(i => "HS " + i)))).Count);
        var many = Assert.Throws<AppException>(() =>
            StudentListInput.Prepare(StudentListInput.FromText(string.Join('\n', Enumerable.Range(1, 61).Select(i => "HS " + i)))));
        Assert.Equal("Danh sách học sinh không hợp lệ: tối đa 60 dòng mỗi lần.", many.AppMessage.Text);
    }

    [Fact]
    public void Student_messages_follow_the_catalog_wording()
    {
        Assert.Equal("Mã học sinh hoặc mật khẩu chưa đúng. Em hãy kiểm tra lại phiếu đăng nhập.", Messages.StudentInvalidCredentials.Text);
        Assert.Equal("Tài khoản của em tạm thời chưa đăng nhập được. Em hãy báo thầy cô phụ trách lớp.",
            Messages.StudentCannotSignIn(Messages.StudentBlockedReasons.AskTeacher).Text);
        Assert.Equal("Em đã nhập sai nhiều lần. Em hãy thử lại sau 15 phút hoặc nhờ thầy cô đặt lại mật khẩu.",
            Messages.StudentBlockedReasons.TemporaryLock(TimeSpan.FromMinutes(15)));
        Assert.Equal("Quên mật khẩu? Em hãy nhờ thầy cô đặt lại.", Messages.StudentForgotPasswordHint);
        Assert.Equal("Đã thêm 4 học sinh vào lớp 7B.", Messages.StudentsAdded(4, "7B", []).Text);
        Assert.Equal("Đã thêm 3 học sinh vào lớp 7B. 2 dòng bị bỏ qua: dòng 2 (trống); dòng 5 (dài quá).",
            Messages.StudentsAdded(3, "7B", ["dòng 2 (trống)", "dòng 5 (dài quá)"]).Text);
    }
}
