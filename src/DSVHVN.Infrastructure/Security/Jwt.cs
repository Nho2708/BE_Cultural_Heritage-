using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DSVHVN.Application.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DSVHVN.Infrastructure.Security;

/// <summary>Cấu hình mục <c>Jwt</c>. Chỉ có access token, hạn mặc định 8 giờ.</summary>
public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "dsvhvn-api";
    public string Audience { get; set; } = "dsvhvn-web";

    /// <summary>Khóa ký HMAC-SHA256, tối thiểu 32 byte. Đặt qua User Secrets hoặc biến môi trường <c>Jwt__SigningKey</c>.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 480;
}

/// <summary>Tên claim dùng chung giữa phát token và kiểm token.</summary>
public static class JwtClaimNames
{
    public const string Subject = "sub";
    public const string Username = "preferred_username";
    public const string Name = "name";

    /// <summary>Mã vai trò: ADMIN, ORG_ADMIN, TEACHER.</summary>
    public const string Role = "role";

    /// <summary>Id trường; không có với ADMIN.</summary>
    public const string Organization = "org";

    /// <summary>Dấu bảo mật suy từ password_hash: đổi mật khẩu thì token cũ bị từ chối.</summary>
    public const string SecurityStamp = "sst";

    public const string TokenId = "jti";
}

/// <summary>
/// Giữ khóa ký JWT. Development/Testing chưa cấu hình khóa thì sinh khóa ngẫu nhiên cho phiên chạy
/// (token cũ mất hiệu lực khi khởi động lại); môi trường khác bắt buộc cấu hình, thiếu thì dừng khởi động.
/// </summary>
public sealed class JwtSigningKeyProvider
{
    public JwtSigningKeyProvider(IOptions<JwtOptions> options, IHostEnvironment env, ILogger<JwtSigningKeyProvider> logger)
    {
        var configured = options.Value.SigningKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!env.IsDevelopment() && !env.IsEnvironment("Testing"))
                throw new InvalidOperationException("Thiếu cấu hình Jwt:SigningKey (đặt bằng biến môi trường Jwt__SigningKey).");

            logger.LogWarning("Chưa cấu hình Jwt:SigningKey, dùng khóa ngẫu nhiên cho phiên chạy này (chỉ Development/Testing)");
            Key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(configured);
        if (bytes.Length < 32)
            throw new InvalidOperationException("Jwt:SigningKey phải dài tối thiểu 32 byte cho HMAC-SHA256.");
        Key = new SymmetricSecurityKey(bytes);
    }

    public SymmetricSecurityKey Key { get; }
}

public sealed class JwtTokenIssuer(IOptions<JwtOptions> options, JwtSigningKeyProvider keys, TimeProvider clock) : ITokenIssuer
{
    private static readonly JsonWebTokenHandler Handler = new();

    public IssuedAccessToken CreateAccessToken(AccessTokenSubject subject)
    {
        var opt = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(opt.AccessTokenMinutes);

        var claims = new Dictionary<string, object>
        {
            [JwtClaimNames.Subject] = subject.UserId.ToString(CultureInfo.InvariantCulture),
            [JwtClaimNames.Username] = subject.Username,
            [JwtClaimNames.Name] = subject.FullName,
            [JwtClaimNames.Role] = subject.Role.ToString(),
            [JwtClaimNames.SecurityStamp] = subject.SecurityStamp,
            [JwtClaimNames.TokenId] = Guid.NewGuid().ToString("N"),
        };
        if (subject.OrganizationId is { } orgId)
            claims[JwtClaimNames.Organization] = orgId.ToString(CultureInfo.InvariantCulture);

        var token = Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = opt.Issuer,
            Audience = opt.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Claims = claims,
            SigningCredentials = new SigningCredentials(keys.Key, SecurityAlgorithms.HmacSha256),
        });

        return new IssuedAccessToken(token, expires);
    }
}
