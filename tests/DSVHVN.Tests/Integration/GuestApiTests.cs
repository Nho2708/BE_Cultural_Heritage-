using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.Guests;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using static DSVHVN.Tests.Integration.GuestTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Khách tham quan qua API thật (SQLite): đăng ký thiết bị không cần đăng nhập, cài đặt, tự ghi lịch sử khi gọi API công khai kèm mã thiết bị
/// (tìm có từ khóa, chi tiết, tóm tắt, mô hình 3D), ghi tay lượt nghe, lượt lặp trong 10 phút, học sinh đăng nhập không bị ghi, xem và xóa lịch sử
/// chỉ trên thiết bị của mình, không lưu đệm.
/// </summary>
public sealed class GuestApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly GuestTestKit _k;

    public GuestApiTests(ApiFactory factory)
    {
        _factory = factory;
        _k = new GuestTestKit(factory);
    }

    // ---- Thiết bị và cài đặt ----

    [Fact]
    public async Task A_device_registers_without_logging_in_and_the_server_can_issue_the_key()
    {
        // Không mã: máy chủ cấp mã mới (UUID phiên bản 4), 201.
        var (issuedResponse, issued) = await _k.PostAsync<GuestDeviceDto>($"{Root}/devices", null, new { });
        Assert.Equal(HttpStatusCode.Created, issuedResponse.StatusCode);
        Assert.Equal(issued.Data!.DeviceKey, GuestRules.NormalizeDeviceKey(issued.Data.DeviceKey));
        Assert.Equal((true, ClientPlatform.WEB, "vi", false), (issued.Data.IsRegistered, issued.Data.Platform, issued.Data.Language, issued.Data.LocationAllowed));

        // Có mã do client sinh: 201 lần đầu, 200 lần sau; nền tảng lấy từ tiêu đề.
        var key = NewKey();
        var (first, created) = await _k.SendAsync<GuestDeviceDto>(HttpMethod.Post, $"{Root}/devices", key, new { language = "ko" }, platform: "android");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal((key, ClientPlatform.ANDROID, "ko"), (created.Data!.DeviceKey, created.Data.Platform, created.Data.Language));
        var (again, _) = await _k.PostAsync<GuestDeviceDto>($"{Root}/devices", key, new { });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, (await _factory.WithDbAsync(db => Task.FromResult(db.GuestDevices.Count(d => d.DeviceKey == key)))));

        // Thân yêu cầu có thể bỏ trống hẳn.
        using var empty = new HttpRequestMessage(HttpMethod.Post, $"{Root}/devices");
        empty.Headers.TryAddWithoutValidation("X-Device-Key", NewKey());
        Assert.Equal(HttpStatusCode.Created, (await _k.Http.SendAsync(empty)).StatusCode);
    }

    [Fact]
    public async Task The_device_key_must_be_a_version_4_uuid()
    {
        foreach (var bad in new[] { "abc", "3f2b8c1e-9a47-1d6b-8e10-5c7a9b2d4f61", "{3f2b8c1e-9a47-4d6b-8e10-5c7a9b2d4f61}", new string('a', 65) })
        {
            await _k.FailAsync(_k.PostAsync<object>($"{Root}/devices", bad), HttpStatusCode.BadRequest, MessageCodes.Invalid);
            await _k.FailAsync(_k.GetAsync<object>($"{Root}/device", bad), HttpStatusCode.BadRequest, MessageCodes.Invalid);
            await _k.FailAsync(_k.GetAsync<object>($"{Root}/history", bad), HttpStatusCode.BadRequest, MessageCodes.Invalid);
        }
        await _k.FailAsync(_k.GetAsync<object>($"{Root}/device", null), HttpStatusCode.BadRequest, MessageCodes.Required);
        await _k.FailAsync(_k.GetAsync<object>($"{Root}/history", null), HttpStatusCode.BadRequest, MessageCodes.Required);
        await _k.FailAsync(_k.SendAsync<object>(HttpMethod.Delete, $"{Root}/history", null), HttpStatusCode.BadRequest, MessageCodes.Required);
        await _k.FailAsync(_k.SendAsync<object>(HttpMethod.Post, $"{Root}/devices", NewKey(), new { }, platform: "NOKIA"), HttpStatusCode.BadRequest, MessageCodes.Invalid);
    }

    [Fact]
    public async Task Settings_are_remembered_and_an_unseen_key_is_read_without_creating_a_row()
    {
        var key = NewKey();
        var before = await _k.DeviceCountAsync();
        var unseen = await _k.ExpectAsync(_k.GetAsync<GuestDeviceDto>($"{Root}/device?lang=ja", key));
        Assert.Equal((false, "ja", false, null), (unseen.IsRegistered, unseen.Language, unseen.LocationAllowed, unseen.CreatedAt));
        Assert.Equal(before, await _k.DeviceCountAsync());

        var saved = await _k.ExpectAsync(_k.SendAsync<GuestDeviceDto>(HttpMethod.Put, $"{Root}/device", key, new { language = "zh-Hans", locationAllowed = true }, platform: "IOS"));
        Assert.Equal((true, ClientPlatform.IOS, "zh-Hans", true), (saved.IsRegistered, saved.Platform, saved.Language, saved.LocationAllowed));

        var partial = await _k.ExpectAsync(_k.SendAsync<GuestDeviceDto>(HttpMethod.Put, $"{Root}/device", key, new { locationAllowed = false }));
        Assert.Equal(("zh-Hans", false), (partial.Language, partial.LocationAllowed));
        var stored = await _k.DeviceAsync(key);
        Assert.Equal((LanguageCode.zh, false, ClientPlatform.IOS), (stored!.LanguageCode, stored.LocationAllowed, stored.Platform));
        Assert.NotNull(stored.LastSeenAt);

        await _k.FailAsync(_k.SendAsync<object>(HttpMethod.Put, $"{Root}/device", key, new { language = "fr" }), HttpStatusCode.BadRequest, MessageCodes.Invalid);
        await _k.FailAsync(_k.SendAsync<object>(HttpMethod.Put, $"{Root}/device", key, new { language = "zh-Hant" }), HttpStatusCode.BadRequest, MessageCodes.Invalid);
        await _k.FailAsync(_k.SendAsync<object>(HttpMethod.Put, $"{Root}/device", key, new { platform = "SYMBIAN" }), HttpStatusCode.BadRequest, MessageCodes.Invalid);
    }

    // ---- Tự ghi lịch sử khi gọi API công khai ----

    [Fact]
    public async Task Calling_the_public_api_with_a_device_key_records_search_view_summary_and_3d()
    {
        var admin = await _k.AdminAsync();
        var kit = new TranslationTestKit(_factory);
        var seeded = await kit.SeedAsync(admin);
        var heritage = seeded.Heritage;
        await kit.GenerateAsync(admin, heritage.Id);
        await kit.ApproveAsync(admin, heritage.Id, "vi", "SUMMARY");
        var models = new HeritageModelTestKit(_factory);
        await models.UploadModelAsync(admin, heritage.Id, show: true);

        var key = NewKey();
        var slug = heritage.Slug;
        var search = await _k.ExpectAsync(_k.GetAsync<DSVHVN.Application.Common.PagedResult<DSVHVN.Application.Discovery.HeritageCardDto>>(
            $"{PublicRoot}/heritages?keyword={Uri.EscapeDataString("hoàng thành " + seeded.Tag)}", key, platform: "ANDROID"));
        Assert.Contains(search.Items, i => i.Slug == slug);
        await _k.ExpectAsync(_k.GetAsync<object>($"{PublicRoot}/heritages/{slug}?lang=ko", key));
        await _k.ExpectAsync(_k.GetAsync<object>($"{PublicRoot}/heritages/{slug}/summary", key));
        await _k.ExpectAsync(_k.GetAsync<object>($"{PublicRoot}/heritages/{slug}/model", key));

        var rows = await _k.ActivitiesAsync(key);
        Assert.Equal([GuestActivityType.SEARCH, GuestActivityType.VIEW, GuestActivityType.READ_SUMMARY, GuestActivityType.VIEW_3D], rows.Select(r => r.ActivityType));
        Assert.Equal("hoàng thành " + seeded.Tag, rows[0].SearchQuery);
        Assert.All(rows.Skip(1), r => Assert.Equal(heritage.Id, r.HeritageId));
        Assert.Equal([LanguageCode.vi, LanguageCode.ko, LanguageCode.vi, LanguageCode.vi], rows.Select(r => r.LanguageCode));   // ngôn ngữ đang dùng lúc thực hiện
        var device = await _k.DeviceAsync(key);
        Assert.Equal(ClientPlatform.ANDROID, device!.Platform);

        // Lịch sử đọc lại: mới nhất trước, thẻ di sản theo ngôn ngữ yêu cầu.
        var history = await _k.ExpectAsync(_k.GetAsync<PagedResult<GuestHistoryItemDto>>($"{Root}/history?lang=en", key));
        Assert.Equal([GuestActivityType.VIEW_3D, GuestActivityType.READ_SUMMARY, GuestActivityType.VIEW, GuestActivityType.SEARCH], history.Items.Select(i => i.Type));
        Assert.Equal(slug, history.Items[0].Heritage!.Slug);
        Assert.Null(history.Items[3].Heritage);
        Assert.Equal("hoàng thành " + seeded.Tag, history.Items[3].Query);
    }

    [Fact]
    public async Task Requests_that_do_not_succeed_or_carry_no_key_record_nothing()
    {
        var admin = await _k.AdminAsync();
        var published = await _k.HeritageAsync(admin, "Văn Miếu");
        var draft = await _k.Discovery.DraftAsync(admin, new DiscoveryTestKit.Spec("Nháp " + DiscoveryTestKit.Unique()));
        var key = NewKey();
        var devicesBefore = await _k.DeviceCountAsync();

        // Không gửi mã: không ghi, không tạo thiết bị.
        await _k.ExpectAsync(_k.GetAsync<object>($"{PublicRoot}/heritages/{published.Slug}", null));
        await _k.ExpectAsync(_k.GetAsync<object>($"{PublicRoot}/heritages?keyword=van", null));
        Assert.Equal(devicesBefore, await _k.DeviceCountAsync());

        // Di sản chưa xuất bản, không có mô hình, không có tóm tắt, từ khóa ngắn: lỗi thì không ghi.
        await _k.FailAsync(_k.GetAsync<object>($"{PublicRoot}/heritages/{draft.Slug}", key), HttpStatusCode.NotFound, "BLOCKED");
        await _k.FailAsync(_k.GetAsync<object>($"{PublicRoot}/heritages/{published.Slug}/model", key), HttpStatusCode.NotFound, "MODEL_UNAVAILABLE");
        await _k.FailAsync(_k.GetAsync<object>($"{PublicRoot}/heritages/{published.Slug}/summary", key), HttpStatusCode.NotFound, "SUMMARY_UNAVAILABLE");
        await _k.FailAsync(_k.GetAsync<object>($"{PublicRoot}/heritages?keyword=a", key), HttpStatusCode.BadRequest, MessageCodes.Invalid);
        // Tìm không có từ khóa (chỉ lọc) hoặc xem bản đồ, dòng thời gian, trang chủ không phải lượt tìm hay xem.
        await _k.ExpectAsync(_k.GetAsync<object>($"{PublicRoot}/heritages?region=Bac", key));
        await _k.ExpectAsync(_k.GetAsync<object>($"{PublicRoot}/heritages/map", key));
        await _k.ExpectAsync(_k.GetAsync<object>($"{PublicRoot}/heritages/{published.Slug}/timeline", key));
        await _k.ExpectAsync(_k.GetAsync<object>($"{PublicRoot}/home", key));
        Assert.Empty(await _k.ActivitiesAsync(key));

        // Mã sai định dạng thì từ chối ngay, không làm việc chính.
        await _k.FailAsync(_k.GetAsync<object>($"{PublicRoot}/heritages/{published.Slug}", "khong-phai-uuid"), HttpStatusCode.BadRequest, MessageCodes.Invalid);
        await _k.FailAsync(_k.SendAsync<object>(HttpMethod.Get, $"{PublicRoot}/heritages/{published.Slug}", key, platform: "NOKIA"), HttpStatusCode.BadRequest, MessageCodes.Invalid);
    }

    [Fact]
    public async Task Searching_records_only_the_first_page_and_a_repeat_within_ten_minutes_is_recorded_once()
    {
        var admin = await _k.AdminAsync();
        var tag = DiscoveryTestKit.Unique();
        var heritage = await _k.HeritageAsync(admin, "Thành cổ " + tag);
        var key = NewKey();

        var query = $"{PublicRoot}/heritages?keyword={Uri.EscapeDataString("thành cổ " + tag)}";
        await _k.ExpectAsync(_k.GetAsync<object>(query, key));
        await _k.ExpectAsync(_k.GetAsync<object>(query, key));                       // lặp trong 10 phút
        await _k.ExpectAsync(_k.GetAsync<object>(query + "&page=2&pageSize=1", key));    // chuyển trang không phải lượt tìm mới
        await _k.ExpectAsync(_k.GetAsync<object>($"{PublicRoot}/heritages/{heritage.Slug}", key));
        await _k.ExpectAsync(_k.GetAsync<object>($"{PublicRoot}/heritages/{heritage.Slug}", key));   // xem lặp

        var rows = await _k.ActivitiesAsync(key);
        Assert.Equal([GuestActivityType.SEARCH, GuestActivityType.VIEW], rows.Select(r => r.ActivityType));
    }

    [Fact]
    public async Task A_student_is_never_recorded_and_cannot_use_the_guest_functions()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageAsync(admin, "Hội An");
        var lessons = new LessonTestKit(_factory);
        var (school, schoolClass) = await lessons.SchoolWithClassAsync(2);
        var student = await lessons.StudentTokenAsync(school.TeacherToken, schoolClass.Id);
        var key = NewKey();
        var devicesBefore = await _k.DeviceCountAsync();

        // Xem trang di sản công khai khi đã đăng nhập: không ghi, không tạo thiết bị, vẫn nhận tiếng Việt.
        await _k.ExpectAsync(_k.GetAsync<object>($"{PublicRoot}/heritages/{heritage.Slug}?lang=en", key, student));
        await _k.ExpectAsync(_k.GetAsync<object>($"{PublicRoot}/heritages?keyword=hoi", key, student));
        Assert.Equal(devicesBefore, await _k.DeviceCountAsync());
        Assert.Empty(await _k.ActivitiesAsync(key));

        // Ghi tay từ phiên học sinh: không báo lỗi, không ghi.
        var manual = await _k.ExpectAsync(_k.PostAsync<GuestActivityResultDto>($"{Root}/activities", key, new { type = "LISTEN", slug = heritage.Slug }, student));
        Assert.Equal((false, "STUDENT_SESSION"), (manual.Recorded, manual.Reason));
        Assert.Empty(await _k.ActivitiesAsync(key));

        // Chức năng riêng của khách: 403 GUEST_ONLY.
        await _k.FailAsync(_k.GetAsync<object>($"{Root}/history", key, student), HttpStatusCode.Forbidden, MessageCodes.GuestOnly);
        await _k.FailAsync(_k.SendAsync<object>(HttpMethod.Delete, $"{Root}/history", key, token: student), HttpStatusCode.Forbidden, MessageCodes.GuestOnly);
        await _k.FailAsync(_k.GetAsync<object>($"{Root}/device", key, student), HttpStatusCode.Forbidden, MessageCodes.GuestOnly);
        await _k.FailAsync(_k.PostAsync<object>($"{Root}/devices", key, new { }, student), HttpStatusCode.Forbidden, MessageCodes.GuestOnly);
        await _k.FailAsync(_k.GetAsync<object>($"{Root}/recommendations", key, student), HttpStatusCode.Forbidden, MessageCodes.GuestOnly);
        await _k.FailAsync(_k.PostAsync<object>($"{Root}/nearby", null, new { latitude = 21.03, longitude = 105.85 }, student), HttpStatusCode.Forbidden, MessageCodes.GuestOnly);
        Assert.Equal(devicesBefore, await _k.DeviceCountAsync());
    }

    // ---- Ghi tay ----

    [Fact]
    public async Task The_listen_activity_is_recorded_by_the_client_and_a_repeat_within_ten_minutes_is_not()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageAsync(admin, "Cố đô Huế");
        var draft = await _k.Discovery.DraftAsync(admin, new DiscoveryTestKit.Spec("Chưa mở " + DiscoveryTestKit.Unique()));
        var key = NewKey();

        var first = await _k.RecordAsync(key, "LISTEN", heritage.Slug, lang: "ja");
        Assert.Equal((true, null), (first.Recorded, first.Reason));
        var repeat = await _k.RecordAsync(key, "LISTEN", heritage.Slug);
        Assert.Equal((false, "DUPLICATE"), (repeat.Recorded, repeat.Reason));
        Assert.True((await _k.RecordAsync(key, "VIEW_3D", heritage.Slug!.ToUpperInvariant())).Recorded);   // đường dẫn không phân biệt hoa thường
        Assert.True((await _k.RecordAsync(key, "SEARCH", query: "  cố   đô ")).Recorded);

        var rows = await _k.ActivitiesAsync(key);
        Assert.Equal([GuestActivityType.LISTEN, GuestActivityType.VIEW_3D, GuestActivityType.SEARCH], rows.Select(r => r.ActivityType));
        Assert.Equal(LanguageCode.ja, rows[0].LanguageCode);
        Assert.Equal("cố đô", rows[2].SearchQuery);

        await _k.FailAsync(_k.PostAsync<object>($"{Root}/activities", key, new { type = "LISTEN", slug = draft.Slug }), HttpStatusCode.NotFound, "BLOCKED");
        await _k.FailAsync(_k.PostAsync<object>($"{Root}/activities", key, new { type = "LISTEN", slug = "khong-co" }), HttpStatusCode.NotFound, "BLOCKED");
        await _k.FailAsync(_k.PostAsync<object>($"{Root}/activities", key, new { type = "LISTEN" }), HttpStatusCode.BadRequest, MessageCodes.Required);
        await _k.FailAsync(_k.PostAsync<object>($"{Root}/activities", key, new { type = "SEARCH", query = "x" }), HttpStatusCode.BadRequest, MessageCodes.Invalid);
        await _k.FailAsync(_k.PostAsync<object>($"{Root}/activities", key, new { type = "SEARCH", query = new string('x', 101) }), HttpStatusCode.BadRequest, MessageCodes.Invalid);
        await _k.FailAsync(_k.PostAsync<object>($"{Root}/activities", key, new { slug = heritage.Slug }), HttpStatusCode.BadRequest, MessageCodes.Required);
        await _k.FailAsync(_k.PostAsync<object>($"{Root}/activities", key, new { type = "DANCE", slug = heritage.Slug }), HttpStatusCode.BadRequest, MessageCodes.Invalid);
        await _k.FailAsync(_k.PostAsync<object>($"{Root}/activities", null, new { type = "LISTEN", slug = heritage.Slug }), HttpStatusCode.BadRequest, MessageCodes.Required);
        Assert.Equal(3, (await _k.ActivitiesAsync(key)).Count);
    }

    // ---- Xem và xóa lịch sử ----

    [Fact]
    public async Task History_is_private_to_the_key_pages_and_is_never_cached()
    {
        var admin = await _k.AdminAsync();
        var a = await _k.HeritageAsync(admin, "Phố cổ");
        var b = await _k.HeritageAsync(admin, "Chùa Một Cột");
        var mine = NewKey();
        var other = NewKey();
        await _k.SeedAsync(mine,
            new Seed(GuestActivityType.VIEW, a.Id, Minutes(50)), new Seed(GuestActivityType.LISTEN, b.Id, Minutes(40)),
            new Seed(GuestActivityType.SEARCH, null, Minutes(30), "phố"), new Seed(GuestActivityType.VIEW_3D, a.Id, Minutes(20)),
            new Seed(GuestActivityType.READ_SUMMARY, b.Id, Minutes(10)));
        await _k.SeedAsync(other, new Seed(GuestActivityType.VIEW, a.Id, Minutes(5)));

        var first = await _k.HistoryAsync(mine, "pageSize=2");
        Assert.Equal((1, 2, 5, 3), (first.Page, first.PageSize, first.TotalItems, first.TotalPages));
        Assert.Equal([GuestActivityType.READ_SUMMARY, GuestActivityType.VIEW_3D], first.Items.Select(i => i.Type));
        var last = await _k.HistoryAsync(mine, "pageSize=2&page=3");
        Assert.Equal([GuestActivityType.VIEW], last.Items.Select(i => i.Type));
        Assert.Equal(1, (await _k.HistoryAsync(other)).TotalItems);
        Assert.Equal(0, (await _k.HistoryAsync(NewKey())).TotalItems);       // mã chưa từng thấy

        await _k.FailAsync(_k.GetAsync<object>($"{Root}/history?pageSize=101", mine), HttpStatusCode.BadRequest, MessageCodes.Invalid);
        await _k.FailAsync(_k.GetAsync<object>($"{Root}/history?page=0", mine), HttpStatusCode.BadRequest, MessageCodes.Invalid);

        var response = await _k.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{Root}/history") { Headers = { { "X-Device-Key", mine } } });
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Heritage_that_is_archived_disappears_from_the_history_and_activities_past_ninety_days_are_not_shown()
    {
        var admin = await _k.AdminAsync();
        var visible = await _k.HeritageAsync(admin, "Đền Hùng");
        var archived = await _k.HeritageAsync(admin, "Tháp Chàm");
        var key = NewKey();
        await _k.SeedAsync(key,
            new Seed(GuestActivityType.VIEW, visible.Id, Days(89)), new Seed(GuestActivityType.VIEW, archived.Id, Days(10)),
            new Seed(GuestActivityType.VIEW, visible.Id, Days(91)), new Seed(GuestActivityType.SEARCH, null, Days(100), "quá cũ"));
        await _k.ArchiveAsync(admin, archived.Id);

        var history = await _k.HistoryAsync(key);
        Assert.Equal(1, history.TotalItems);
        Assert.Equal(visible.Slug, history.Items.Single().Heritage!.Slug);
    }

    [Fact]
    public async Task A_history_item_is_localized_with_a_fallback_flag_when_there_is_no_translation()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageAsync(admin, "Chùa Hương");
        var key = NewKey();
        await _k.SeedAsync(key, new Seed(GuestActivityType.VIEW, heritage.Id, Minutes(1)));

        var vi = (await _k.HistoryAsync(key)).Items.Single().Heritage!;
        var ko = (await _k.HistoryAsync(key, "lang=ko")).Items.Single().Heritage!;
        Assert.False(vi.IsFallback);
        Assert.True(ko.IsFallback);                    // chưa có bản dịch: hiện tiếng Việt kèm cờ lùi
        Assert.Equal(vi.Name, ko.Name);
    }

    [Fact]
    public async Task Deleting_one_item_or_all_removes_the_rows_for_real_and_only_on_the_own_device()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.HeritageAsync(admin, "Hồ Gươm");
        var mine = NewKey();
        var other = NewKey();
        await _k.SeedAsync(mine, new Seed(GuestActivityType.VIEW, heritage.Id, Minutes(30)), new Seed(GuestActivityType.SEARCH, null, Minutes(20), "gươm"),
            new Seed(GuestActivityType.LISTEN, heritage.Id, Minutes(10)));
        await _k.SeedAsync(other, new Seed(GuestActivityType.VIEW, heritage.Id, Minutes(5)));
        var ids = (await _k.HistoryAsync(mine)).Items.Select(i => i.Id).ToList();
        var othersId = (await _k.HistoryAsync(other)).Items.Single().Id;

        // Mục của thiết bị khác như không tồn tại.
        await _k.FailAsync(_k.SendAsync<object>(HttpMethod.Delete, $"{Root}/history/{othersId}", mine), HttpStatusCode.NotFound, "BLOCKED");
        await _k.FailAsync(_k.SendAsync<object>(HttpMethod.Delete, $"{Root}/history/999999", mine), HttpStatusCode.NotFound, "BLOCKED");
        await _k.FailAsync(_k.SendAsync<object>(HttpMethod.Delete, $"{Root}/history/{ids[0]}", NewKey()), HttpStatusCode.NotFound, "BLOCKED");
        Assert.Equal(3, (await _k.ActivitiesAsync(mine)).Count);

        var one = await _k.ExpectAsync(_k.SendAsync<object>(HttpMethod.Delete, $"{Root}/history/{ids[0]}", mine));
        Assert.Null(one);
        Assert.Equal(2, (await _k.ActivitiesAsync(mine)).Count);

        var all = await _k.ExpectAsync(_k.SendAsync<GuestDeletedDto>(HttpMethod.Delete, $"{Root}/history", mine));
        Assert.Equal(2, all.Deleted);
        Assert.Empty(await _k.ActivitiesAsync(mine));
        Assert.Single(await _k.ActivitiesAsync(other));
        Assert.NotNull(await _k.DeviceAsync(mine));          // cài đặt thiết bị còn nguyên
        Assert.Equal(0, (await _k.ExpectAsync(_k.SendAsync<GuestDeletedDto>(HttpMethod.Delete, $"{Root}/history", NewKey()))).Deleted);
    }
}
