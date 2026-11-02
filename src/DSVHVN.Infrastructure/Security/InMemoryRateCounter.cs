using DSVHVN.Application.Common;

namespace DSVHVN.Infrastructure.Security;

/// <summary>
/// Bộ đếm tần suất trong bộ nhớ máy chủ (khởi động lại thì về 0, không có bảng lưu): mỗi khóa giữ hàng đợi mốc thời gian của các lượt còn nằm trong cửa sổ trượt.
/// Mốc thời gian lấy từ <see cref="TimeProvider"/> để kiểm thử tua giờ. Lượt bị từ chối không được ghi nhận nên kẻ lạm dụng không tự kéo dài thời gian bị chặn.
/// Định kỳ dọn khóa đã hết hạn để bộ nhớ không phình ra theo số mã thiết bị, người dùng và IP đã gặp. Khóa là mã thiết bị, mã người dùng hoặc IP, không ghi vào nhật ký.
/// </summary>
public sealed class InMemoryRateCounter(TimeProvider clock) : IRateCounter
{
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private DateTime _lastSweep = DateTime.MinValue;

    public TimeSpan? TryHit(string key, int limit, TimeSpan window)
    {
        lock (_lock)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            Sweep(now);

            if (!_entries.TryGetValue(key, out var entry)) _entries[key] = entry = new Entry();
            entry.Window = window;
            while (entry.Hits.Count > 0 && now - entry.Hits.Peek() >= window) entry.Hits.Dequeue();

            if (entry.Hits.Count >= Math.Max(1, limit))
            {
                var wait = entry.Hits.Peek() + window - now;
                return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
            }

            entry.Hits.Enqueue(now);
            return null;
        }
    }

    private void Sweep(DateTime now)
    {
        if (now - _lastSweep < SweepEvery) return;
        _lastSweep = now;
        foreach (var key in _entries.Where(p => p.Value.Hits.Count == 0 || now - p.Value.Hits.Last() >= p.Value.Window)
                     .Select(p => p.Key).ToList())
            _entries.Remove(key);
    }

    private sealed class Entry
    {
        public Queue<DateTime> Hits { get; } = new();
        public TimeSpan Window { get; set; }
    }
}
