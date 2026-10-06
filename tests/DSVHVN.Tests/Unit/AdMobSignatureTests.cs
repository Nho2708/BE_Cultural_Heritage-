using System.Security.Cryptography;
using DSVHVN.Application.Ads;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Chữ ký xác minh phía máy chủ của AdMob: tách đúng phần chuỗi truy vấn thô được ký, ký bằng khóa tự sinh (đường cong P-256, SHA-256, DER, base64 an toàn cho đường dẫn)
/// rồi kiểm lại bằng khóa công khai; mọi sửa đổi, khóa sai và chữ ký hỏng đều bị từ chối.
/// </summary>
public sealed class AdMobSignatureTests
{
    private const string Content =
        "ad_network=5450213213286189855&ad_unit=5224354917&custom_data=42&reward_amount=1&reward_item=summary&timestamp=1759000000000&transaction_id=ABC123&user_id=3f2b8c1e-9a47-4d6b-8e10-5c7a9b2d4f61";

    private static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public void The_signed_part_is_everything_before_the_signature_parameter()
    {
        var url = Content + "&signature=MEUCIQ&key_id=3335741209";
        Assert.Equal(Content, AdMobSignature.SignedContent(url));
        Assert.Equal(Content, AdMobSignature.SignedContent("?" + url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("?")]
    [InlineData("signature=abc&key_id=1")]                     // chữ ký đứng đầu: Google không bao giờ gửi như vậy
    [InlineData("?signature=abc&key_id=1")]
    [InlineData("ad_unit=1&user_id=2")]                        // không có chữ ký
    [InlineData("ad_unit=1&xsignature=abc&key_id=1")]          // tham số khác chỉ có đuôi giống
    public void A_query_without_a_trailing_signature_has_nothing_to_verify(string? query) =>
        Assert.Null(AdMobSignature.SignedContent(query));

    [Fact]
    public void A_signature_made_by_the_private_key_verifies_with_the_public_key()
    {
        using var key = NewKey();
        var signature = AdMobSignature.Sign(Content, key);

        // Dạng base64 an toàn cho đường dẫn, không đệm.
        Assert.DoesNotContain('+', signature);
        Assert.DoesNotContain('/', signature);
        Assert.DoesNotContain('=', signature);
        Assert.True(AdMobSignature.Verify(Content, signature, key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void A_signature_padded_with_equals_signs_still_verifies()
    {
        using var key = NewKey();
        var signature = AdMobSignature.Sign(Content, key);
        var padded = signature + new string('=', (4 - signature.Length % 4) % 4);
        Assert.True(AdMobSignature.Verify(Content, padded, key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void Changing_any_part_of_the_content_breaks_the_signature()
    {
        using var key = NewKey();
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var signature = AdMobSignature.Sign(Content, key);

        Assert.False(AdMobSignature.Verify(Content.Replace("custom_data=42", "custom_data=43"), signature, publicKey));
        Assert.False(AdMobSignature.Verify(Content.Replace("ABC123", "ABC124"), signature, publicKey));
        Assert.False(AdMobSignature.Verify(Content + "&extra=1", signature, publicKey));
        Assert.False(AdMobSignature.Verify(Content.ToLowerInvariant(), signature, publicKey));
    }

    [Fact]
    public void A_signature_from_another_key_is_refused()
    {
        using var signer = NewKey();
        using var other = NewKey();
        Assert.False(AdMobSignature.Verify(Content, AdMobSignature.Sign(Content, signer), other.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void Damaged_signatures_and_keys_are_just_a_wrong_signature_never_an_exception()
    {
        using var key = NewKey();
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var signature = AdMobSignature.Sign(Content, key);

        Assert.False(AdMobSignature.Verify(Content, null, publicKey));
        Assert.False(AdMobSignature.Verify(Content, "", publicKey));
        Assert.False(AdMobSignature.Verify(Content, "***not base64***", publicKey));
        Assert.False(AdMobSignature.Verify(Content, "AAAA", publicKey));                                  // base64 đúng nhưng không phải DER
        Assert.False(AdMobSignature.Verify(Content, new string('A', 600), publicKey));                    // quá dài
        Assert.False(AdMobSignature.Verify(Content, signature[..^4], publicKey));                         // cụt
        Assert.False(AdMobSignature.Verify(Content, signature, [1, 2, 3]));                               // khóa hỏng
        Assert.False(AdMobSignature.Verify(Content, signature, []));
    }

    [Fact]
    public void A_signature_over_the_raw_query_keeps_percent_encoding_as_sent()
    {
        // Chuỗi thô được ký trước khi giải mã phần trăm; kiểm trên chuỗi đã giải mã sẽ sai.
        using var key = NewKey();
        const string raw = "ad_unit=5224354917&custom_data=a%20b&timestamp=1&transaction_id=T%2B1&user_id=u";
        var signature = AdMobSignature.Sign(raw, key);
        Assert.True(AdMobSignature.Verify(AdMobSignature.SignedContent($"?{raw}&signature={signature}&key_id=7")!, signature, key.ExportSubjectPublicKeyInfo()));
        Assert.False(AdMobSignature.Verify(raw.Replace("%20", " ").Replace("%2B", "+"), signature, key.ExportSubjectPublicKeyInfo()));
    }
}
