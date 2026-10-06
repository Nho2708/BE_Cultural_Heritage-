using System.Net;
using System.Security.Cryptography;
using System.Text;
using DSVHVN.Application.Ads;
using DSVHVN.Infrastructure.Ads;
using DSVHVN.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Khóa công khai của Google: tải danh sách theo định dạng của Google (<c>keyId</c>, <c>pem</c>, <c>base64</c>), đệm 24 giờ, tải lại khi gặp mã khóa lạ nhưng không thường hơn
/// một lần mỗi 60 giây, giữ danh sách cũ khi tải lỗi, chỉ tải qua HTTPS.
/// </summary>
public sealed class GoogleAdMobKeyStoreTests
{
    private const string Url = "https://keys.example.test/admob/reward/verifier-keys.json";

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(Respond(request));
        }
    }

    private static (byte[] Spki, string Pem) NewKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var base64 = Convert.ToBase64String(spki);
        var pem = "-----BEGIN PUBLIC KEY-----\n" + string.Join('\n', Enumerable.Range(0, (base64.Length + 63) / 64).Select(i => base64.Substring(i * 64, Math.Min(64, base64.Length - i * 64)))) + "\n-----END PUBLIC KEY-----";
        return (spki, pem);
    }

    private static string Json(params (long Id, string? Base64, string? Pem)[] keys) =>
        "{\"keys\":[" + string.Join(',', keys.Select(k =>
            "{\"keyId\":" + k.Id + (k.Pem is null ? "" : ",\"pem\":" + System.Text.Json.JsonSerializer.Serialize(k.Pem)) + (k.Base64 is null ? "" : ",\"base64\":\"" + k.Base64 + "\"") + "}")) + "]}";

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (GoogleAdMobKeyStore Store, ScriptedHandler Handler, MutableTimeProvider Clock) Create(string url = Url)
    {
        var handler = new ScriptedHandler();
        var clock = new MutableTimeProvider();
        var options = Options.Create(new AdsOptions { AdMob = new AdMobOptions { VerifierKeysUrl = url } });
        return (new GoogleAdMobKeyStore(new HttpClient(handler), options, clock, NullLogger<GoogleAdMobKeyStore>.Instance), handler, clock);
    }

    [Fact]
    public async Task Keys_are_read_from_the_base64_field_and_fall_back_to_the_pem_field()
    {
        var (store, handler, _) = Create();
        var (spkiA, pemA) = NewKey();
        var (spkiB, pemB) = NewKey();
        handler.Respond = _ => Ok(Json((3335741209, Convert.ToBase64String(spkiA), pemA), (4000000001, null, pemB)));

        Assert.Equal(spkiA, await store.FindPublicKeyAsync(3335741209, default));
        Assert.Equal(spkiB, await store.FindPublicKeyAsync(4000000001, default));
        Assert.Single(handler.Requests);
        Assert.Equal(new Uri(Url), handler.Requests[0]);
    }

    [Fact]
    public async Task The_list_is_cached_for_24_hours_and_then_loaded_again()
    {
        var (store, handler, clock) = Create();
        var (spki, _) = NewKey();
        handler.Respond = _ => Ok(Json((1, Convert.ToBase64String(spki), null)));

        for (var i = 0; i < 5; i++) Assert.Equal(spki, await store.FindPublicKeyAsync(1, default));
        Assert.Single(handler.Requests);

        clock.Advance(TimeSpan.FromHours(23));
        await store.FindPublicKeyAsync(1, default);
        Assert.Single(handler.Requests);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(spki, await store.FindPublicKeyAsync(1, default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task An_unknown_key_id_reloads_the_list_once_and_never_more_than_once_a_minute()
    {
        var (store, handler, clock) = Create();
        var (spkiOld, _) = NewKey();
        var (spkiNew, _) = NewKey();
        handler.Respond = _ => Ok(Json((1, Convert.ToBase64String(spkiOld), null)));
        Assert.Equal(spkiOld, await store.FindPublicKeyAsync(1, default));
        Assert.Single(handler.Requests);

        // Mã khóa lạ ngay sau lần tải đầu: chưa đủ 60 giây nên không tải lại, trả null.
        Assert.Null(await store.FindPublicKeyAsync(999, default));
        Assert.Null(await store.FindPublicKeyAsync(998, default));
        Assert.Single(handler.Requests);

        // Google xoay khóa: sau 60 giây mã khóa mới làm máy chủ tải lại đúng một lần.
        clock.Advance(TimeSpan.FromSeconds(60));
        handler.Respond = _ => Ok(Json((1, Convert.ToBase64String(spkiOld), null), (2, Convert.ToBase64String(spkiNew), null)));
        Assert.Equal(spkiNew, await store.FindPublicKeyAsync(2, default));
        Assert.Equal(2, handler.Requests.Count);

        // Mã khóa vẫn không có sau lần tải lại: không tải thêm trong 60 giây kế.
        Assert.Null(await store.FindPublicKeyAsync(777, default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_failed_load_keeps_the_old_list_and_a_key_that_was_never_seen_is_not_accepted()
    {
        var (store, handler, clock) = Create();
        var (spki, _) = NewKey();
        handler.Respond = _ => Ok(Json((1, Convert.ToBase64String(spki), null)));
        Assert.Equal(spki, await store.FindPublicKeyAsync(1, default));

        clock.Advance(TimeSpan.FromHours(25));   // hết hạn đệm, Google lỗi
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        Assert.Equal(spki, await store.FindPublicKeyAsync(1, default));   // vẫn dùng khóa cũ đã biết
        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(await store.FindPublicKeyAsync(55, default));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"keys\":[]}")]
    [InlineData("{\"keys\":[{\"keyId\":1,\"base64\":\"AAAA\"}]}")]
    [InlineData("{\"keys\":[{\"keyId\":1,\"pem\":\"-----BEGIN PUBLIC KEY-----\\n!!!\\n-----END PUBLIC KEY-----\"}]}")]
    [InlineData("{\"keys\":[{\"keyId\":1}]}")]
    public async Task A_list_with_no_usable_key_gives_nothing(string body)
    {
        var (store, handler, _) = Create();
        handler.Respond = _ => Ok(body);
        Assert.Null(await store.FindPublicKeyAsync(1, default));
    }

    [Fact]
    public async Task A_broken_key_is_skipped_but_the_good_keys_in_the_same_list_still_work()
    {
        var (store, handler, _) = Create();
        var (spki, _) = NewKey();
        handler.Respond = _ => Ok(Json((1, "AAAA", null), (2, Convert.ToBase64String(spki), null)));
        Assert.Null(await store.FindPublicKeyAsync(1, default));
        Assert.Equal(spki, await store.FindPublicKeyAsync(2, default));
    }

    [Theory]
    [InlineData("http://keys.example.test/verifier-keys.json")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task Keys_are_only_loaded_over_https(string url)
    {
        var (store, handler, _) = Create(url);
        var (spki, _) = NewKey();
        handler.Respond = _ => Ok(Json((1, Convert.ToBase64String(spki), null)));
        Assert.Null(await store.FindPublicKeyAsync(1, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cancelling_the_request_cancels_the_wait_instead_of_being_swallowed()
    {
        var (store, _, _) = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.FindPublicKeyAsync(1, cts.Token));
    }
}
