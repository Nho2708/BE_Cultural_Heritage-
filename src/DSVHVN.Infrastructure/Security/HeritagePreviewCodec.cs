using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DSVHVN.Application.Discovery;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DSVHVN.Infrastructure.Security;

/// <summary>Cấu hình mục <c>Preview</c>: thời hạn của liên kết xem trước bản nháp di sản.</summary>
public sealed class PreviewOptions
{
    public const string Section = "Preview";

    public const int DefaultMinutes = 30;

    /// <summary>Số phút liên kết xem trước còn dùng được kể từ lúc cấp; mặc định 30, tối đa 240.</summary>
    public int CodeMinutes { get; set; } = DefaultMinutes;
}

public sealed class PreviewOptionsValidator : IValidateOptions<PreviewOptions>
{
    public ValidateOptionsResult Validate(string? name, PreviewOptions options) =>
        options.CodeMinutes is >= 1 and <= 240
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Preview:CodeMinutes phải từ 1 đến 240 phút.");
}

/// <summary>
/// Mã xem trước ký bằng HMAC-SHA256. Khóa ký suy ra từ khóa ký JWT của máy chủ cùng một nhãn riêng (không dùng chung khóa thô với JWT), nên đổi khóa
/// JWT thì mọi mã đang có mất hiệu lực. Mã gồm <c>phần dữ liệu.chữ ký</c> (base64url): 16 byte ngẫu nhiên, mã di sản và hạn dùng (giây Unix).
/// Chữ ký so bằng phép so sánh thời gian cố định.
/// </summary>
public sealed class HeritagePreviewCodec : IHeritagePreviewCodec
{
    private const int NonceBytes = 16;
    private const int PayloadBytes = NonceBytes + sizeof(long) + sizeof(long);
    private const int MaxCodeLength = 160;

    private readonly byte[] _key;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _lifetime;

    public HeritagePreviewCodec(JwtSigningKeyProvider keys, IOptions<PreviewOptions> options, TimeProvider clock)
    {
        _key = HMACSHA256.HashData(keys.Key.Key, Encoding.UTF8.GetBytes("dsvhvn.heritage-preview.v1"));
        _clock = clock;
        _lifetime = TimeSpan.FromMinutes(options.Value.CodeMinutes);
    }

    public PreviewTicket Issue(long heritageId)
    {
        var expires = _clock.GetUtcNow().Add(_lifetime);
        var payload = new byte[PayloadBytes];
        RandomNumberGenerator.Fill(payload.AsSpan(0, NonceBytes));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(NonceBytes), heritageId);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(NonceBytes + sizeof(long)), expires.ToUnixTimeSeconds());

        var code = Base64UrlEncoder.Encode(payload) + "." + Base64UrlEncoder.Encode(HMACSHA256.HashData(_key, payload));
        return new PreviewTicket(code, heritageId, expires.UtcDateTime);
    }

    public PreviewTicket? Read(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > MaxCodeLength) return null;
        var parts = code.Split('.');
        if (parts.Length != 2) return null;

        byte[] payload, signature;
        try
        {
            payload = Base64UrlEncoder.DecodeBytes(parts[0]);
            signature = Base64UrlEncoder.DecodeBytes(parts[1]);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return null;
        }

        if (payload.Length != PayloadBytes) return null;
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_key, payload), signature)) return null;

        var heritageId = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(NonceBytes));
        var expiresAt = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(NonceBytes + sizeof(long)));
        if (_clock.GetUtcNow().ToUnixTimeSeconds() >= expiresAt) return null;
        return new PreviewTicket(code, heritageId, DateTimeOffset.FromUnixTimeSeconds(expiresAt).UtcDateTime);
    }
}
