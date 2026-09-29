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

    [Fact]
    public void Length_boundaries_are_8_and_64()
    {
        Assert.True(PasswordPolicy.IsSatisfiedBy("Aa1" + new string('x', 61)));   // 64
        Assert.False(PasswordPolicy.IsSatisfiedBy("Aa1" + new string('x', 62)));  // 65
    }
}
