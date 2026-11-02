using DSVHVN.Application.Common;
using DSVHVN.Infrastructure.Security;
using DSVHVN.Tests.Support;

namespace DSVHVN.Tests.Unit;

/// <summary>Bộ đếm tần suất trong bộ nhớ: cửa sổ trượt theo từng khóa, lượt bị từ chối không được ghi nhận, thời gian chờ tính đến lượt cũ nhất rời cửa sổ.</summary>
public sealed class RateCounterTests
{
    private readonly MutableTimeProvider _clock = new();
    private readonly InMemoryRateCounter _counter;

    public RateCounterTests() => _counter = new InMemoryRateCounter(_clock);

    [Fact]
    public void Allows_up_to_the_limit_then_reports_how_long_to_wait()
    {
        var window = TimeSpan.FromMinutes(1);
        Assert.Null(_counter.TryHit("a", 3, window));
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(_counter.TryHit("a", 3, window));
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(_counter.TryHit("a", 3, window));

        // Lượt cũ nhất cách đây 20 giây: còn phải chờ 40 giây.
        Assert.Equal(TimeSpan.FromSeconds(40), _counter.TryHit("a", 3, window));
        // Lượt bị từ chối không kéo dài thời gian chờ.
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(35), _counter.TryHit("a", 3, window));

        _clock.Advance(TimeSpan.FromSeconds(35));
        Assert.Null(_counter.TryHit("a", 3, window));
    }

    [Fact]
    public void Keys_are_independent_and_a_limit_below_one_is_treated_as_one()
    {
        var window = TimeSpan.FromMinutes(1);
        Assert.Null(_counter.TryHit("a", 1, window));
        Assert.NotNull(_counter.TryHit("a", 1, window));
        Assert.Null(_counter.TryHit("b", 1, window));
        Assert.Null(_counter.TryHit("c", 0, window));
        Assert.NotNull(_counter.TryHit("c", 0, window));
    }

    [Fact]
    public void The_wait_is_at_least_one_second()
    {
        var window = TimeSpan.FromMinutes(1);
        Assert.Null(_counter.TryHit("a", 1, window));
        _clock.Advance(TimeSpan.FromMilliseconds(59_900));
        Assert.Equal(TimeSpan.FromSeconds(1), _counter.TryHit("a", 1, window));
    }

    [Fact]
    public void EnforcePerMinute_throws_429_with_retry_after_and_the_shared_message()
    {
        _counter.EnforcePerMinute("k", 2);
        _counter.EnforcePerMinute("k", 2);

        var ex = Assert.Throws<AppException>(() => _counter.EnforcePerMinute("k", 2));
        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(MessageCodes.TooManyRequests, ex.AppMessage.Code);
        Assert.Equal("Có quá nhiều yêu cầu gửi tới trong thời gian ngắn. Vui lòng thử lại sau 60 giây.", ex.Message);
        Assert.Equal(TimeSpan.FromSeconds(60), ex.RetryAfter);
    }

    [Fact]
    public void Defaults_match_the_agreed_limits()
    {
        var options = new RateLimitOptions();
        Assert.Equal((300, 10, 120, 60, 300, 5),
            (options.PublicRequestsPerMinute, options.TeacherAiRequestsPerMinute, options.GuestHistoryEventsPerMinute,
                options.GuestSummaryRequestsPerMinute, options.AdMobBadSignaturesPerMinute, options.AdminTranslationAiRequestsPerMinute));
    }
}
