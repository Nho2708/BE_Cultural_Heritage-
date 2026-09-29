using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.Heritages;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Questions;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Kho di sản qua HTTP (quản trị hệ thống): quyền, tạo và sửa bản nháp, điều kiện xuất bản, ẩn và hiện lại, nhật ký thao tác,
/// timeline, ảnh có ghi công, ảnh đại diện, nguồn tham chiếu, xóa mềm, tìm và lọc.
/// </summary>
public sealed class HeritageApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Api = "/api/v1/admin";

    private readonly PlatformClient _api = new(factory);
    private HttpClient Http => _api.Http;

    // ---- Dụng cụ ----

    private static string Words(int count) => string.Join(' ', Enumerable.Repeat("từ", count));

    private static string Unique() => PlatformClient.Unique();

    private async Task<int> ProvinceIdAsync(string code = "01", string name = "Thành phố Hà Nội", string region = "Bắc") =>
        await factory.WithDbAsync(async db =>
        {
            var province = await db.Provinces.FirstOrDefaultAsync(p => p.Code == code);
            if (province is null)
            {
                province = new Province { Code = code, Name = name, Region = region };
                db.Provinces.Add(province);
                await db.SaveChangesAsync();
            }
            return province.Id;
        });

    private static object FullBody(string name, int provinceId, string? slug = null, string type = "TANGIBLE") => new
    {
        name,
        slug,
        shortDescription = "Mô tả ngắn của " + name,
        content = Words(210),
        heritageType = type,
        recognitionLevel = "NATIONAL",
        recognizedYear = 1999,
        provinceId,
        address = "1 Phố Cổ, Hà Nội",
        latitude = 21.033333m,
        longitude = 105.85m,
        placeId = "Q1",
    };

    private static object ImageBody(string url, bool thumbnail = false, string type = "IMAGE", string license = "CC BY-SA 4.0") => new
    {
        mediaType = type,
        url,
        caption = "Ảnh minh họa",
        author = "Nguyễn Văn An",
        license,
        source = "Wikimedia Commons",
        setAsThumbnail = thumbnail ? true : (bool?)null,
    };

    private static object GovDocBody(string url = "https://vanban.chinhphu.vn/qd-1272") => new
    {
        title = "Quyết định số 1272/QĐ-TTg về xếp hạng di tích quốc gia đặc biệt — Thủ tướng Chính phủ, truy cập 26/09/2026",
        url,
        sourceType = "GOV_DOC",
    };

    private static async Task<T> Expect<T>(Task<(HttpResponseMessage Response, Envelope<T> Body)> call,
        HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (res, body) = await call;
        Assert.True(res.StatusCode == expected, $"Mong {(int)expected}, nhận {(int)res.StatusCode} {body.ErrorCode}: {body.Message}");
        return body.Data!;
    }

    private static async Task<Envelope<object>> Fail(Task<(HttpResponseMessage Response, Envelope<object> Body)> call,
        HttpStatusCode expected, string errorCode)
    {
        var (res, body) = await call;
        Assert.Equal((expected, errorCode), (res.StatusCode, body.ErrorCode));
        Assert.False(body.Success);
        return body;
    }

    private Task<HeritageDetailDto> GetAsync(string token, long id) =>
        Expect(Http.GetAsync<HeritageDetailDto>($"{Api}/heritages/{id}", token));

    /// <summary>Di sản DRAFT có đủ thông tin, một ảnh đại diện và một nguồn: xuất bản được ngay.</summary>
    private async Task<HeritageDetailDto> PublishableDraftAsync(string token, string? name = null)
    {
        var province = await ProvinceIdAsync();
        name ??= "Di sản thử " + Unique();
        var heritage = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", FullBody(name, province), token),
            HttpStatusCode.Created);
        await Expect(Http.PostAsync<MediaDto>($"{Api}/heritages/{heritage.Id}/media",
            ImageBody($"https://upload.wikimedia.org/{Unique()}.jpg", thumbnail: true), token), HttpStatusCode.Created);
        await Expect(Http.PostAsync<ReferenceDto>($"{Api}/heritages/{heritage.Id}/references", GovDocBody(), token), HttpStatusCode.Created);
        return await GetAsync(token, heritage.Id);
    }

    private Task<List<AuditLog>> AuditOfAsync(long heritageId) => factory.WithDbAsync(db => db.AuditLogs
        .Where(l => l.TargetType == AuditTargets.Heritages && l.TargetId == heritageId).OrderBy(l => l.Id).ToListAsync());

    // ---- Quyền ----

    [Fact]
    public async Task Only_the_system_admin_can_use_the_heritage_api()
    {
        var (_, orgAdmin) = await _api.OrgWithAdminAsync();
        var (_, teacher) = await _api.TeacherAsync(orgAdmin);

        var (anonymous, _) = await Http.GetAsync<object>($"{Api}/heritages");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        foreach (var token in new[] { orgAdmin, teacher })
        {
            await Fail(Http.GetAsync<object>($"{Api}/heritages", token), HttpStatusCode.Forbidden, "FORBIDDEN");
            await Fail(Http.GetAsync<object>($"{Api}/provinces", token), HttpStatusCode.Forbidden, "FORBIDDEN");
            await Fail(Http.PostAsync<object>($"{Api}/heritages", new { name = "Không được tạo" }, token), HttpStatusCode.Forbidden, "FORBIDDEN");
            await Fail(Http.PutAsync<object>($"{Api}/timelines/1", new { name = "x" }, token), HttpStatusCode.Forbidden, "FORBIDDEN");
            await Fail(Http.DeleteAsync<object>($"{Api}/media/1", token), HttpStatusCode.Forbidden, "FORBIDDEN");
            await Fail(Http.PostAsync<object>($"{Api}/heritages/1/publish", null, token), HttpStatusCode.Forbidden, "FORBIDDEN");
        }
    }

    [Fact]
    public async Task Provinces_are_listed_by_code_with_their_region()
    {
        var admin = await _api.AdminTokenAsync();
        await ProvinceIdAsync("02", "Tỉnh Tuyên Quang", "Bắc");
        await ProvinceIdAsync("01");

        var provinces = await Expect(Http.GetAsync<List<ProvinceDto>>($"{Api}/provinces", admin));

        var codes = provinces.Select(p => p.Code).ToList();
        Assert.Equal(codes.Order(StringComparer.Ordinal), codes);
        Assert.Contains(provinces, p => p is { Code: "02", Name: "Tỉnh Tuyên Quang", Region: "Bắc" });
    }

    // ---- Tạo và sửa bản nháp ----

    [Fact]
    public async Task A_draft_needs_only_a_name_and_gets_a_unique_slug_from_it()
    {
        var admin = await _api.AdminTokenAsync();
        var name = "Đền Hùng " + Unique();

        var first = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", new { name }, admin), HttpStatusCode.Created);
        var second = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", new { name }, admin), HttpStatusCode.Created);

        Assert.Equal(ContentStatus.DRAFT, first.Status);
        Assert.Equal(HeritageRules.Slugify(name), first.Slug);
        Assert.Equal(first.Slug + "-2", second.Slug);
        Assert.NotNull(first.CreatedAt);
        Assert.False(first.Readiness.IsReady);
        Assert.Contains(first.Readiness.Missing, m => m is { Field: "shortDescription", Label: "mô tả ngắn" });

        var suggestion = await Expect(Http.GetAsync<string>($"{Api}/heritages/suggest-slug?name={Uri.EscapeDataString(name)}", admin));
        Assert.Equal(first.Slug + "-3", suggestion);
        var own = await Expect(Http.GetAsync<string>(
            $"{Api}/heritages/suggest-slug?name={Uri.EscapeDataString(name)}&heritageId={first.Id}", admin));
        Assert.Equal(first.Slug, own);          // chính di sản đang sửa không tính là "đã có người dùng"
        await Fail(Http.GetAsync<object>($"{Api}/heritages/suggest-slug?name=%20%20", admin), HttpStatusCode.BadRequest, "REQUIRED");
    }

    [Fact]
    public async Task An_explicit_slug_must_be_well_formed_and_not_used_by_another_heritage()
    {
        var admin = await _api.AdminTokenAsync();
        var slug = "duong-dan-" + Unique();
        await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", new { name = "Di sản A", slug }, admin), HttpStatusCode.Created);

        var duplicate = await Fail(Http.PostAsync<object>($"{Api}/heritages", new { name = "Di sản B", slug }, admin),
            HttpStatusCode.Conflict, "DUPLICATE");
        Assert.Equal("Đường dẫn không hợp lệ: đã có di sản khác dùng đường dẫn này.", duplicate.Errors!["slug"]);

        var bad = await Fail(Http.PostAsync<object>($"{Api}/heritages", new { name = "Di sản C", slug = "Đường Dẫn Sai" }, admin),
            HttpStatusCode.BadRequest, "INVALID");
        Assert.StartsWith("Đường dẫn không hợp lệ", bad.Errors!["slug"]);
    }

    [Fact]
    public async Task Invalid_fields_are_reported_under_the_field_in_vietnamese()
    {
        var admin = await _api.AdminTokenAsync();
        var thisYear = VietnamTime.Today(DateTimeOffset.UtcNow).Year;

        var missing = await Fail(Http.PostAsync<object>($"{Api}/heritages", new { name = " " }, admin), HttpStatusCode.BadRequest, "REQUIRED");
        Assert.Equal("Vui lòng nhập tên di sản.", missing.Errors!["name"]);

        var bad = await Fail(Http.PostAsync<object>($"{Api}/heritages", new
        {
            name = new string('a', 256),
            shortDescription = new string('b', 301),
            recognizedYear = thisYear + 1,
            provinceId = 99_999,
            latitude = 30m,
            longitude = 90m,
        }, admin), HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal("Tên di sản không hợp lệ: tối đa 255 ký tự.", bad.Errors!["name"]);
        Assert.Equal("Mô tả ngắn không hợp lệ: tối đa 300 ký tự.", bad.Errors["shortDescription"]);
        Assert.StartsWith("Năm công nhận không hợp lệ", bad.Errors["recognizedYear"]);
        Assert.Equal("Vĩ độ không hợp lệ: phải từ 8,0 đến 23,5.", bad.Errors["latitude"]);
        Assert.Equal("Kinh độ không hợp lệ: phải từ 102,0 đến 117,5.", bad.Errors["longitude"]);

        var province = await Fail(Http.PostAsync<object>($"{Api}/heritages", new { name = "Di sản D", provinceId = 99_999 }, admin),
            HttpStatusCode.BadRequest, "INVALID");
        Assert.StartsWith("Tỉnh/thành không hợp lệ", province.Errors!["provinceId"]);

        var onlyOne = await Fail(Http.PostAsync<object>($"{Api}/heritages", new { name = "Di sản E", latitude = 21m }, admin),
            HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal("Tọa độ không hợp lệ: phải nhập đủ cả vĩ độ và kinh độ hoặc bỏ trống cả hai.", onlyOne.Errors!["latitude"]);

        await Fail(Http.PostAsync<object>($"{Api}/heritages", new { name = "Di sản F", heritageType = "KHONG_CO" }, admin),
            HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.PostAsync<object>($"{Api}/heritages", new { name = "Di sản G", heritageType = 99 }, admin),
            HttpStatusCode.BadRequest, "INVALID");
    }

    [Fact]
    public async Task Updating_replaces_the_general_information_and_keeps_the_slug_when_none_is_sent()
    {
        var admin = await _api.AdminTokenAsync();
        var province = await ProvinceIdAsync();
        var created = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", new { name = "Văn Miếu " + Unique() }, admin),
            HttpStatusCode.Created);

        var body = FullBody("Văn Miếu – Quốc Tử Giám", province);
        var updated = await Expect(Http.PutAsync<HeritageDetailDto>($"{Api}/heritages/{created.Id}", body, admin));

        Assert.Equal(created.Slug, updated.Slug);
        Assert.Equal(("Văn Miếu – Quốc Tử Giám", HeritageType.TANGIBLE, RecognitionLevel.NATIONAL, 1999),
            (updated.Name, updated.HeritageType, updated.RecognitionLevel, updated.RecognizedYear));
        Assert.Equal(province, updated.Province!.Id);
        Assert.Equal((21.033333m, 105.85m), (updated.Latitude, updated.Longitude));
        Assert.NotNull(updated.UpdatedAt);

        var renamed = await Expect(Http.PutAsync<HeritageDetailDto>($"{Api}/heritages/{created.Id}",
            FullBody("Văn Miếu – Quốc Tử Giám", province, slug: "van-mieu-" + Unique()), admin));
        Assert.StartsWith("van-mieu-", renamed.Slug);

        await Fail(Http.PutAsync<object>($"{Api}/heritages/999999", body, admin), HttpStatusCode.NotFound, "BLOCKED");
        await Fail(Http.PutAsync<object>($"{Api}/heritages/{created.Id}", new { name = "" }, admin), HttpStatusCode.BadRequest, "REQUIRED");
    }

    [Fact]
    public async Task Coordinates_are_rounded_to_six_decimals()
    {
        var admin = await _api.AdminTokenAsync();
        var created = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages",
            new { name = "Tọa độ " + Unique(), latitude = 21.0123456789m, longitude = 105.1234564m }, admin), HttpStatusCode.Created);
        Assert.Equal((21.012346m, 105.123456m), (created.Latitude, created.Longitude));
    }

    // ---- Xuất bản, ẩn, hiện lại ----

    [Fact]
    public async Task Publishing_lists_what_is_missing_and_changes_nothing_until_everything_is_there()
    {
        var admin = await _api.AdminTokenAsync();
        var created = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages",
            new { name = "Thiếu nhiều thứ " + Unique() }, admin), HttpStatusCode.Created);

        var refused = await Fail(Http.PostAsync<object>($"{Api}/heritages/{created.Id}/publish", null, admin),
            HttpStatusCode.UnprocessableEntity, "NOT_READY");
        Assert.StartsWith("Chưa đủ điều kiện xuất bản di sản. Còn thiếu: mô tả ngắn, nội dung tối thiểu 200 từ, loại di sản", refused.Message);
        Assert.Contains("nguồn tham chiếu (ít nhất một)", refused.Message);
        Assert.Equal(
            ["shortDescription", "content", "heritageType", "recognitionLevel", "thumbnailUrl", "provinceId", "references"],
            refused.Errors!.Keys);
        Assert.Equal(ContentStatus.DRAFT, (await GetAsync(admin, created.Id)).Status);
        Assert.Empty(await AuditOfAsync(created.Id));
    }

    [Fact]
    public async Task A_source_is_required_and_the_thumbnail_must_come_from_the_heritage_own_library()
    {
        var admin = await _api.AdminTokenAsync();
        var province = await ProvinceIdAsync();
        var h = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", FullBody("Đủ thông tin " + Unique(), province), admin),
            HttpStatusCode.Created);
        var image = await Expect(Http.PostAsync<MediaDto>($"{Api}/heritages/{h.Id}/media", ImageBody($"https://x.org/{Unique()}.jpg"), admin),
            HttpStatusCode.Created);
        Assert.False(image.IsThumbnail);

        var noThumbnail = await Fail(Http.PostAsync<object>($"{Api}/heritages/{h.Id}/publish", null, admin),
            HttpStatusCode.UnprocessableEntity, "NOT_READY");
        Assert.Equal(["thumbnailUrl", "references"], noThumbnail.Errors!.Keys);

        // Ảnh của di sản khác không chọn được làm ảnh đại diện.
        var other = await PublishableDraftAsync(admin);
        var otherImage = other.Media.Single();
        await Fail(Http.PutAsync<object>($"{Api}/heritages/{h.Id}/thumbnail", new { mediaId = otherImage.Id }, admin),
            HttpStatusCode.BadRequest, "INVALID");

        var withThumbnail = await Expect(Http.PutAsync<HeritageDetailDto>($"{Api}/heritages/{h.Id}/thumbnail", new { mediaId = image.Id }, admin));
        Assert.Equal(image.Url, withThumbnail.ThumbnailUrl);
        Assert.True(withThumbnail.Media.Single().IsThumbnail);

        var noSource = await Fail(Http.PostAsync<object>($"{Api}/heritages/{h.Id}/publish", null, admin),
            HttpStatusCode.UnprocessableEntity, "NOT_READY");
        Assert.Equal(["references"], noSource.Errors!.Keys);
        Assert.Contains("nguồn tham chiếu", noSource.Message);

        await Expect(Http.PostAsync<ReferenceDto>($"{Api}/heritages/{h.Id}/references", GovDocBody(), admin), HttpStatusCode.Created);
        var published = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages/{h.Id}/publish", null, admin));
        Assert.Equal(ContentStatus.PUBLISHED, published.Status);
        Assert.True(published.Readiness.IsReady);
    }

    [Fact]
    public async Task Publish_archive_and_restore_follow_the_state_machine_and_are_audited_with_the_actor()
    {
        var admin = await _api.AdminTokenAsync();
        var h = await PublishableDraftAsync(admin);
        Assert.Equal(ContentStatus.DRAFT, h.Status);
        Assert.True(h.Readiness.IsReady);

        // Chưa xuất bản thì không ẩn, không hiện lại được.
        await Fail(Http.PostAsync<object>($"{Api}/heritages/{h.Id}/archive", null, admin), HttpStatusCode.UnprocessableEntity, "BLOCKED");
        await Fail(Http.PostAsync<object>($"{Api}/heritages/{h.Id}/restore", null, admin), HttpStatusCode.UnprocessableEntity, "BLOCKED");

        var published = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages/{h.Id}/publish", null, admin));
        Assert.Equal(ContentStatus.PUBLISHED, published.Status);
        var again = await Fail(Http.PostAsync<object>($"{Api}/heritages/{h.Id}/publish", null, admin), HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Equal("Không thể xuất bản di sản. Di sản đang được xuất bản.", again.Message);

        var archived = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages/{h.Id}/archive", null, admin));
        Assert.Equal(ContentStatus.ARCHIVED, archived.Status);
        var publishArchived = await Fail(Http.PostAsync<object>($"{Api}/heritages/{h.Id}/publish", null, admin),
            HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("hiện lại", publishArchived.Message);

        var restored = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages/{h.Id}/restore", null, admin));
        Assert.Equal(ContentStatus.PUBLISHED, restored.Status);

        var log = await AuditOfAsync(h.Id);
        Assert.Equal([AuditAction.HERITAGE_PUBLISHED, AuditAction.HERITAGE_ARCHIVED, AuditAction.HERITAGE_PUBLISHED], log.Select(l => l.Action));
        var adminId = await factory.WithDbAsync(db => db.Users.Where(u => u.Username == ApiFactory.AdminUsername).Select(u => u.Id).SingleAsync());
        Assert.All(log, l => Assert.Equal((adminId, ActorRole.ADMIN, "203.0.113.10"), (l.UserId!.Value, l.ActorRole, l.IpAddress)));
        Assert.Contains("\"status\":\"DRAFT\"", log[0].OldValue);
        Assert.Contains("\"status\":\"PUBLISHED\"", log[0].NewValue);
        Assert.Contains($"\"slug\":\"{h.Slug}\"", log[0].NewValue);
        Assert.Contains("\"status\":\"ARCHIVED\"", log[1].NewValue);
    }

    [Fact]
    public async Task Restoring_checks_the_conditions_again()
    {
        var admin = await _api.AdminTokenAsync();
        var h = await PublishableDraftAsync(admin);
        await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages/{h.Id}/publish", null, admin));
        await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages/{h.Id}/archive", null, admin));

        // Đang bị ẩn thì xóa nguồn cuối cùng được; hiện lại phải báo còn thiếu nguồn.
        await Expect(Http.DeleteAsync<object>($"{Api}/heritage-references/{h.References.Single().Id}", admin));
        var refused = await Fail(Http.PostAsync<object>($"{Api}/heritages/{h.Id}/restore", null, admin),
            HttpStatusCode.UnprocessableEntity, "NOT_READY");
        Assert.StartsWith("Chưa đủ điều kiện hiện lại di sản.", refused.Message);
        Assert.Equal(ContentStatus.ARCHIVED, (await GetAsync(admin, h.Id)).Status);
    }

    [Fact]
    public async Task A_published_heritage_can_be_edited_but_must_stay_publishable()
    {
        var admin = await _api.AdminTokenAsync();
        var province = await ProvinceIdAsync();
        var h = await PublishableDraftAsync(admin);
        await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages/{h.Id}/publish", null, admin));

        var edited = await Expect(Http.PutAsync<HeritageDetailDto>($"{Api}/heritages/{h.Id}",
            FullBody("Tên mới " + Unique(), province, slug: h.Slug), admin));
        Assert.Equal(ContentStatus.PUBLISHED, edited.Status);
        Assert.StartsWith("Tên mới", edited.Name);

        var refused = await Fail(Http.PutAsync<object>($"{Api}/heritages/{h.Id}",
            new { name = "Bị mất mô tả", slug = h.Slug, shortDescription = "", content = Words(10), heritageType = "TANGIBLE" }, admin),
            HttpStatusCode.UnprocessableEntity, "NOT_READY");
        Assert.StartsWith("Chưa đủ điều kiện lưu di sản đã xuất bản.", refused.Message);
        Assert.Contains("shortDescription", refused.Errors!.Keys);
        Assert.Equal(edited.Name, (await GetAsync(admin, h.Id)).Name);      // không lưu gì

        // Không bỏ ảnh đại diện, không xóa ảnh đại diện, không xóa nguồn cuối cùng của di sản đang xuất bản.
        await Fail(Http.PutAsync<object>($"{Api}/heritages/{h.Id}/thumbnail", new { mediaId = (long?)null }, admin),
            HttpStatusCode.UnprocessableEntity, "BLOCKED");
        await Fail(Http.DeleteAsync<object>($"{Api}/media/{h.Media.Single().Id}", admin), HttpStatusCode.UnprocessableEntity, "BLOCKED");
        var lastSource = await Fail(Http.DeleteAsync<object>($"{Api}/heritage-references/{h.References.Single().Id}", admin),
            HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.StartsWith("Không thể xóa nguồn tham chiếu.", lastSource.Message);

        // Có nguồn thứ hai thì xóa được nguồn cũ.
        await Expect(Http.PostAsync<ReferenceDto>($"{Api}/heritages/{h.Id}/references",
            new { title = "Cục Di sản văn hóa", url = "https://dsvh.gov.vn/x", sourceType = "WEBSITE" }, admin), HttpStatusCode.Created);
        await Expect(Http.DeleteAsync<object>($"{Api}/heritage-references/{h.References.Single().Id}", admin));
    }

    [Fact]
    public async Task Readiness_warns_about_the_two_source_kinds_the_team_process_asks_for()
    {
        var admin = await _api.AdminTokenAsync();
        var h = await PublishableDraftAsync(admin);
        Assert.Equal(["Chưa có nguồn là bài của Cục Di sản văn hóa hoặc UNESCO."], h.Readiness.Warnings);

        await Expect(Http.PostAsync<ReferenceDto>($"{Api}/heritages/{h.Id}/references",
            new { title = "Bài viết — Cục Di sản văn hóa, truy cập 26/09/2026", url = "https://dsvh.gov.vn/bai", sourceType = "WEBSITE" }, admin),
            HttpStatusCode.Created);
        Assert.Empty((await GetAsync(admin, h.Id)).Readiness.Warnings);
    }

    // ---- Timeline ----

    [Fact]
    public async Task Timelines_are_created_edited_ordered_and_deleted_with_their_events()
    {
        var admin = await _api.AdminTokenAsync();
        var h = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", new { name = "Timeline " + Unique() }, admin),
            HttpStatusCode.Created);

        var first = await Expect(Http.PostAsync<TimelineDto>($"{Api}/heritages/{h.Id}/timelines",
            new { name = "Thời Lý", description = "Dời đô về Thăng Long", startYear = 1010, endYear = 1225 }, admin), HttpStatusCode.Created);
        var second = await Expect(Http.PostAsync<TimelineDto>($"{Api}/heritages/{h.Id}/timelines",
            new { name = "Thời tiền sử", startYear = -3000, endYear = -700 }, admin), HttpStatusCode.Created);
        var third = await Expect(Http.PostAsync<TimelineDto>($"{Api}/heritages/{h.Id}/timelines",
            new { name = "Thời Trần" }, admin), HttpStatusCode.Created);
        Assert.Equal([1, 2, 3], new[] { first.SortOrder, second.SortOrder, third.SortOrder });
        Assert.Equal(-3000, second.StartYear);

        var edited = await Expect(Http.PutAsync<TimelineDto>($"{Api}/timelines/{first.Id}",
            new { name = "Thời Lý – Trần", description = "Đã sửa", startYear = 1010, endYear = 1400 }, admin));
        Assert.Equal(("Thời Lý – Trần", 1400, 1), (edited.Name, edited.EndYear, edited.SortOrder));   // giữ thứ tự khi không gửi sortOrder

        var ordered = await Expect(Http.PutAsync<List<TimelineDto>>($"{Api}/heritages/{h.Id}/timelines/order",
            new { ids = new[] { second.Id, third.Id, first.Id } }, admin));
        Assert.Equal([second.Id, third.Id, first.Id], ordered.Select(t => t.Id));
        Assert.Equal([1, 2, 3], ordered.Select(t => t.SortOrder));
        Assert.Equal([second.Id, third.Id, first.Id], (await GetAsync(admin, h.Id)).Timelines.Select(t => t.Id));

        foreach (var wrong in new object[]
                 {
                     new { ids = new[] { second.Id, third.Id } },                        // thiếu
                     new { ids = new[] { second.Id, second.Id, third.Id } },              // lặp
                     new { ids = new[] { second.Id, third.Id, 999_999L } },               // lạ
                     new { ids = (long[]?)null },
                 })
            await Fail(Http.PutAsync<object>($"{Api}/heritages/{h.Id}/timelines/order", wrong, admin), HttpStatusCode.BadRequest, "INVALID");

        var ev = await Expect(Http.PostAsync<EventDto>($"{Api}/timelines/{first.Id}/events",
            new { name = "Dời đô", startYear = 1010 }, admin), HttpStatusCode.Created);
        await Expect(Http.PostAsync<MediaDto>($"{Api}/events/{ev.Id}/media", ImageBody($"https://x.org/{Unique()}.jpg"), admin), HttpStatusCode.Created);

        await Expect(Http.DeleteAsync<object>($"{Api}/timelines/{first.Id}", admin));
        var counts = await factory.WithDbAsync(async db => (
            await db.Timelines.CountAsync(t => t.HeritageId == h.Id),
            await db.Events.CountAsync(e => e.Id == ev.Id),
            await db.Media.CountAsync(m => m.EventId == ev.Id)));
        Assert.Equal((2, 0, 0), counts);
        await Fail(Http.DeleteAsync<object>($"{Api}/timelines/{first.Id}", admin), HttpStatusCode.NotFound, "BLOCKED");
    }

    [Fact]
    public async Task Timeline_fields_are_validated()
    {
        var admin = await _api.AdminTokenAsync();
        var h = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", new { name = "Kiểm giai đoạn " + Unique() }, admin),
            HttpStatusCode.Created);

        var noName = await Fail(Http.PostAsync<object>($"{Api}/heritages/{h.Id}/timelines", new { name = "" }, admin),
            HttpStatusCode.BadRequest, "REQUIRED");
        Assert.Equal("Vui lòng nhập tên giai đoạn.", noName.Errors!["name"]);

        var reversed = await Fail(Http.PostAsync<object>($"{Api}/heritages/{h.Id}/timelines",
            new { name = "Ngược", startYear = 1225, endYear = 1010 }, admin), HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal("Năm kết thúc không hợp lệ: không được nhỏ hơn năm bắt đầu.", reversed.Errors!["endYear"]);

        // Năm âm: kết thúc -700 sau bắt đầu -3000 là hợp lệ, ngược lại thì không.
        await Expect(Http.PostAsync<TimelineDto>($"{Api}/heritages/{h.Id}/timelines",
            new { name = "Đồ đồng", startYear = -3000, endYear = -700 }, admin), HttpStatusCode.Created);
        await Fail(Http.PostAsync<object>($"{Api}/heritages/{h.Id}/timelines",
            new { name = "Sai", startYear = -700, endYear = -3000 }, admin), HttpStatusCode.BadRequest, "INVALID");

        await Fail(Http.PostAsync<object>($"{Api}/heritages/999999/timelines", new { name = "Không có di sản" }, admin),
            HttpStatusCode.NotFound, "BLOCKED");
    }

    [Fact]
    public async Task Events_are_validated_sorted_by_date_and_reordered_within_a_date()
    {
        var admin = await _api.AdminTokenAsync();
        var h = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", new { name = "Sự kiện " + Unique() }, admin),
            HttpStatusCode.Created);
        var timeline = await Expect(Http.PostAsync<TimelineDto>($"{Api}/heritages/{h.Id}/timelines", new { name = "Thời Lý" }, admin),
            HttpStatusCode.Created);
        string EventsUrl() => $"{Api}/timelines/{timeline.Id}/events";

        var noYear = await Fail(Http.PostAsync<object>(EventsUrl(), new { name = "Thiếu năm" }, admin), HttpStatusCode.BadRequest, "REQUIRED");
        Assert.Equal("Vui lòng nhập năm bắt đầu.", noYear.Errors!["startYear"]);
        var noName = await Fail(Http.PostAsync<object>(EventsUrl(), new { startYear = 1010 }, admin), HttpStatusCode.BadRequest, "REQUIRED");
        Assert.Equal("Vui lòng nhập tên sự kiện.", noName.Errors!["name"]);

        var badMonth = await Fail(Http.PostAsync<object>(EventsUrl(), new { name = "x", startYear = 1010, startMonth = 13 }, admin),
            HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal("Tháng bắt đầu không hợp lệ: phải từ 1 đến 12.", badMonth.Errors!["startMonth"]);
        var dayWithoutMonth = await Fail(Http.PostAsync<object>(EventsUrl(), new { name = "x", startYear = 1010, startDay = 5 }, admin),
            HttpStatusCode.BadRequest, "INVALID");
        Assert.StartsWith("Ngày bắt đầu không hợp lệ", dayWithoutMonth.Errors!["startDay"]);
        var noSuchDay = await Fail(Http.PostAsync<object>(EventsUrl(), new { name = "x", startYear = 2026, startMonth = 2, startDay = 29 }, admin),
            HttpStatusCode.BadRequest, "INVALID");
        Assert.StartsWith("Ngày bắt đầu không hợp lệ", noSuchDay.Errors!["startDay"]);
        var endMonthNoYear = await Fail(Http.PostAsync<object>(EventsUrl(), new { name = "x", startYear = 1010, endMonth = 3 }, admin),
            HttpStatusCode.BadRequest, "INVALID");
        Assert.StartsWith("Tháng kết thúc không hợp lệ", endMonthNoYear.Errors!["endMonth"]);
        var endsBefore = await Fail(Http.PostAsync<object>(EventsUrl(),
            new { name = "x", startYear = 1010, startMonth = 3, startDay = 5, endYear = 1010, endMonth = 3, endDay = 4 }, admin),
            HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal("Thời điểm kết thúc không hợp lệ: không được trước thời điểm bắt đầu.", endsBefore.Errors!["endYear"]);

        var late = await Expect(Http.PostAsync<EventDto>(EventsUrl(),
            new { name = "Cuối năm", description = "Tháng 12", startYear = 1010, startMonth = 12, startDay = 25 }, admin), HttpStatusCode.Created);
        var early = await Expect(Http.PostAsync<EventDto>(EventsUrl(),
            new { name = "Đầu năm", startYear = 1010, startMonth = 2 }, admin), HttpStatusCode.Created);
        var yearOnly = await Expect(Http.PostAsync<EventDto>(EventsUrl(), new { name = "Chỉ có năm", startYear = 1010 }, admin), HttpStatusCode.Created);
        var bc = await Expect(Http.PostAsync<EventDto>(EventsUrl(),
            new { name = "Trước Công nguyên", startYear = -179, endYear = -111 }, admin), HttpStatusCode.Created);
        Assert.Equal((null, null), (yearOnly.StartMonth, yearOnly.StartDay));

        var detail = await GetAsync(admin, h.Id);
        Assert.Equal([bc.Id, yearOnly.Id, early.Id, late.Id], detail.Timelines.Single().Events.Select(e => e.Id));

        var edited = await Expect(Http.PutAsync<EventDto>($"{Api}/events/{late.Id}",
            new { name = "Cuối năm (sửa)", startYear = 1010, startMonth = 12, startDay = 25, endYear = 1011, endMonth = 1 }, admin));
        Assert.Equal(("Cuối năm (sửa)", (short?)1, late.SortOrder), (edited.Name, edited.EndMonth, edited.SortOrder));

        // Ba sự kiện cùng năm không tháng: thứ tự cùng kỳ theo sortOrder.
        var a = await Expect(Http.PostAsync<EventDto>(EventsUrl(), new { name = "A", startYear = 1500 }, admin), HttpStatusCode.Created);
        var b = await Expect(Http.PostAsync<EventDto>(EventsUrl(), new { name = "B", startYear = 1500 }, admin), HttpStatusCode.Created);
        var reordered = await Expect(Http.PutAsync<List<EventDto>>($"{Api}/timelines/{timeline.Id}/events/order",
            new { ids = new[] { late.Id, early.Id, yearOnly.Id, bc.Id, b.Id, a.Id } }, admin));
        Assert.Equal([bc.Id, yearOnly.Id, early.Id, late.Id, b.Id, a.Id], reordered.Select(e => e.Id));

        await Expect(Http.DeleteAsync<object>($"{Api}/events/{a.Id}", admin));
        await Fail(Http.PutAsync<object>($"{Api}/events/{a.Id}", new { name = "Đã xóa", startYear = 1500 }, admin), HttpStatusCode.NotFound, "BLOCKED");
    }

    // ---- Ảnh và ghi công ----

    [Fact]
    public async Task Media_needs_a_complete_credit_with_an_open_license()
    {
        var admin = await _api.AdminTokenAsync();
        var h = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", new { name = "Ảnh " + Unique() }, admin),
            HttpStatusCode.Created);
        var url = $"{Api}/heritages/{h.Id}/media";

        var image = await Expect(Http.PostAsync<MediaDto>(url, ImageBody($"https://x.org/{Unique()}.jpg"), admin), HttpStatusCode.Created);
        Assert.Equal("Nguyễn Văn An · CC BY-SA 4.0 · Wikimedia Commons", image.Credit);
        Assert.Equal(("Nguyễn Văn An", "CC BY-SA 4.0", "Wikimedia Commons"), (image.Author, image.License, image.Source));
        Assert.Equal((h.Id, null, "IMAGE", 1), (image.HeritageId, image.EventId, image.MediaType, image.SortOrder));

        var noCredit = await Fail(Http.PostAsync<object>(url, new { mediaType = "IMAGE", url = "https://x.org/a.jpg" }, admin),
            HttpStatusCode.BadRequest, "REQUIRED");
        Assert.Equal("Vui lòng nhập tác giả.", noCredit.Errors!["author"]);
        Assert.Equal("Vui lòng nhập giấy phép.", noCredit.Errors["license"]);
        Assert.Equal("Vui lòng nhập nguồn ảnh.", noCredit.Errors["source"]);

        var restricted = await Fail(Http.PostAsync<object>(url, ImageBody("https://x.org/b.jpg", license: "CC BY-NC 4.0"), admin),
            HttpStatusCode.BadRequest, "INVALID");
        Assert.StartsWith("Giấy phép không hợp lệ: chỉ dùng CC0, CC BY, CC BY-SA", restricted.Errors!["license"]);

        var badType = await Fail(Http.PostAsync<object>(url, ImageBody("https://x.org/c.jpg", type: "PDF"), admin), HttpStatusCode.BadRequest, "INVALID");
        Assert.StartsWith("Loại media không hợp lệ", badType.Errors!["mediaType"]);
        var notUrl = await Fail(Http.PostAsync<object>(url, ImageBody("khong-phai-duong-dan"), admin), HttpStatusCode.BadRequest, "INVALID");
        Assert.StartsWith("Đường dẫn không hợp lệ", notUrl.Errors!["url"]);
        var tooLong = await Fail(Http.PostAsync<object>(url, new
        {
            mediaType = "IMAGE", url = "https://x.org/d.jpg", author = new string('a', 200), license = "CC0", source = new string('b', 100),
        }, admin), HttpStatusCode.BadRequest, "INVALID");
        Assert.StartsWith("Ghi công không hợp lệ", tooLong.Errors!["author"]);

        var duplicate = await Fail(Http.PostAsync<object>(url, ImageBody(image.Url!), admin), HttpStatusCode.Conflict, "DUPLICATE");
        Assert.Equal("Đường dẫn không hợp lệ: đường dẫn này đã có trong thư viện.", duplicate.Errors!["url"]);

        var video = await Expect(Http.PostAsync<MediaDto>(url, ImageBody("https://www.youtube.com/embed/abc", type: "VIDEO", license: "CC BY 3.0"), admin),
            HttpStatusCode.Created);
        Assert.Equal((2, "VIDEO"), (video.SortOrder, video.MediaType));
        await Fail(Http.PostAsync<object>(url, ImageBody("https://x.org/e.mp3", thumbnail: true, type: "AUDIO"), admin),
            HttpStatusCode.BadRequest, "INVALID");
    }

    [Fact]
    public async Task The_thumbnail_follows_its_image_and_only_heritage_images_can_be_the_thumbnail()
    {
        var admin = await _api.AdminTokenAsync();
        var h = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", new { name = "Ảnh đại diện " + Unique() }, admin),
            HttpStatusCode.Created);
        var mediaUrl = $"{Api}/heritages/{h.Id}/media";
        var a = await Expect(Http.PostAsync<MediaDto>(mediaUrl, ImageBody($"https://x.org/{Unique()}.jpg", thumbnail: true), admin), HttpStatusCode.Created);
        var b = await Expect(Http.PostAsync<MediaDto>(mediaUrl, ImageBody($"https://x.org/{Unique()}.jpg"), admin), HttpStatusCode.Created);
        Assert.True(a.IsThumbnail);
        Assert.Equal(a.Url, (await GetAsync(admin, h.Id)).ThumbnailUrl);

        // Đổi đường dẫn ảnh đại diện thì ảnh đại diện đổi theo.
        var moved = $"https://x.org/{Unique()}.jpg";
        var updated = await Expect(Http.PutAsync<MediaDto>($"{Api}/media/{a.Id}", ImageBody(moved), admin));
        Assert.True(updated.IsThumbnail);
        Assert.Equal(moved, (await GetAsync(admin, h.Id)).ThumbnailUrl);

        // Chọn ảnh khác qua setAsThumbnail, ảnh cũ thôi là ảnh đại diện.
        var chosen = await Expect(Http.PutAsync<MediaDto>($"{Api}/media/{b.Id}", ImageBody(b.Url!, thumbnail: true), admin));
        Assert.True(chosen.IsThumbnail);
        var detail = await GetAsync(admin, h.Id);
        Assert.Equal(b.Url, detail.ThumbnailUrl);
        Assert.Equal([false, true], detail.Media.Select(m => m.IsThumbnail));

        // Bỏ chọn, đổi thành video thì không còn là ảnh đại diện hợp lệ.
        await Expect(Http.PutAsync<MediaDto>($"{Api}/media/{b.Id}", new
        {
            mediaType = "IMAGE", url = b.Url, author = "Nguyễn Văn An", license = "CC0", source = "Tự chụp", setAsThumbnail = false,
        }, admin));
        Assert.Null((await GetAsync(admin, h.Id)).ThumbnailUrl);
        await Fail(Http.PutAsync<object>($"{Api}/media/{a.Id}", ImageBody(moved, thumbnail: true, type: "VIDEO"), admin),
            HttpStatusCode.BadRequest, "INVALID");

        // Ảnh của sự kiện không phải ảnh đại diện.
        var timeline = await Expect(Http.PostAsync<TimelineDto>($"{Api}/heritages/{h.Id}/timelines", new { name = "GĐ" }, admin), HttpStatusCode.Created);
        var ev = await Expect(Http.PostAsync<EventDto>($"{Api}/timelines/{timeline.Id}/events", new { name = "SK", startYear = 1010 }, admin),
            HttpStatusCode.Created);
        await Fail(Http.PostAsync<object>($"{Api}/events/{ev.Id}/media", ImageBody($"https://x.org/{Unique()}.jpg", thumbnail: true), admin),
            HttpStatusCode.BadRequest, "INVALID");
        var eventImage = await Expect(Http.PostAsync<MediaDto>($"{Api}/events/{ev.Id}/media", ImageBody($"https://x.org/{Unique()}.jpg"), admin),
            HttpStatusCode.Created);
        Assert.Equal((null, ev.Id, false), (eventImage.HeritageId, eventImage.EventId, eventImage.IsThumbnail));
        await Fail(Http.PutAsync<object>($"{Api}/heritages/{h.Id}/thumbnail", new { mediaId = eventImage.Id }, admin),
            HttpStatusCode.BadRequest, "INVALID");
        Assert.Contains((await GetAsync(admin, h.Id)).Timelines.Single().Events.Single().Media, m => m.Id == eventImage.Id);

        // Xóa ảnh đại diện của di sản chưa xuất bản thì bỏ luôn ảnh đại diện.
        await Expect(Http.PutAsync<HeritageDetailDto>($"{Api}/heritages/{h.Id}/thumbnail", new { mediaId = a.Id }, admin));
        await Expect(Http.DeleteAsync<object>($"{Api}/media/{a.Id}", admin));
        Assert.Null((await GetAsync(admin, h.Id)).ThumbnailUrl);
    }

    [Fact]
    public async Task Heritage_and_event_media_are_reordered_independently()
    {
        var admin = await _api.AdminTokenAsync();
        var h = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", new { name = "Sắp ảnh " + Unique() }, admin),
            HttpStatusCode.Created);
        var ids = new List<long>();
        for (var i = 0; i < 3; i++)
            ids.Add((await Expect(Http.PostAsync<MediaDto>($"{Api}/heritages/{h.Id}/media", ImageBody($"https://x.org/{Unique()}.jpg"), admin),
                HttpStatusCode.Created)).Id);

        var reordered = await Expect(Http.PutAsync<List<MediaDto>>($"{Api}/heritages/{h.Id}/media/order",
            new { ids = new[] { ids[2], ids[0], ids[1] } }, admin));
        Assert.Equal([ids[2], ids[0], ids[1]], reordered.Select(m => m.Id));
        Assert.Equal([ids[2], ids[0], ids[1]], (await GetAsync(admin, h.Id)).Media.Select(m => m.Id));
        await Fail(Http.PutAsync<object>($"{Api}/heritages/{h.Id}/media/order", new { ids = new[] { ids[0] } }, admin),
            HttpStatusCode.BadRequest, "INVALID");
    }

    // ---- Nguồn tham chiếu ----

    [Fact]
    public async Task References_have_a_type_and_websites_need_a_link()
    {
        var admin = await _api.AdminTokenAsync();
        var h = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", new { name = "Nguồn " + Unique() }, admin),
            HttpStatusCode.Created);
        var url = $"{Api}/heritages/{h.Id}/references";

        var noTitle = await Fail(Http.PostAsync<object>(url, new { sourceType = "BOOK" }, admin), HttpStatusCode.BadRequest, "REQUIRED");
        Assert.Equal("Vui lòng nhập tiêu đề nguồn.", noTitle.Errors!["title"]);
        var oldType = await Fail(Http.PostAsync<object>(url, new { title = "UNESCO", sourceType = "UNESCO" }, admin), HttpStatusCode.BadRequest, "INVALID");
        Assert.StartsWith("Loại nguồn không hợp lệ: chỉ nhận GOV_DOC", oldType.Errors!["sourceType"]);
        var noLink = await Fail(Http.PostAsync<object>(url, new { title = "Trang web", sourceType = "WEBSITE" }, admin), HttpStatusCode.BadRequest, "REQUIRED");
        Assert.Equal("Vui lòng nhập đường dẫn của nguồn website.", noLink.Errors!["url"]);
        await Fail(Http.PostAsync<object>(url, new { title = "Trang web", url = "ftp://x", sourceType = "WEBSITE" }, admin),
            HttpStatusCode.BadRequest, "INVALID");

        var book = await Expect(Http.PostAsync<ReferenceDto>(url, new { title = "Sách — Nguyễn Văn A, 2020", sourceType = "BOOK" }, admin),
            HttpStatusCode.Created);
        Assert.Equal(("BOOK", "Sách", null), (book.SourceType, book.SourceTypeLabel, book.Url));

        var edited = await Expect(Http.PutAsync<ReferenceDto>($"{Api}/heritage-references/{book.Id}",
            new { title = "Sách (bản sửa)", url = "https://sach.vn/a", sourceType = "BOOK" }, admin));
        Assert.Equal(("Sách (bản sửa)", "https://sach.vn/a"), (edited.Title, edited.Url));
        Assert.Equal(["Sách (bản sửa)"], (await GetAsync(admin, h.Id)).References.Select(r => r.Title));

        await Expect(Http.DeleteAsync<object>($"{Api}/heritage-references/{book.Id}", admin));
        await Fail(Http.DeleteAsync<object>($"{Api}/heritage-references/{book.Id}", admin), HttpStatusCode.NotFound, "BLOCKED");
    }

    // ---- Xóa mềm ----

    [Fact]
    public async Task A_draft_can_be_deleted_but_a_published_one_must_be_hidden_first()
    {
        var admin = await _api.AdminTokenAsync();
        var draft = await PublishableDraftAsync(admin);
        var published = await PublishableDraftAsync(admin);
        await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages/{published.Id}/publish", null, admin));

        await Expect(Http.DeleteAsync<object>($"{Api}/heritages/{draft.Id}", admin));
        await Fail(Http.GetAsync<object>($"{Api}/heritages/{draft.Id}", admin), HttpStatusCode.NotFound, "BLOCKED");
        await Fail(Http.PostAsync<object>($"{Api}/heritages/{draft.Id}/references", GovDocBody(), admin), HttpStatusCode.NotFound, "BLOCKED");
        var list = await Expect(Http.GetAsync<PagedResult<HeritageSummaryDto>>($"{Api}/heritages?keyword={draft.Slug}&pageSize=100", admin));
        Assert.Empty(list.Items);

        var refused = await Fail(Http.DeleteAsync<object>($"{Api}/heritages/{published.Id}", admin), HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Equal("Không thể xóa di sản. Di sản đang xuất bản. Hãy ẩn di sản trước.", refused.Message);

        await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages/{published.Id}/archive", null, admin));
        await Expect(Http.DeleteAsync<object>($"{Api}/heritages/{published.Id}", admin));
    }

    [Fact]
    public async Task A_heritage_used_by_a_lesson_or_a_question_can_only_be_hidden()
    {
        var admin = await _api.AdminTokenAsync();
        var (org, orgAdmin) = await _api.OrgWithAdminAsync();
        var (teacher, _) = await _api.TeacherAsync(orgAdmin);
        var h = await PublishableDraftAsync(admin);
        await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages/{h.Id}/publish", null, admin));
        await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages/{h.Id}/archive", null, admin));

        await factory.WithDbAsync(async db =>
        {
            var schoolClass = new SchoolClass { OrganizationId = org.Id, TeacherId = teacher.Id, Name = "8A1", Grade = 8 };
            db.Classes.Add(schoolClass);
            var lesson = new Lesson { TeacherId = teacher.Id, Class = schoolClass, Title = "Bài về di sản", Status = ContentStatus.DRAFT };
            lesson.Heritages.Add(new LessonHeritage { HeritageId = h.Id });
            db.Lessons.Add(lesson);
            return await db.SaveChangesAsync();
        });

        var refused = await Fail(Http.DeleteAsync<object>($"{Api}/heritages/{h.Id}", admin), HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Equal("Không thể xóa di sản. Di sản đã được dùng trong bài học hoặc câu hỏi, chỉ có thể ẩn.", refused.Message);
        Assert.Equal(ContentStatus.ARCHIVED, (await GetAsync(admin, h.Id)).Status);

        // Di sản chỉ gắn với một câu hỏi (chưa có bài học nào) cũng chỉ được ẩn.
        var other = await PublishableDraftAsync(admin);
        await factory.WithDbAsync(async db =>
        {
            db.Questions.Add(new Question { HeritageId = other.Id, CreatedBy = teacher.Id, Content = "Câu hỏi về di sản?" });
            return await db.SaveChangesAsync();
        });
        var refusedByQuestion = await Fail(Http.DeleteAsync<object>($"{Api}/heritages/{other.Id}", admin),
            HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("chỉ có thể ẩn", refusedByQuestion.Message);
    }

    // ---- Danh sách, tìm, lọc ----

    [Fact]
    public async Task The_list_pages_searches_without_accents_and_filters()
    {
        var admin = await _api.AdminTokenAsync();
        var north = await ProvinceIdAsync();
        var south = await ProvinceIdAsync("79", "Thành phố Hồ Chí Minh", "Nam");
        var tag = Unique();

        var hue = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", FullBody($"Đại Nội Huế {tag}", north), admin), HttpStatusCode.Created);
        var saigon = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages",
            FullBody($"Dinh Độc Lập {tag}", south, type: "MIXED"), admin), HttpStatusCode.Created);
        var song = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", new
        {
            name = $"Ca trù {tag}", heritageType = "INTANGIBLE", recognitionLevel = "UNESCO", recognizedYear = 2009,
        }, admin), HttpStatusCode.Created);

        Task<PagedResult<HeritageSummaryDto>> List(string query) =>
            Expect(Http.GetAsync<PagedResult<HeritageSummaryDto>>($"{Api}/heritages?{query}", admin));

        var all = await List($"keyword={tag}&pageSize=2");
        Assert.Equal((3, 2, 2, 1), (all.TotalItems, all.TotalPages, all.Items.Count, all.Page));
        Assert.Equal([song.Id, saigon.Id], all.Items.Select(i => i.Id));     // mới nhất trước
        Assert.Equal([hue.Id], (await List($"keyword={tag}&pageSize=2&page=2")).Items.Select(i => i.Id));

        // Không dấu: "dinh doc lap" tìm ra "Dinh Độc Lập" qua đường dẫn; có dấu tìm ra qua tên.
        Assert.Equal([saigon.Id], (await List($"keyword={Uri.EscapeDataString("dinh doc lap " + tag)}")).Items.Select(i => i.Id));
        Assert.Equal([hue.Id], (await List($"keyword={Uri.EscapeDataString("Đại Nội Huế " + tag)}")).Items.Select(i => i.Id));

        Assert.Equal([saigon.Id], (await List($"keyword={tag}&provinceId={south}")).Items.Select(i => i.Id));
        Assert.Equal([song.Id], (await List($"keyword={tag}&heritageType=INTANGIBLE")).Items.Select(i => i.Id));
        Assert.Equal([song.Id], (await List($"keyword={tag}&recognitionLevel=UNESCO")).Items.Select(i => i.Id));
        Assert.Equal(3, (await List($"keyword={tag}&status=DRAFT")).TotalItems);
        Assert.Empty((await List($"keyword={tag}&status=PUBLISHED")).Items);
        Assert.Empty((await List($"keyword={tag}&provinceId={south}&heritageType=INTANGIBLE")).Items);   // điều kiện kết hợp theo VÀ

        var row = (await List($"keyword={tag}&provinceId={south}")).Items.Single();
        Assert.Equal(("Thành phố Hồ Chí Minh", HeritageType.MIXED, ContentStatus.DRAFT, 0, 0, 0),
            (row.ProvinceName, row.HeritageType, row.Status, row.TimelineCount, row.MediaCount, row.ReferenceCount));

        await Expect(Http.PostAsync<TimelineDto>($"{Api}/heritages/{saigon.Id}/timelines", new { name = "GĐ 1" }, admin), HttpStatusCode.Created);
        await Expect(Http.PostAsync<ReferenceDto>($"{Api}/heritages/{saigon.Id}/references", GovDocBody(), admin), HttpStatusCode.Created);
        var counted = (await List($"keyword={tag}&provinceId={south}")).Items.Single();
        Assert.Equal((1, 0, 1), (counted.TimelineCount, counted.MediaCount, counted.ReferenceCount));

        await Fail(Http.GetAsync<object>($"{Api}/heritages?page=0", admin), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.GetAsync<object>($"{Api}/heritages?pageSize=101", admin), HttpStatusCode.BadRequest, "INVALID");
    }

    [Fact]
    public async Task Editing_a_child_moves_the_heritage_to_the_top_of_the_list()
    {
        var admin = await _api.AdminTokenAsync();
        var tag = Unique();
        var older = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", new { name = $"Cũ hơn {tag}" }, admin), HttpStatusCode.Created);
        await Task.Delay(30);
        await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", new { name = $"Mới hơn {tag}" }, admin), HttpStatusCode.Created);
        await Task.Delay(30);

        await Expect(Http.PostAsync<TimelineDto>($"{Api}/heritages/{older.Id}/timelines", new { name = "GĐ" }, admin), HttpStatusCode.Created);

        var list = await Expect(Http.GetAsync<PagedResult<HeritageSummaryDto>>($"{Api}/heritages?keyword={tag}", admin));
        Assert.Equal(older.Id, list.Items.First().Id);
    }
}
