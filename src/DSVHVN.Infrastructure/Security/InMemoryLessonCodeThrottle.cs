using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Rules;
using Microsoft.Extensions.Caching.Memory;

namespace DSVHVN.Infrastructure.Security;

/// <summary>
/// Chống dò mã bài học, đếm trong bộ nhớ máy chủ: khởi động lại thì về 0 (không có bảng lưu), đủ cho một máy chủ. Hai bộ đếm độc lập:
/// theo tài khoản học sinh (10 lần sai trong 10 phút thì chặn 10 phút) và theo địa chỉ IP (30 lần sai trong 10 phút thì chặn 15 phút).
/// Mốc thời gian lấy từ <see cref="TimeProvider"/> để kiểm thử tua giờ; cache chỉ dùng để dọn mục cũ.
/// </summary>
public sealed class InMemoryLessonCodeThrottle(IMemoryCache cache, TimeProvider clock) : ILessonCodeThrottle
{
    public TimeSpan? GetBlock(long userId, string? ipAddress)
    {
        var byUser = Remaining(UserKey(userId));
        var byIp = ipAddress is null ? null : Remaining(IpKey(ipAddress));
        return (byUser, byIp) switch
        {
            (null, null) => null,
            ({ } u, null) => u,
            (null, { } i) => i,
            ({ } u, { } i) => u > i ? u : i,
        };
    }

    public TimeSpan? RegisterFailure(long userId, string? ipAddress)
    {
        var byUser = Register(UserKey(userId), LessonRules.MaxWrongCodesPerStudent, LessonRules.StudentCodeBlock);
        var byIp = ipAddress is null ? null : Register(IpKey(ipAddress), LessonRules.MaxWrongCodesPerIp, LessonRules.IpCodeBlock);
        return (byUser, byIp) switch
        {
            (null, null) => null,
            ({ } u, null) => u,
            (null, { } i) => i,
            ({ } u, { } i) => u > i ? u : i,
        };
    }

    private TimeSpan? Remaining(string key)
    {
        if (!cache.TryGetValue(key, out Bucket? bucket) || bucket is null) return null;
        lock (bucket)
        {
            if (bucket.BlockedUntil is not { } until) return null;
            var now = Now();
            if (until > now) return until - now;
            bucket.BlockedUntil = null;
            bucket.Failures.Clear();
            return null;
        }
    }

    private TimeSpan? Register(string key, int max, TimeSpan block)
    {
        var bucket = cache.GetOrCreate(key, entry =>
        {
            entry.SlidingExpiration = LessonRules.WrongCodeWindow + block;
            return new Bucket();
        })!;

        lock (bucket)
        {
            var now = Now();
            while (bucket.Failures.Count > 0 && now - bucket.Failures.Peek() >= LessonRules.WrongCodeWindow)
                bucket.Failures.Dequeue();
            bucket.Failures.Enqueue(now);
            if (bucket.Failures.Count < max) return null;

            bucket.Failures.Clear();
            bucket.BlockedUntil = now + block;
            return block;
        }
    }

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private static string UserKey(long userId) => "lesson-code:user:" + userId;
    private static string IpKey(string ip) => "lesson-code:ip:" + ip;

    private sealed class Bucket
    {
        public Queue<DateTime> Failures { get; } = new();
        public DateTime? BlockedUntil { get; set; }
    }
}
