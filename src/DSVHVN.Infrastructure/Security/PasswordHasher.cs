using DSVHVN.Application.Common;
using DSVHVN.Domain.Identity;
using Microsoft.AspNetCore.Identity;

namespace DSVHVN.Infrastructure.Security;

/// <summary>
/// PasswordHasher của ASP.NET Core Identity (định dạng phiên bản 3: PBKDF2-HMAC-SHA512, 100.000 vòng,
/// muối riêng từng mật khẩu). Chuỗi băm dài khoảng 84 ký tự, vừa cột <c>password_hash nvarchar(255)</c>.
/// </summary>
public sealed class IdentityPasswordHasher : IPasswordHasher
{
    private readonly PasswordHasher<User> _inner = new();
    private readonly Lazy<string> _dummyHash;

    public IdentityPasswordHasher()
    {
        _dummyHash = new Lazy<string>(() => _inner.HashPassword(null!, "Dummy-Passw0rd-For-Timing"));
    }

    public string Hash(string password) => _inner.HashPassword(null!, password);

    public PasswordCheck Verify(string passwordHash, string password) =>
        _inner.VerifyHashedPassword(null!, passwordHash, password) switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
            _ => PasswordCheck.Failed,
        };

    public void VerifyAgainstDummy(string password) => _inner.VerifyHashedPassword(null!, _dummyHash.Value, password);
}
