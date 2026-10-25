using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy tắc mật khẩu: 8-64 ký tự, có chữ hoa, chữ thường và chữ số.</summary>
public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("Abcdefg1")]                 // đúng 8 ký tự
    [InlineData("MatKhau2026")]
    [InlineData("Mật-khẩu-Việt-9")]           // chữ có dấu vẫn tính hoa/thường
    [InlineData("Aa1!@#$%^&*()")]
    public void Accepts_passwords_meeting_the_password_rule(string password) =>
        Assert.True(PasswordPolicy.IsSatisfiedBy(password));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Abcdef1")]                  // 7 ký tự
    [InlineData("abcdefg1")]                 // thiếu chữ hoa
    [InlineData("ABCDEFG1")]                 // thiếu chữ thường
    [InlineData("Abcdefgh")]                 // thiếu chữ số
    [InlineData("12345678")]
    public void Rejects_passwords_violating_the_password_rule(string? password) =>
        Assert.False(PasswordPolicy.IsSatisfiedBy(password));

    [Theory]
    [InlineData("123456")]                   // đúng 6 ký tự, chỉ chữ số
    [InlineData("abcdef")]
    [InlineData("matkhau")]
    public void Level_one_students_only_need_6_to_64_characters(string password)
    {
        Assert.True(PasswordPolicy.IsSatisfiedByLevelOne(password));
        Assert.True(PasswordPolicy.IsSatisfiedByStudent(password, 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]                    // 5 ký tự
    public void Level_one_students_are_rejected_below_6_characters(string? password) =>
        Assert.False(PasswordPolicy.IsSatisfiedByLevelOne(password));

    [Fact]
    public void Level_one_length_boundaries_are_6_and_64_and_the_other_levels_keep_the_full_rule()
    {
        Assert.True(PasswordPolicy.IsSatisfiedByLevelOne(new string('1', 64)));
        Assert.False(PasswordPolicy.IsSatisfiedByLevelOne(new string('1', 65)));
        Assert.False(PasswordPolicy.IsSatisfiedByStudent("123456", 2));
        Assert.False(PasswordPolicy.IsSatisfiedByStudent("matkhau", 3));
        Assert.True(PasswordPolicy.IsSatisfiedByStudent("MatKhau2026", 2));
    }

    [Fact]
    public void Length_boundaries_are_8_and_64()
    {
        Assert.True(PasswordPolicy.IsSatisfiedBy("Aa1" + new string('x', 61)));   // 64
        Assert.False(PasswordPolicy.IsSatisfiedBy("Aa1" + new string('x', 62)));  // 65
    }
}
