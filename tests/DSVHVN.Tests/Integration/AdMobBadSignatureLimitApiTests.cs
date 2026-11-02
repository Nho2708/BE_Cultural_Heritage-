using System.Net;
using DSVHVN.Application.Common;
using static DSVHVN.Tests.Integration.AdsTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>Api quảng cáo với ngưỡng sai chữ ký thấp (3 lượt mỗi phút) để kiểm giới hạn của lời gọi xác minh AdMob.</summary>
public sealed class AdsLowBadSignatureApiFactory : AdsApiFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
        base.ExtraSettings.Append(new("RateLimiting:AdMobBadSignaturesPerMinute", "3"));
}

/// <summary>
/// Giới hạn lượt sai chữ ký của xác minh AdMob: mỗi IP được 3 lượt sai mỗi phút (ngưỡng thấp trong kiểm thử, mặc định 300), lượt kế tiếp nhận 429 kèm Retry-After và không ghi thêm nhật ký;
/// lời gọi đúng chữ ký không bị đếm hay chặn dù IP đã vượt ngưỡng.
/// </summary>
public sealed class AdMobBadSignatureLimitApiTests(AdsLowBadSignatureApiFactory factory) : IClassFixture<AdsLowBadSignatureApiFactory>
{
    private readonly AdsTestKit _k = new(factory);

    [Fact]
    public async Task Only_bad_signatures_are_counted_and_a_correct_call_still_passes_after_the_limit_is_hit()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageWithoutSummaryAsync(admin);
        var device = NewKey();

        // Lời gọi đúng không tiêu lượt: gọi nhiều hơn ngưỡng vẫn 200.
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await _k.CallSsvAsync(_k.Ssv(heritage.Id, NewKey()))).Response.StatusCode);

        var rejectionsBefore = await _k.RejectionCountAsync();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await _k.CallSsvAsync(_k.Ssv(heritage.Id, device, mutate: q => q + "0"))).Response.StatusCode);
        Assert.Equal(rejectionsBefore + 3, await _k.RejectionCountAsync());

        var (blocked, body) = await _k.CallSsvAsync(_k.Ssv(heritage.Id, device, mutate: q => q + "0"));
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal(MessageCodes.TooManyRequests, body.ErrorCode);
        var seconds = int.Parse(blocked.Headers.GetValues("Retry-After").Single());
        Assert.InRange(seconds, 1, 60);
        Assert.Equal($"Có quá nhiều yêu cầu gửi tới trong thời gian ngắn. Vui lòng thử lại sau {seconds} giây.", body.Message);
        // Lượt bị chặn không ghi thêm nhật ký thao tác.
        Assert.Equal(rejectionsBefore + 3, await _k.RejectionCountAsync());

        // Cùng IP đã vượt ngưỡng nhưng lời gọi đúng chữ ký của Google vẫn được ghi.
        var unlocked = await _k.UnlockViaAdMobAsync(heritage.Id, device);
        Assert.True(unlocked.Recorded);
        Assert.Single(await _k.UnlocksAsync(device));
    }
}
