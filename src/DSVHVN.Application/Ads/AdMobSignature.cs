using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace DSVHVN.Application.Ads;

/// <summary>
/// Chữ ký của xác minh phía máy chủ (server-side verification) của AdMob. AdMob gọi địa chỉ máy chủ của nền tảng bằng GET; các tham số
/// (<c>ad_network</c>, <c>ad_unit</c>, <c>custom_data</c>, <c>reward_amount</c>, <c>reward_item</c>, <c>timestamp</c>, <c>transaction_id</c>, <c>user_id</c>) đứng trước, hai tham số cuối luôn là
/// <c>signature</c> rồi <c>key_id</c>. Chữ ký là ECDSA (đường cong P-256, SHA-256, dạng DER) tính trên đúng chuỗi truy vấn thô đứng trước <c>&amp;signature=</c>,
/// mã hóa base64 an toàn cho đường dẫn (không đệm). Khóa công khai theo <c>key_id</c> lấy từ danh sách khóa của Google.
/// </summary>
public static class AdMobSignature
{
    private const string SignatureMarker = "&signature=";

    /// <summary>
    /// Phần chuỗi truy vấn thô được ký: mọi thứ đứng trước <c>&amp;signature=</c> (bỏ dấu <c>?</c> đầu). Không có <c>&amp;signature=</c> (chữ ký đứng đầu hoặc không có)
    /// thì null, vì Google luôn đặt chữ ký sau các tham số khác.
    /// </summary>
    public static string? SignedContent(string? rawQueryString)
    {
        if (string.IsNullOrEmpty(rawQueryString)) return null;
        var raw = rawQueryString[0] == '?' ? rawQueryString[1..] : rawQueryString;
        var index = raw.IndexOf(SignatureMarker, StringComparison.Ordinal);
        return index <= 0 ? null : raw[..index];
    }

    /// <summary>Kiểm chữ ký bằng khóa công khai (SubjectPublicKeyInfo, DER). Mọi lỗi định dạng (chữ ký không giải mã được, khóa hỏng) đều coi là sai chữ ký.</summary>
    public static bool Verify(string signedContent, string? signature, byte[] publicKey)
    {
        if (string.IsNullOrEmpty(signature) || signature.Length > 512) return false;
        try
        {
            var raw = Base64Url.DecodeFromChars(signature.TrimEnd('='));
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(signedContent), raw, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Ký một chuỗi truy vấn như Google (dùng cho kiểm thử và công cụ phát triển): trả chữ ký base64 an toàn cho đường dẫn, không đệm.</summary>
    public static string Sign(string signedContent, ECDsa privateKey) =>
        Base64Url.EncodeToString(privateKey.SignData(Encoding.UTF8.GetBytes(signedContent), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
}
