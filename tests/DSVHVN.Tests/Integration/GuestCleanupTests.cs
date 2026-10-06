using DSVHVN.Domain.Enums;
using DSVHVN.Infrastructure.Background;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static DSVHVN.Tests.Integration.GuestTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Tác vụ nền dọn lịch sử khách: chạy ngay lúc khởi động rồi theo chu kỳ, xóa thật lượt quá 90 ngày của mọi thiết bị, giữ lượt còn hạn và bản ghi thiết bị, tắt được bằng cấu hình.
/// </summary>
public sealed class GuestCleanupTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly GuestTestKit _k;

    public GuestCleanupTests(ApiFactory factory)
    {
        _factory = factory;
        _k = new GuestTestKit(factory);
    }

    private GuestHistoryCleanupService Worker(int hours) => new(
        _factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new GuestCleanupOptions { CleanupIntervalHours = hours }),
        NullLogger<GuestHistoryCleanupService>.Instance);

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition()) return true;
            await Task.Delay(50);
        }
        return false;
    }

    [Fact]
    public async Task The_worker_deletes_expired_history_at_start_and_keeps_the_rest()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageAsync(admin, "Thành nhà Hồ");
        var first = NewKey();
        var second = NewKey();
        await _k.SeedAsync(first, new Seed(GuestActivityType.VIEW, heritage.Id, Days(91)), new Seed(GuestActivityType.SEARCH, null, Days(200), "cũ"),
            new Seed(GuestActivityType.LISTEN, heritage.Id, Days(89)));
        await _k.SeedAsync(second, new Seed(GuestActivityType.VIEW_3D, heritage.Id, Days(95)), new Seed(GuestActivityType.VIEW, heritage.Id, Minutes(5)));

        using var worker = Worker(24);
        await worker.StartAsync(default);
        try
        {
            Assert.True(await WaitUntilAsync(async () => (await _k.ActivitiesAsync(first)).Count == 1 && (await _k.ActivitiesAsync(second)).Count == 1),
                "tác vụ nền chưa dọn lịch sử quá hạn");
        }
        finally
        {
            await worker.StopAsync(default);
        }

        Assert.Equal([GuestActivityType.LISTEN], (await _k.ActivitiesAsync(first)).Select(a => a.ActivityType));
        Assert.Equal([GuestActivityType.VIEW], (await _k.ActivitiesAsync(second)).Select(a => a.ActivityType));
        Assert.NotNull(await _k.DeviceAsync(first));        // chỉ lịch sử bị xóa, thiết bị còn
        Assert.NotNull(await _k.DeviceAsync(second));
    }

    [Fact]
    public async Task A_zero_interval_turns_the_worker_off()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageAsync(admin, "Thành Cổ Loa");
        var key = NewKey();
        await _k.SeedAsync(key, new Seed(GuestActivityType.VIEW, heritage.Id, Days(120)));

        using var worker = Worker(0);
        await worker.StartAsync(default);
        await (worker.ExecuteTask ?? Task.CompletedTask);       // kết thúc ngay, không chờ chu kỳ
        await worker.StopAsync(default);

        Assert.Single(await _k.ActivitiesAsync(key));
    }
}
