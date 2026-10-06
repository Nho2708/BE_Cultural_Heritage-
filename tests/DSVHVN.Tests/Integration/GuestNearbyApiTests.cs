using System.Collections.Concurrent;
using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.Guests;
using DSVHVN.Application.Heritages;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static DSVHVN.Tests.Integration.GuestTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// "Gần bạn": tối đa 6 di sản đã xuất bản có tọa độ, gần nhất trước, khoảng cách bằng mét hoặc km, tọa độ làm tròn 3 chữ số, vị trí ngoài khung Việt Nam nới 1 độ thì không hiện danh sách,
/// và vị trí không bao giờ được lưu, ghi lịch sử hay lọt vào nhật ký của máy chủ.
/// </summary>
public sealed class GuestNearbyApiTests
{
    private const double MyLatitude = 21.033;
    private const double MyLongitude = 105.85;

    private sealed record Place(HeritageDetailDto Heritage, double Latitude, double Longitude);

    private static async Task<Place> PlaceAsync(GuestTestKit kit, string admin, string name, double latitude, double longitude) =>
        new(await kit.HeritageAsync(admin, name, "TANGIBLE", "01", "NATIONAL", (decimal)latitude, (decimal)longitude), latitude, longitude);

    [Fact]
    public async Task The_six_nearest_published_heritage_with_coordinates_come_back_nearest_first_with_distances()
    {
        using var factory = new ApiFactory();
        var kit = new GuestTestKit(factory);
        var admin = await kit.AdminAsync();
        // Xa dần theo thứ tự đặt tên; đặt lộn xộn để thứ tự trả về không phải thứ tự tạo.
        var p4 = await PlaceAsync(kit, admin, "Điểm bốn", 20.95, 105.80);
        var p1 = await PlaceAsync(kit, admin, "Điểm một", 21.0335, 105.85);
        var p7 = await PlaceAsync(kit, admin, "Điểm bảy", 22.5, 104.6);
        var p3 = await PlaceAsync(kit, admin, "Điểm ba", 21.044, 105.85);            // khoảng 1,2 km
        var p5 = await PlaceAsync(kit, admin, "Điểm năm", 21.3, 105.9);
        var p2 = await PlaceAsync(kit, admin, "Điểm hai", 21.0341, 105.8504);
        var p6 = await PlaceAsync(kit, admin, "Điểm sáu", 21.6, 106.1);
        // Không vào danh sách: không có tọa độ, đã lưu trữ, đang nháp.
        await kit.HeritageAsync(admin, "Không tọa độ", "INTANGIBLE", "01", "NATIONAL", null, null);
        var archived = await PlaceAsync(kit, admin, "Đã lưu trữ", 21.0331, 105.8501);
        await kit.ArchiveAsync(admin, archived.Heritage.Id);
        await kit.Discovery.DraftAsync(admin, new DiscoveryTestKit.Spec("Nháp gần " + DiscoveryTestKit.Unique(), Latitude: 21.0331m, Longitude: 105.8501m));

        var result = await kit.NearbyAsync(MyLatitude, MyLongitude);

        Assert.Null(result.UnavailableReason);
        Assert.Null(result.Message);
        var expected = new[] { p1, p2, p3, p4, p5, p6 };
        Assert.Equal(expected.Select(p => p.Heritage.Slug), result.Items.Select(i => i.Heritage.Slug));
        Assert.DoesNotContain(p7.Heritage.Slug, result.Items.Select(i => i.Heritage.Slug));
        Assert.Equal(GuestRules.ResultLimit, result.Items.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            var meters = GuestRules.DistanceMeters(MyLatitude, MyLongitude, expected[i].Latitude, expected[i].Longitude);
            Assert.Equal((int)Math.Round(meters, MidpointRounding.AwayFromZero), result.Items[i].DistanceMeters);
            Assert.Equal(GuestRules.FormatDistance(meters, LanguageCode.vi), result.Items[i].DistanceText);
        }
        Assert.EndsWith(" m", result.Items[0].DistanceText);
        Assert.Equal("1,2 km", result.Items[2].DistanceText);
        Assert.Equal(result.Items.Select(i => i.DistanceMeters).Order(), result.Items.Select(i => i.DistanceMeters));
    }

    [Fact]
    public async Task Distance_text_and_names_follow_the_language_and_the_card_is_the_public_heritage_card()
    {
        using var factory = new ApiFactory();
        var kit = new GuestTestKit(factory);
        var admin = await kit.AdminAsync();
        var near = await PlaceAsync(kit, admin, "Gần", 21.044, 105.85);

        var vi = await kit.NearbyAsync(MyLatitude, MyLongitude);
        var en = await kit.NearbyAsync(MyLatitude, MyLongitude, "lang=en");
        var ko = await kit.NearbyAsync(MyLatitude, MyLongitude, "lang=ko");

        Assert.Equal(("1,2 km", "1.2 km", "1.2 km"), (vi.Items.Single().DistanceText, en.Items.Single().DistanceText, ko.Items.Single().DistanceText));
        Assert.Equal(vi.Items.Single().DistanceMeters, en.Items.Single().DistanceMeters);
        var card = en.Items.Single().Heritage;
        Assert.Equal((near.Heritage.Slug, true), (card.Slug, card.IsFallback));       // chưa có bản dịch: tiếng Việt kèm cờ lùi
        Assert.Equal(HeritageType.TANGIBLE, card.HeritageType);
        Assert.False(vi.Items.Single().Heritage.IsFallback);
    }

    [Fact]
    public async Task Coordinates_are_rounded_to_three_decimals_before_the_distance_is_computed()
    {
        using var factory = new ApiFactory();
        var kit = new GuestTestKit(factory);
        var admin = await kit.AdminAsync();
        await PlaceAsync(kit, admin, "Điểm", 21.05, 105.9);

        var exact = await kit.NearbyAsync(21.033, 105.85);
        var noisy = await kit.NearbyAsync(21.03312345678, 105.84987654321);        // làm tròn về 21,033 và 105,850
        var different = await kit.NearbyAsync(21.034, 105.85);

        Assert.Equal(exact.Items.Single().DistanceMeters, noisy.Items.Single().DistanceMeters);
        Assert.NotEqual(exact.Items.Single().DistanceMeters, different.Items.Single().DistanceMeters);
    }

    [Theory]
    [InlineData(35.68, 139.69)]       // Tokyo
    [InlineData(48.85, 2.35)]         // Paris
    [InlineData(6.9, 105.0)]          // dưới khung nới 1 độ
    [InlineData(25.0, 105.0)]         // trên khung
    [InlineData(15.0, 100.5)]         // Thái Lan
    [InlineData(15.0, 119.0)]         // Biển Đông phía đông, ngoài khung nới
    public async Task A_position_outside_vietnam_gets_no_list_and_a_reason(double latitude, double longitude)
    {
        using var factory = new ApiFactory();
        var kit = new GuestTestKit(factory);
        var admin = await kit.AdminAsync();
        await PlaceAsync(kit, admin, "Điểm", 21.05, 105.9);

        var result = await kit.NearbyAsync(latitude, longitude);

        Assert.Empty(result.Items);
        Assert.Equal((NearbyUnavailable.OutsideVietnam, GuestMessages.NearbyOutsideVietnam), (result.UnavailableReason, result.Message));
    }

    [Theory]
    [InlineData(7.0, 101.0)]
    [InlineData(24.5, 118.5)]
    [InlineData(16.5, 112.0)]         // vùng quần đảo Hoàng Sa
    public async Task The_widened_vietnam_box_still_gives_a_list(double latitude, double longitude)
    {
        using var factory = new ApiFactory();
        var kit = new GuestTestKit(factory);
        var admin = await kit.AdminAsync();
        await PlaceAsync(kit, admin, "Điểm", 21.05, 105.9);

        var result = await kit.NearbyAsync(latitude, longitude);

        Assert.Null(result.UnavailableReason);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task Missing_or_impossible_coordinates_are_refused()
    {
        using var factory = new ApiFactory();
        var kit = new GuestTestKit(factory);
        var url = $"{Root}/nearby";

        await kit.FailAsync(kit.PostAsync<object>(url, null, new { }), HttpStatusCode.BadRequest, MessageCodes.Required);
        await kit.FailAsync(kit.PostAsync<object>(url, null, new { latitude = 21.0 }), HttpStatusCode.BadRequest, MessageCodes.Required);
        await kit.FailAsync(kit.PostAsync<object>(url, null, new { latitude = 91.0, longitude = 105.0 }), HttpStatusCode.BadRequest, MessageCodes.Invalid);
        await kit.FailAsync(kit.PostAsync<object>(url, null, new { latitude = 21.0, longitude = 181.0 }), HttpStatusCode.BadRequest, MessageCodes.Invalid);
        await kit.FailAsync(kit.PostAsync<object>(url, null, new { latitude = "hai mươi", longitude = 105.0 }), HttpStatusCode.BadRequest, MessageCodes.Invalid);
        var bad = await kit.PostAsync<object>(url, null, new { latitude = -91.0, longitude = 105.0 });
        Assert.Equal("latitude", bad.Body.Errors!.Keys.Single());
        var badLongitude = await kit.PostAsync<object>(url, null, new { latitude = 21.0, longitude = -181.0 });
        Assert.Equal("longitude", badLongitude.Body.Errors!.Keys.Single());
    }

    [Fact]
    public async Task Nothing_is_stored_when_a_guest_asks_for_heritage_nearby_even_with_a_device_key_and_the_settings_flag_is_only_a_flag()
    {
        using var factory = new ApiFactory();
        var kit = new GuestTestKit(factory);
        var admin = await kit.AdminAsync();
        await PlaceAsync(kit, admin, "Điểm", 21.05, 105.9);
        var key = NewKey();
        await kit.ExpectAsync(kit.SendAsync<GuestDeviceDto>(HttpMethod.Put, $"{Root}/device", key, new { locationAllowed = false }));
        var devicesBefore = await kit.DeviceCountAsync();

        // Tính khoảng cách không phụ thuộc cờ cho phép, và không ghi gì.
        var result = await kit.ExpectAsync(kit.SendAsync<NearbyDto>(HttpMethod.Post, $"{Root}/nearby", key, new { latitude = 21.033, longitude = 105.85 }));
        Assert.Single(result.Items);

        Assert.Equal(devicesBefore, await kit.DeviceCountAsync());
        Assert.Empty(await kit.ActivitiesAsync(key));
        Assert.False((await kit.DeviceAsync(key))!.LocationAllowed);
        Assert.Equal(0, await factory.WithDbAsync(db => db.GuestActivities.CountAsync()));
    }

    [Fact]
    public async Task The_guest_tables_have_no_column_that_could_hold_a_position_an_ip_address_or_a_person()
    {
        using var factory = new ApiFactory();
        _ = factory.Server;
        var columns = await factory.WithDbAsync(db => Task.FromResult(
            db.Model.GetEntityTypes().Where(e => e.GetTableName()!.StartsWith("guest_"))
                .ToDictionary(e => e.GetTableName()!, e => e.GetProperties().Select(p => p.GetColumnName()).Order().ToList())));

        Assert.Equal(["created_at", "device_key", "id", "language_code", "last_seen_at", "location_allowed", "platform"], columns["guest_devices"]);
        Assert.Equal(["activity_type", "created_at", "guest_device_id", "heritage_id", "id", "language_code", "search_query"], columns["guest_activities"]);
    }

    [Fact]
    public async Task The_position_never_reaches_the_server_log_while_a_query_string_would()
    {
        using var factory = new LogCapturingApiFactory();
        var kit = new GuestTestKit(factory);
        var admin = await kit.AdminAsync();
        await PlaceAsync(kit, admin, "Điểm", 21.05, 105.9);
        var key = NewKey();

        // Vị trí riêng, dễ nhận ra trong nhật ký; đi qua mọi đường có thể: gần bạn, đổi cài đặt, gợi ý, lỗi dữ liệu.
        await kit.ExpectAsync(kit.SendAsync<NearbyDto>(HttpMethod.Post, $"{Root}/nearby?lang=en", key, new { latitude = 21.123456, longitude = 105.654321 }));
        await kit.ExpectAsync(kit.SendAsync<NearbyDto>(HttpMethod.Post, $"{Root}/nearby", null, new { latitude = 35.123456, longitude = 139.654321 }));
        await kit.FailAsync(kit.PostAsync<object>($"{Root}/nearby", null, new { latitude = 91.123456, longitude = 105.654321 }), HttpStatusCode.BadRequest, MessageCodes.Invalid);
        await kit.ExpectAsync(kit.SendAsync<GuestDeviceDto>(HttpMethod.Put, $"{Root}/device", key, new { locationAllowed = true }));
        await kit.RecommendationsAsync(key);
        // Đối chứng: từ khóa trên đường dẫn thì có trong nhật ký, cho thấy nhật ký đang ghi đúng loại thông tin này.
        await kit.ExpectAsync(kit.GetAsync<object>($"{PublicRoot}/heritages?keyword=doichung{key[..8]}", null));

        var log = string.Join('\n', factory.Logs.Messages);
        Assert.Contains("/api/v1/public/guest/nearby", log);
        Assert.Contains("doichung" + key[..8], log);
        foreach (var secret in new[] { "21.123456", "105.654321", "35.123456", "139.654321", "91.123456" })
            Assert.DoesNotContain(secret, log);
    }

    /// <summary>Api có thêm một nơi nhận mọi dòng nhật ký (mọi mức, mọi nhóm) để kiểm nội dung nhật ký.</summary>
    private sealed class LogCapturingApiFactory : ApiFactory
    {
        public CapturingLogProvider Logs { get; } = new();

        // Mọi nhóm ở mức thấp nhất, kể cả nhật ký yêu cầu của máy chủ web và câu lệnh của EF Core, vốn mặc định bị lọc.
        protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
        [
            new("Logging:LogLevel:Default", "Trace"),
            new("Logging:LogLevel:Microsoft.AspNetCore", "Trace"),
            new("Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command", "Trace"),
        ];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        }
    }

    private sealed class CapturingLogProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                sink.Enqueue($"{category}: {formatter(state, exception)}");
                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                    foreach (var value in values) sink.Enqueue($"{category}: {value.Key}={value.Value}");
                if (exception is not null) sink.Enqueue($"{category}: {exception}");
            }
        }
    }
}
