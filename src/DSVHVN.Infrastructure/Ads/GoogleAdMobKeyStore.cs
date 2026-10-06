using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DSVHVN.Application.Ads;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Ads;

/// <summary>
/// Khóa công khai của Google để kiểm chữ ký xác minh phía máy chủ của AdMob, tải từ <c>Ads:AdMob:VerifierKeysUrl</c>
/// (mặc định <c>https://www.gstatic.com/admob/reward/verifier-keys.json</c>). Có đệm trong bộ nhớ:
/// <list type="bullet">
/// <item>Danh sách làm mới sau <c>KeyCacheHours</c> (mặc định 24 giờ).</item>
/// <item>Gặp mã khóa lạ thì tải lại ngay (Google có thể vừa xoay khóa), tối đa một lần tải lại mỗi lời gọi, và không tải lại thường hơn <c>KeyMinRefreshSeconds</c> (mặc định 60 giây)
/// để lời gọi giả mạo với mã khóa ngẫu nhiên không bắt máy chủ tải liên tục.</item>
/// <item>Tải lỗi (mạng, định dạng) thì ghi log cảnh báo và giữ danh sách cũ; mã khóa không có trong danh sách thì trả null (chữ ký bị từ chối, không bao giờ chấp nhận khi không kiểm được).</item>
/// </list>
/// Dòng JSON của Google: <c>{"keys":[{"keyId":3335741209,"pem":"-----BEGIN PUBLIC KEY-----...","base64":"MFkw..."}]}</c>; dùng <c>base64</c> (SubjectPublicKeyInfo), không có thì đọc <c>pem</c>.
/// </summary>
public sealed class GoogleAdMobKeyStore(HttpClient http, IOptions<AdsOptions> options, TimeProvider clock, ILogger<GoogleAdMobKeyStore> logger) : IAdMobKeyStore
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _fetching = new(1, 1);
    private volatile Snapshot? _snapshot;
    private DateTime? _lastAttempt;

    private sealed record Snapshot(IReadOnlyDictionary<long, byte[]> Keys, DateTime FetchedAt);

    private sealed record KeyFile(List<KeyEntry>? Keys);

    private sealed record KeyEntry(long KeyId, string? Pem, string? Base64);

    public async Task<byte[]?> FindPublicKeyAsync(long keyId, CancellationToken ct)
    {
        var settings = options.Value.AdMob;
        var now = clock.GetUtcNow().UtcDateTime;
        var snapshot = _snapshot;
        var fresh = snapshot is not null && now - snapshot.FetchedAt < TimeSpan.FromHours(Math.Max(1, settings.KeyCacheHours));
        if (fresh && snapshot!.Keys.TryGetValue(keyId, out var cached)) return cached;

        // Danh sách chưa có, đã quá hạn hoặc không có mã khóa này: tải lại một lần nếu lần tải trước đã đủ xa.
        await _fetching.WaitAsync(ct);
        try
        {
            now = clock.GetUtcNow().UtcDateTime;
            snapshot = _snapshot;
            fresh = snapshot is not null && now - snapshot.FetchedAt < TimeSpan.FromHours(Math.Max(1, settings.KeyCacheHours));
            if (snapshot is not null && fresh && snapshot.Keys.TryGetValue(keyId, out cached)) return cached;   // luồng khác vừa tải xong

            var tooSoon = _lastAttempt is { } last && now - last < TimeSpan.FromSeconds(Math.Max(0, settings.KeyMinRefreshSeconds));
            if (!tooSoon)
            {
                _lastAttempt = now;
                var loaded = await FetchAsync(settings.VerifierKeysUrl, now, ct);
                if (loaded is not null) _snapshot = snapshot = loaded;
            }

            return snapshot is not null && snapshot.Keys.TryGetValue(keyId, out var key) ? key : null;
        }
        finally
        {
            _fetching.Release();
        }
    }

    private async Task<Snapshot?> FetchAsync(string url, DateTime now, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            logger.LogWarning("Ads:AdMob:VerifierKeysUrl không phải địa chỉ HTTPS hợp lệ; không tải được khóa xác minh của Google.");
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FetchTimeout);
            using var response = await http.GetAsync(uri, timeout.Token);
            response.EnsureSuccessStatusCode();
            var file = await response.Content.ReadFromJsonAsync<KeyFile>(Json, timeout.Token);

            var keys = new Dictionary<long, byte[]>();
            foreach (var entry in file?.Keys ?? [])
            {
                var der = ParseKey(entry);
                if (der is not null) keys[entry.KeyId] = der;
            }

            if (keys.Count == 0)
            {
                logger.LogWarning("Danh sách khóa xác minh của Google không có khóa hợp lệ nào; giữ danh sách cũ.");
                return null;
            }

            return new Snapshot(keys, now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or NotSupportedException)
        {
            logger.LogWarning(ex, "Không tải được danh sách khóa xác minh của Google; giữ danh sách cũ.");
            return null;
        }
    }

    /// <summary>Khóa ở dạng SubjectPublicKeyInfo (DER); bỏ qua khóa hỏng hoặc không phải ECDSA.</summary>
    private static byte[]? ParseKey(KeyEntry entry)
    {
        try
        {
            var der = !string.IsNullOrWhiteSpace(entry.Base64)
                ? Convert.FromBase64String(entry.Base64.Trim())
                : !string.IsNullOrWhiteSpace(entry.Pem) ? PemDer(entry.Pem) : null;
            if (der is null) return null;
            using var probe = ECDsa.Create();
            probe.ImportSubjectPublicKeyInfo(der, out _);
            return der;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[] PemDer(string pem)
    {
        var body = string.Concat(pem.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(line => !line.StartsWith("-----", StringComparison.Ordinal)));
        return Convert.FromBase64String(body);
    }
}
