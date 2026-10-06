using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DSVHVN.Application.Ads;
using DSVHVN.Domain.Rules;
using Microsoft.IdentityModel.Tokens;

namespace DSVHVN.Infrastructure.Security;

/// <summary>
/// Mã giao dịch một lần của quảng cáo có thưởng trên web, ký bằng HMAC-SHA256. Khóa ký suy ra từ khóa ký JWT của máy chủ cùng một nhãn riêng (không dùng chung khóa thô với JWT),
/// nên đổi khóa JWT thì mọi mã đang có mất hiệu lực. Mã gồm <c>phần dữ liệu.chữ ký</c> (base64url): 16 byte ngẫu nhiên, mã thiết bị (16 byte), mã di sản và thời điểm cấp (giây Unix).
/// Mã không lưu bảng nào, hết hạn sau 10 phút; chữ ký so bằng phép so sánh thời gian cố định. Tính duy nhất của mã (một mã chỉ mở khóa một lần) do ràng buộc
/// <c>UX_ad_unlocks_network_transaction</c> bảo đảm khi ghi lượt mở khóa.
/// </summary>
public sealed class HmacAdRewardTokenCodec : IAdRewardTokenCodec
{
    private const int NonceBytes = 16;
    private const int DeviceBytes = 16;
    private const int PayloadBytes = NonceBytes + DeviceBytes + sizeof(long) + sizeof(long);
    private const int MaxCodeLength = 200;
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(1);

    private readonly byte[] _key;
    private readonly TimeProvider _clock;

    public HmacAdRewardTokenCodec(JwtSigningKeyProvider keys, TimeProvider clock)
    {
        _key = HMACSHA256.HashData(keys.Key.Key, Encoding.UTF8.GetBytes("dsvhvn.ad-reward.v1"));
        _clock = clock;
    }

    public AdRewardTicket Issue(string deviceKey, long heritageId)
    {
        var device = Guid.Parse(deviceKey);
        var issued = _clock.GetUtcNow();
        var payload = new byte[PayloadBytes];
        RandomNumberGenerator.Fill(payload.AsSpan(0, NonceBytes));
        device.ToByteArray().CopyTo(payload.AsSpan(NonceBytes));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(NonceBytes + DeviceBytes), heritageId);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(NonceBytes + DeviceBytes + sizeof(long)), issued.ToUnixTimeSeconds());

        var code = Base64UrlEncoder.Encode(payload) + "." + Base64UrlEncoder.Encode(HMACSHA256.HashData(_key, payload));
        return new AdRewardTicket(code, device.ToString("D"), heritageId, issued.UtcDateTime, issued.Add(AdRules.WebTransactionLifetime).UtcDateTime);
    }

    public AdRewardTicket? Read(string? code)
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

        var device = new Guid(payload.AsSpan(NonceBytes, DeviceBytes));
        var heritageId = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(NonceBytes + DeviceBytes));
        var issued = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(NonceBytes + DeviceBytes + sizeof(long))));
        var now = _clock.GetUtcNow();
        if (issued > now + FutureTolerance) return null;
        var expires = issued.Add(AdRules.WebTransactionLifetime);
        if (now >= expires) return null;
        return new AdRewardTicket(code, device.ToString("D"), heritageId, issued.UtcDateTime, expires.UtcDateTime);
    }
}
