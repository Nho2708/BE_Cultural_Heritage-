using DSVHVN.Application.Ads;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Infrastructure.Security;

/// <summary>
/// Giới hạn tần suất báo thưởng của web, đếm trong bộ nhớ máy chủ (không có bảng): khởi động lại thì về 0. Cửa sổ trượt 1 giờ; tối đa 10 lượt cho một mã thiết bị và 60 lượt cho một
/// địa chỉ IP. Địa chỉ IP chỉ nằm trong bộ đếm này (mất khi hết cửa sổ hoặc khởi động lại), không ghi vào cơ sở dữ liệu hay nhật ký. Mốc thời gian lấy từ <see cref="TimeProvider"/>
/// để kiểm thử tua giờ. Cả hai ngưỡng phải còn chỗ thì mới ghi nhận lượt (một lượt bị từ chối không làm đầy bộ đếm bên kia).
/// </summary>
public sealed class InMemoryAdRewardThrottle(TimeProvider clock) : IAdRewardThrottle
{
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<DateTime>> _hits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _logged = new(StringComparer.Ordinal);
    private DateTime _lastSweep = DateTime.MinValue;

    public AdRateDecision TryAcquire(string deviceKey, string? ipAddress)
    {
        lock (_lock)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            Sweep(now);

            var byDevice = Window("device:" + deviceKey, now);
            var byIp = string.IsNullOrEmpty(ipAddress) ? null : Window("ip:" + ipAddress, now);

            var wait = TimeSpan.Zero;
            if (byDevice.Count >= AdRules.WebMaxPerDevicePerHour) wait = Max(wait, byDevice.Peek() + AdRules.RateWindow - now);
            if (byIp is not null && byIp.Count >= AdRules.WebMaxPerIpPerHour) wait = Max(wait, byIp.Peek() + AdRules.RateWindow - now);
            if (wait > TimeSpan.Zero) return new AdRateDecision(false, Max(wait, TimeSpan.FromSeconds(1)));

            byDevice.Enqueue(now);
            byIp?.Enqueue(now);
            return new AdRateDecision(true, TimeSpan.Zero);
        }
    }

    public bool ShouldLogRejection(string deviceKey)
    {
        lock (_lock)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (_logged.TryGetValue(deviceKey, out var last) && now - last < AdRules.RateWindow) return false;
            _logged[deviceKey] = now;
            return true;
        }
    }

    private Queue<DateTime> Window(string key, DateTime now)
    {
        if (!_hits.TryGetValue(key, out var queue)) _hits[key] = queue = new Queue<DateTime>();
        while (queue.Count > 0 && now - queue.Peek() >= AdRules.RateWindow) queue.Dequeue();
        return queue;
    }

    /// <summary>Dọn định kỳ các bộ đếm đã hết hạn để bộ nhớ không phình ra theo số thiết bị và IP đã gặp.</summary>
    private void Sweep(DateTime now)
    {
        if (now - _lastSweep < SweepEvery) return;
        _lastSweep = now;
        foreach (var key in _hits.Where(p => p.Value.Count == 0 || now - p.Value.Last() >= AdRules.RateWindow)
                     .Select(p => p.Key).ToList())
            _hits.Remove(key);
        foreach (var key in _logged.Where(p => now - p.Value >= AdRules.RateWindow).Select(p => p.Key).ToList())
            _logged.Remove(key);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
