using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Security;
using DSVHVN.Tests.Support;
using Microsoft.Extensions.Caching.Memory;

namespace DSVHVN.Tests.Unit;

/// <summary>Giới hạn nhập sai mã bài học: 10 lần/10 phút theo tài khoản (chặn 10 phút), 30 lần/10 phút theo IP (chặn 15 phút), đếm trong bộ nhớ.</summary>
public sealed class LessonCodeThrottleTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly MutableTimeProvider _clock = new();
    private readonly InMemoryLessonCodeThrottle _throttle;

    public LessonCodeThrottleTests() => _throttle = new InMemoryLessonCodeThrottle(_cache, _clock);

    public void Dispose() => _cache.Dispose();

    private void Fail(long user, string? ip, int times)
    {
        for (var i = 0; i < times; i++) _throttle.RegisterFailure(user, ip);
    }

    [Fact]
    public void Nobody_is_blocked_before_any_failure()
    {
        Assert.Null(_throttle.GetBlock(1, "10.0.0.1"));
        Assert.Null(_throttle.GetBlock(1, null));
    }

    [Fact]
    public void The_tenth_wrong_code_in_ten_minutes_blocks_the_student_for_ten_minutes()
    {
        for (var i = 1; i <= 9; i++)
        {
            Assert.Null(_throttle.RegisterFailure(1, "10.0.0.1"));
            Assert.Null(_throttle.GetBlock(1, "10.0.0.1"));
        }

        Assert.Equal(TimeSpan.FromMinutes(10), _throttle.RegisterFailure(1, "10.0.0.1"));
        Assert.Equal(TimeSpan.FromMinutes(10), _throttle.GetBlock(1, "10.0.0.1"));

        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(TimeSpan.FromMinutes(6), _throttle.GetBlock(1, "10.0.0.1"));

        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(_throttle.GetBlock(1, "10.0.0.1"));
    }

    [Fact]
    public void Failures_older_than_ten_minutes_do_not_count()
    {
        Fail(1, null, 9);
        _clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Null(_throttle.RegisterFailure(1, null));           // chỉ còn 1 lần trong cửa sổ
        Fail(1, null, 8);
        Assert.Null(_throttle.GetBlock(1, null));                   // 9 lần: chưa chặn
        Assert.NotNull(_throttle.RegisterFailure(1, null));         // lần thứ 10 trong cửa sổ
    }

    [Fact]
    public void After_the_block_ends_the_student_starts_counting_from_zero()
    {
        Fail(1, null, 10);
        _clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        Assert.Null(_throttle.GetBlock(1, null));

        Fail(1, null, 9);
        Assert.Null(_throttle.GetBlock(1, null));
        Assert.NotNull(_throttle.RegisterFailure(1, null));
    }

    [Fact]
    public void Each_student_has_a_separate_count_but_the_ip_count_is_shared_by_the_class()
    {
        Fail(1, "10.0.0.1", 10);
        Assert.NotNull(_throttle.GetBlock(1, "10.0.0.1"));
        Assert.Null(_throttle.GetBlock(2, "10.0.0.1"));            // bạn cùng lớp chưa bị chặn

        // 30 lần sai từ nhiều học sinh cùng một IP làm khóa IP, kể cả với học sinh chưa sai lần nào.
        for (var student = 100; student < 130; student++)
            _throttle.RegisterFailure(student, "10.0.0.2");
        Assert.Equal(TimeSpan.FromMinutes(15), _throttle.GetBlock(999, "10.0.0.2"));
        Assert.Null(_throttle.GetBlock(999, "10.0.0.3"));
        Assert.Null(_throttle.GetBlock(999, null));

        _clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Null(_throttle.GetBlock(999, "10.0.0.2"));
    }

    [Fact]
    public void The_longer_of_the_two_blocks_is_reported_when_both_apply()
    {
        for (var student = 100; student < 129; student++) _throttle.RegisterFailure(student, "10.0.0.2");   // 29 lần theo IP

        // Học sinh 1 sai lần thứ 30 theo IP (chặn 15 phút) trong khi mới sai 1 lần theo tài khoản.
        Assert.Equal(TimeSpan.FromMinutes(15), _throttle.RegisterFailure(1, "10.0.0.2"));
        Assert.Equal(TimeSpan.FromMinutes(15), _throttle.GetBlock(1, "10.0.0.2"));
    }

    [Fact]
    public void Limits_come_from_the_documented_rules()
    {
        Assert.Equal((10, 30), (LessonRules.MaxWrongCodesPerStudent, LessonRules.MaxWrongCodesPerIp));
        Assert.Equal((TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15)),
            (LessonRules.WrongCodeWindow, LessonRules.StudentCodeBlock, LessonRules.IpCodeBlock));
    }
}
