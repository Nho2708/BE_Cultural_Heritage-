using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Security;
using DSVHVN.Tests.Support;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Unit;

/// <summary>Mã giao dịch một lần của web (ký, gắn thiết bị và di sản, hết hạn sau 10 phút) và bộ giới hạn tần suất báo thưởng của web (10 lượt mỗi giờ mỗi thiết bị, 60 lượt mỗi giờ mỗi IP).</summary>
public sealed class AdRewardSecurityTests
{
    private const string DeviceKey = "3f2b8c1e-9a47-4d6b-8e10-5c7a9b2d4f61";
    private const string OtherDevice = "0e9d1c52-7b3f-4a28-9c6d-1f4e8a7b2c30";

    private sealed class TestingEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static JwtSigningKeyProvider Keys(string key) =>
        new(Options.Create(new JwtOptions { SigningKey = key }), new TestingEnvironment(), NullLogger<JwtSigningKeyProvider>.Instance);

    private static HmacAdRewardTokenCodec Codec(MutableTimeProvider clock, string key = "unit-test-signing-key-0123456789-abcdef") => new(Keys(key), clock);

    // ---- Mã giao dịch ----

    [Fact]
    public void A_code_reads_back_to_its_device_and_heritage_and_lives_for_ten_minutes()
    {
        var clock = new MutableTimeProvider();
        var codec = Codec(clock);

        var ticket = codec.Issue(DeviceKey, 4711);

        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddMinutes(10), ticket.ExpiresAt);
        var read = codec.Read(ticket.Code);
        Assert.NotNull(read);
        Assert.Equal((DeviceKey, 4711L, ticket.Code, ticket.ExpiresAt), (read.DeviceKey, read.HeritageId, read.Code, read.ExpiresAt));
        Assert.True(ticket.Code.Length <= 200);
        Assert.Matches("^[A-Za-z0-9_.-]+$", ticket.Code);
    }

    [Fact]
    public void A_code_works_until_its_ten_minutes_run_out()
    {
        var clock = new MutableTimeProvider();
        var codec = Codec(clock);
        var code = codec.Issue(DeviceKey, 1).Code;

        clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        Assert.NotNull(codec.Read(code));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(codec.Read(code));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(codec.Read(code));
    }

    [Fact]
    public void Two_codes_for_the_same_device_and_heritage_differ()
    {
        var codec = Codec(new MutableTimeProvider());
        Assert.NotEqual(codec.Issue(DeviceKey, 1).Code, codec.Issue(DeviceKey, 1).Code);
    }

    [Fact]
    public void A_code_from_another_signing_key_or_with_edits_is_refused()
    {
        var clock = new MutableTimeProvider();
        var code = Codec(clock).Issue(DeviceKey, 9).Code;

        Assert.Null(Codec(clock, "another-signing-key-0123456789-abcdefghi").Read(code));

        var codec = Codec(clock);
        var parts = code.Split('.');
        var flipped = (parts[0][0] == 'A' ? 'B' : 'A') + parts[0][1..];
        Assert.Null(codec.Read(flipped + "." + parts[1]));
        Assert.Null(codec.Read(parts[0] + "." + (parts[1][0] == 'A' ? 'B' : 'A') + parts[1][1..]));
        Assert.Null(codec.Read(parts[0]));
        Assert.Null(codec.Read(parts[0] + "." + parts[1] + ".x"));
        Assert.Null(codec.Read("." + parts[1]));
        Assert.Null(codec.Read(parts[0] + "."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("***.***")]
    [InlineData("AAAA.AAAA")]
    public void Garbage_is_not_a_code(string? code) => Assert.Null(Codec(new MutableTimeProvider()).Read(code));

    [Fact]
    public void An_overlong_code_is_refused_before_any_work()
    {
        var codec = Codec(new MutableTimeProvider());
        Assert.Null(codec.Read(new string('A', 201)));
    }

    [Fact]
    public void A_code_issued_in_the_future_is_refused()
    {
        var clock = new MutableTimeProvider();
        var code = Codec(clock).Issue(DeviceKey, 1).Code;
        clock.Advance(TimeSpan.FromMinutes(-5));   // đồng hồ lùi: mã được cấp "trong tương lai"
        Assert.Null(Codec(clock).Read(code));
    }

    // ---- Giới hạn tần suất ----

    [Fact]
    public void A_device_gets_ten_rewards_an_hour_then_waits_until_the_oldest_one_leaves_the_window()
    {
        var clock = new MutableTimeProvider();
        var throttle = new InMemoryAdRewardThrottle(clock);

        for (var i = 0; i < AdRules.WebMaxPerDevicePerHour; i++)
        {
            Assert.True(throttle.TryAcquire(DeviceKey, "10.0.0.1").Allowed);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        // Đã 10 phút kể từ lượt đầu: lượt đầu ra khỏi cửa sổ sau 50 phút nữa.
        var denied = throttle.TryAcquire(DeviceKey, "10.0.0.1");
        Assert.False(denied.Allowed);
        Assert.Equal(TimeSpan.FromMinutes(50), denied.RetryAfter);

        // Thiết bị khác không bị ảnh hưởng.
        Assert.True(throttle.TryAcquire(OtherDevice, "10.0.0.2").Allowed);

        clock.Advance(TimeSpan.FromMinutes(50));
        Assert.True(throttle.TryAcquire(DeviceKey, "10.0.0.1").Allowed);
        Assert.False(throttle.TryAcquire(DeviceKey, "10.0.0.1").Allowed);   // lượt thứ hai trong cửa sổ vẫn kẹt ở 10
    }

    [Fact]
    public void An_address_gets_sixty_rewards_an_hour_shared_by_every_device_behind_it()
    {
        var clock = new MutableTimeProvider();
        var throttle = new InMemoryAdRewardThrottle(clock);

        for (var i = 0; i < AdRules.WebMaxPerIpPerHour; i++)
            Assert.True(throttle.TryAcquire(Guid.NewGuid().ToString(), "203.0.113.7").Allowed);

        var denied = throttle.TryAcquire(Guid.NewGuid().ToString(), "203.0.113.7");
        Assert.False(denied.Allowed);
        Assert.True(denied.RetryAfter > TimeSpan.Zero);
        Assert.True(throttle.TryAcquire(Guid.NewGuid().ToString(), "203.0.113.8").Allowed);   // IP khác thì được

        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(throttle.TryAcquire(Guid.NewGuid().ToString(), "203.0.113.7").Allowed);
    }

    [Fact]
    public void A_denied_request_does_not_use_up_the_other_counter()
    {
        var clock = new MutableTimeProvider();
        var throttle = new InMemoryAdRewardThrottle(clock);
        for (var i = 0; i < AdRules.WebMaxPerDevicePerHour; i++) Assert.True(throttle.TryAcquire(DeviceKey, "10.0.0.1").Allowed);

        // Thiết bị đã kịch trần bị từ chối nhiều lần: bộ đếm của IP không bị đẩy lên theo.
        for (var i = 0; i < 100; i++) Assert.False(throttle.TryAcquire(DeviceKey, "10.0.0.1").Allowed);
        for (var i = 0; i < AdRules.WebMaxPerIpPerHour - AdRules.WebMaxPerDevicePerHour; i++)
            Assert.True(throttle.TryAcquire(Guid.NewGuid().ToString(), "10.0.0.1").Allowed);
        Assert.False(throttle.TryAcquire(Guid.NewGuid().ToString(), "10.0.0.1").Allowed);
    }

    [Fact]
    public void Without_an_address_only_the_device_counter_applies()
    {
        var throttle = new InMemoryAdRewardThrottle(new MutableTimeProvider());
        for (var i = 0; i < AdRules.WebMaxPerDevicePerHour; i++) Assert.True(throttle.TryAcquire(DeviceKey, null).Allowed);
        Assert.False(throttle.TryAcquire(DeviceKey, null).Allowed);
        Assert.True(throttle.TryAcquire(OtherDevice, null).Allowed);
    }

    [Fact]
    public void Only_the_first_rejection_of_an_hour_is_worth_a_log_line_per_device()
    {
        var clock = new MutableTimeProvider();
        var throttle = new InMemoryAdRewardThrottle(clock);

        Assert.True(throttle.ShouldLogRejection(DeviceKey));
        Assert.False(throttle.ShouldLogRejection(DeviceKey));
        Assert.True(throttle.ShouldLogRejection(OtherDevice));

        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.False(throttle.ShouldLogRejection(DeviceKey));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(throttle.ShouldLogRejection(DeviceKey));
    }

    [Fact]
    public void Old_counters_are_swept_so_memory_does_not_grow_with_every_device_seen()
    {
        var clock = new MutableTimeProvider();
        var throttle = new InMemoryAdRewardThrottle(clock);
        for (var i = 0; i < 200; i++) throttle.TryAcquire(Guid.NewGuid().ToString(), "10.0.0." + i % 250);

        clock.Advance(TimeSpan.FromHours(3));
        // Một lượt mới kích hoạt dọn; mọi thiết bị cũ bắt đầu lại từ 0 (không bị tính nhầm lượt của ba giờ trước).
        Assert.True(throttle.TryAcquire(DeviceKey, "10.0.0.1").Allowed);
        for (var i = 1; i < AdRules.WebMaxPerDevicePerHour; i++) Assert.True(throttle.TryAcquire(DeviceKey, "10.0.0.1").Allowed);
        Assert.False(throttle.TryAcquire(DeviceKey, "10.0.0.1").Allowed);
    }
}
