using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DSVHVN.Application.Files;
using DSVHVN.Application.Heritages;
using DSVHVN.Domain.Rules;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Tải ảnh và mô hình 3D lên kho tệp qua HTTP (quản trị hệ thống): quyền, kiểm bằng nội dung tệp, giới hạn dung lượng, bỏ EXIF, ảnh thu nhỏ,
/// ghi công bắt buộc, ảnh đại diện, giữ tệp khi xóa ảnh, phục vụ tệp công khai có kiểm soát.
/// </summary>
public sealed class FileUploadApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Api = "/api/v1/admin";

    private readonly PlatformClient _api = new(factory);
    private HttpClient Http => _api.Http;

    // ---- Dụng cụ ----

    private static string Unique() => PlatformClient.Unique();

    private static readonly Dictionary<string, string> Credit = new()
    {
        ["author"] = "Nguyễn Văn An",
        ["license"] = "CC BY-SA 4.0",
        ["source"] = "Nhóm tự chụp, 2026",
        ["caption"] = "Cổng chính",
    };

    private async Task<(HttpResponseMessage Response, Envelope<T> Body)> UploadAsync<T>(
        string url, string? token, byte[]? file, string fileName = "anh.jpg", string contentType = "image/jpeg",
        IDictionary<string, string>? fields = null)
    {
        using var form = new MultipartFormDataContent();
        foreach (var (key, value) in fields ?? Credit) form.Add(new StringContent(value), key);
        if (file is not null)
        {
            var part = new ByteArrayContent(file);
            part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(part, "file", fileName);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await Http.SendAsync(request);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(HttpTestExtensions.Json);
        return (response, envelope!);
    }

    private static async Task<T> Expect<T>(Task<(HttpResponseMessage Response, Envelope<T> Body)> call,
        HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (res, body) = await call;
        Assert.True(res.StatusCode == expected, $"Mong {(int)expected}, nhận {(int)res.StatusCode} {body.ErrorCode}: {body.Message}");
        return body.Data!;
    }

    private static async Task<Envelope<T>> Fail<T>(Task<(HttpResponseMessage Response, Envelope<T> Body)> call, HttpStatusCode expected, string code)
    {
        var (res, body) = await call;
        Assert.Equal((expected, code), (res.StatusCode, body.ErrorCode));
        Assert.False(body.Success);
        return body;
    }

    private async Task<HeritageDetailDto> NewHeritageAsync(string token) =>
        await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", new { name = "Di sản ảnh " + Unique() }, token), HttpStatusCode.Created);

    private async Task<long> NewEventAsync(string token, long heritageId)
    {
        var timeline = await Expect(Http.PostAsync<TimelineDto>($"{Api}/heritages/{heritageId}/timelines",
            new { name = "Thời Nguyễn", startYear = 1802 }, token), HttpStatusCode.Created);
        var item = await Expect(Http.PostAsync<EventDto>($"{Api}/timelines/{timeline.Id}/events",
            new { name = "Khởi công", startYear = 1804 }, token), HttpStatusCode.Created);
        return item.Id;
    }

    /// <summary>Tệp trong kho (không tính tệp tạm đang ghi).</summary>
    private string[] StoredFiles() => Directory.Exists(factory.StorageRoot)
        ? Directory.GetFiles(factory.StorageRoot, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".tmp")).ToArray()
        : [];

    private string PathOf(string publicUrl) =>
        Path.Combine(factory.StorageRoot, publicUrl[StoredFileRules.PublicPathPrefix.Length..].Replace('/', Path.DirectorySeparatorChar));

    // ---- Quyền ----

    [Fact]
    public async Task Only_the_system_admin_can_upload_files()
    {
        var admin = await _api.AdminTokenAsync();
        var heritage = await NewHeritageAsync(admin);
        var (_, orgAdmin) = await _api.OrgWithAdminAsync();
        var (_, teacher) = await _api.TeacherAsync(orgAdmin);
        var before = StoredFiles().Length;

        foreach (var url in new[] { $"{Api}/heritages/{heritage.Id}/media/upload", $"{Api}/events/1/media/upload", $"{Api}/files/models" })
        {
            var (anonymous, _) = await UploadAsync<object>(url, null, FileSamples.Jpeg());
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            foreach (var token in new[] { orgAdmin, teacher })
                await Fail(UploadAsync<object>(url, token, FileSamples.Jpeg()), HttpStatusCode.Forbidden, "FORBIDDEN");
        }
        Assert.Equal(before, StoredFiles().Length);
    }

    // ---- Tải ảnh ----

    [Fact]
    public async Task An_uploaded_photo_is_stored_without_exif_with_a_preview_and_served_publicly()
    {
        var admin = await _api.AdminTokenAsync();
        var heritage = await NewHeritageAsync(admin);
        var original = FileSamples.WithExif(FileSamples.Jpeg(800, 400), orientation: 1);
        Assert.True(FileSamples.Contains(original, FileSamples.Marker));

        var media = await Expect(UploadAsync<MediaDto>($"{Api}/heritages/{heritage.Id}/media/upload", admin, original,
            fileName: "IMG_20260101_vi-tri-nha-rieng.jpg"), HttpStatusCode.Created);

        Assert.Equal(heritage.Id, media.HeritageId);
        Assert.Equal("IMAGE", media.MediaType);
        Assert.Matches(@"^/files/images/[0-9a-f]{32}\.jpg$", media.Url);
        Assert.DoesNotContain("IMG_2026", media.Url);                         // tên tệp là mã ngẫu nhiên
        Assert.Matches(@"^/files/images/[0-9a-f]{32}-thumb\.webp$", media.PreviewUrl);
        Assert.Equal("Nguyễn Văn An · CC BY-SA 4.0 · Nhóm tự chụp, 2026", media.Credit);
        Assert.Equal(("Nguyễn Văn An", "CC BY-SA 4.0", "Cổng chính"), (media.Author, media.License, media.Caption));
        Assert.False(media.IsThumbnail);

        // Người xem không đăng nhập mở được ảnh; ảnh đã bỏ EXIF, ảnh thu nhỏ là WEBP.
        using var anonymous = factory.CreateClient();
        var image = await anonymous.GetAsync(media.Url);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/jpeg", image.Content.Headers.ContentType!.MediaType);
        Assert.Contains("immutable", image.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", image.Headers.GetValues("X-Content-Type-Options").Single());
        var bytes = await image.Content.ReadAsByteArrayAsync();
        Assert.False(FileSamples.Contains(bytes, FileSamples.Marker));
        Assert.False(FileSamples.Contains(bytes, "Exif"));

        var preview = await anonymous.GetAsync(media.PreviewUrl);
        Assert.Equal("image/webp", preview.Content.Headers.ContentType!.MediaType);
        Assert.True((await preview.Content.ReadAsByteArrayAsync()).Length < bytes.Length);

        // Ảnh nằm trong thư viện của di sản và chi tiết trả cả đường dẫn ảnh thu nhỏ.
        var detail = await Expect(Http.GetAsync<HeritageDetailDto>($"{Api}/heritages/{heritage.Id}", admin));
        var listed = Assert.Single(detail.Media);
        Assert.Equal((media.Url, media.PreviewUrl), (listed.Url, listed.PreviewUrl));
    }

    [Fact]
    public async Task The_format_is_decided_by_the_content_and_the_stored_name_follows_it()
    {
        var admin = await _api.AdminTokenAsync();
        var heritage = await NewHeritageAsync(admin);
        var url = $"{Api}/heritages/{heritage.Id}/media/upload";

        // PNG khai tên .jpg và loại nội dung image/jpeg: vẫn được nhận và lưu đúng là .png.
        var png = await Expect(UploadAsync<MediaDto>(url, admin, FileSamples.Png(), fileName: "anh.jpg", contentType: "image/jpeg"),
            HttpStatusCode.Created);
        Assert.EndsWith(".png", png.Url);
        var webp = await Expect(UploadAsync<MediaDto>(url, admin, FileSamples.Webp(), fileName: "anh.png", contentType: "image/png"),
            HttpStatusCode.Created);
        Assert.EndsWith(".webp", webp.Url);

        var before = StoredFiles().Length;
        // GIF đội lốt .jpg, văn bản/mã đội lốt .png, ảnh cụt: đều bị từ chối, không để lại tệp nào.
        var gif = await Fail(UploadAsync<object>(url, admin, FileSamples.Gif(), fileName: "anh.jpg"), HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal("Tệp ảnh không hợp lệ: chỉ nhận định dạng JPG, PNG hoặc WEBP.", gif.Errors!["file"]);
        var script = await Fail(UploadAsync<object>(url, admin, "<script>alert(1)</script>"u8.ToArray(), fileName: "anh.png", contentType: "image/png"),
            HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal("Tệp ảnh không hợp lệ: không đọc được, tệp không phải ảnh hoặc đã bị hỏng.", script.Errors!["file"]);
        var cut = FileSamples.Jpeg(300, 300);
        await Fail(UploadAsync<object>(url, admin, cut[..(cut.Length / 2)]), HttpStatusCode.BadRequest, "INVALID");
        await Fail(UploadAsync<object>(url, admin, "MZ\u0090\0\u0003\0\0\0"u8.ToArray(), fileName: "tro-giup.jpg"), HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal(before, StoredFiles().Length);
    }

    [Fact]
    public async Task An_image_over_5_MB_or_missing_is_refused()
    {
        var admin = await _api.AdminTokenAsync();
        var heritage = await NewHeritageAsync(admin);
        var url = $"{Api}/heritages/{heritage.Id}/media/upload";
        var before = StoredFiles().Length;

        var big = FileSamples.Jpeg();
        Array.Resize(ref big, StoredFileRules.MaxImageBytes + 1);
        var tooBig = await Fail(UploadAsync<object>(url, admin, big), HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal("Tệp ảnh không hợp lệ: tối đa 5 MB.", tooBig.Errors!["file"]);

        var none = await Fail(UploadAsync<object>(url, admin, null), HttpStatusCode.BadRequest, "REQUIRED");
        Assert.Equal("Vui lòng nhập tệp ảnh.", none.Errors!["file"]);
        await Fail(UploadAsync<object>(url, admin, []), HttpStatusCode.BadRequest, "REQUIRED");

        Assert.Equal(before, StoredFiles().Length);
    }

    [Fact]
    public async Task A_photo_exactly_at_the_limit_is_still_accepted()
    {
        var admin = await _api.AdminTokenAsync();
        var heritage = await NewHeritageAsync(admin);

        // JPEG hợp lệ cộng phần đệm sau dấu kết thúc ảnh cho đủ đúng 5 MB: dữ liệu thừa sau ảnh không làm hỏng ảnh.
        var jpeg = FileSamples.Jpeg();
        var padded = new byte[StoredFileRules.MaxImageBytes];
        jpeg.CopyTo(padded, 0);

        var media = await Expect(UploadAsync<MediaDto>($"{Api}/heritages/{heritage.Id}/media/upload", admin, padded), HttpStatusCode.Created);
        Assert.EndsWith(".jpg", media.Url);
    }

    [Fact]
    public async Task Credit_is_required_and_a_refused_credit_stores_nothing()
    {
        var admin = await _api.AdminTokenAsync();
        var heritage = await NewHeritageAsync(admin);
        var url = $"{Api}/heritages/{heritage.Id}/media/upload";
        var before = StoredFiles().Length;

        var none = await Fail(UploadAsync<object>(url, admin, FileSamples.Jpeg(), fields: new Dictionary<string, string>()),
            HttpStatusCode.BadRequest, "REQUIRED");
        Assert.Equal("Vui lòng nhập tác giả.", none.Errors!["author"]);
        Assert.Equal("Vui lòng nhập giấy phép.", none.Errors["license"]);
        Assert.Equal("Vui lòng nhập nguồn ảnh.", none.Errors["source"]);

        var nonCommercial = new Dictionary<string, string>(Credit) { ["license"] = "CC BY-NC 4.0" };
        var license = await Fail(UploadAsync<object>(url, admin, FileSamples.Jpeg(), fields: nonCommercial), HttpStatusCode.BadRequest, "INVALID");
        Assert.StartsWith("Giấy phép không hợp lệ", license.Errors!["license"]);

        Assert.Equal(before, StoredFiles().Length);
    }

    [Fact]
    public async Task An_uploaded_photo_can_become_the_thumbnail_of_its_heritage()
    {
        var admin = await _api.AdminTokenAsync();
        var heritage = await NewHeritageAsync(admin);
        var fields = new Dictionary<string, string>(Credit) { ["setAsThumbnail"] = "true", ["sortOrder"] = "5" };

        var media = await Expect(UploadAsync<MediaDto>($"{Api}/heritages/{heritage.Id}/media/upload", admin, FileSamples.Png(), fields: fields),
            HttpStatusCode.Created);

        Assert.True(media.IsThumbnail);
        Assert.Equal(5, media.SortOrder);
        var detail = await Expect(Http.GetAsync<HeritageDetailDto>($"{Api}/heritages/{heritage.Id}", admin));
        Assert.Equal(media.Url, detail.ThumbnailUrl);
    }

    [Fact]
    public async Task An_event_illustration_can_be_uploaded_but_is_never_the_thumbnail()
    {
        var admin = await _api.AdminTokenAsync();
        var heritage = await NewHeritageAsync(admin);
        var eventId = await NewEventAsync(admin, heritage.Id);

        var media = await Expect(UploadAsync<MediaDto>($"{Api}/events/{eventId}/media/upload", admin, FileSamples.Webp()), HttpStatusCode.Created);
        Assert.Equal((eventId, (long?)null), (media.EventId, media.HeritageId));
        Assert.NotNull(media.PreviewUrl);

        var before = StoredFiles().Length;
        var fields = new Dictionary<string, string>(Credit) { ["setAsThumbnail"] = "true" };
        var thumb = await Fail(UploadAsync<object>($"{Api}/events/{eventId}/media/upload", admin, FileSamples.Webp(), fields: fields),
            HttpStatusCode.BadRequest, "INVALID");
        Assert.Contains("setAsThumbnail", thumb.Errors!.Keys);
        Assert.Equal(before, StoredFiles().Length);
    }

    [Fact]
    public async Task Uploading_to_a_missing_heritage_or_event_is_not_found_and_stores_nothing()
    {
        var admin = await _api.AdminTokenAsync();
        var before = StoredFiles().Length;

        await Fail(UploadAsync<object>($"{Api}/heritages/999999/media/upload", admin, FileSamples.Jpeg()), HttpStatusCode.NotFound, "BLOCKED");
        await Fail(UploadAsync<object>($"{Api}/events/999999/media/upload", admin, FileSamples.Jpeg()), HttpStatusCode.NotFound, "BLOCKED");

        // Di sản đã xóa mềm cũng không nhận ảnh.
        var gone = await NewHeritageAsync(admin);
        await Expect(Http.DeleteAsync<object>($"{Api}/heritages/{gone.Id}", admin));
        await Fail(UploadAsync<object>($"{Api}/heritages/{gone.Id}/media/upload", admin, FileSamples.Jpeg()), HttpStatusCode.NotFound, "BLOCKED");
        Assert.Equal(before, StoredFiles().Length);
    }

    [Fact]
    public async Task Removing_a_photo_from_a_heritage_keeps_the_file_in_storage()
    {
        var admin = await _api.AdminTokenAsync();
        var heritage = await NewHeritageAsync(admin);
        var media = await Expect(UploadAsync<MediaDto>($"{Api}/heritages/{heritage.Id}/media/upload", admin, FileSamples.Jpeg()),
            HttpStatusCode.Created);

        await Expect(Http.DeleteAsync<object>($"{Api}/media/{media.Id}", admin));

        Assert.False(await factory.WithDbAsync(db => db.Media.AnyAsync(m => m.Id == media.Id)));
        Assert.True(File.Exists(PathOf(media.Url!)));
        Assert.True(File.Exists(PathOf(media.PreviewUrl!)));
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(media.Url)).StatusCode);
    }

    [Fact]
    public async Task A_path_to_a_stored_file_is_accepted_when_adding_a_photo_by_address_and_other_local_paths_are_not()
    {
        var admin = await _api.AdminTokenAsync();
        var heritage = await NewHeritageAsync(admin);
        var stored = $"/files/images/{Guid.NewGuid():N}.jpg";
        object Body(string url) => new
        {
            mediaType = "IMAGE", url, author = "Nhóm", license = "Ảnh nhóm tự chụp", source = "Nhóm tự chụp",
        };

        var media = await Expect(Http.PostAsync<MediaDto>($"{Api}/heritages/{heritage.Id}/media", Body(stored), admin), HttpStatusCode.Created);
        Assert.Equal(stored, media.Url);
        Assert.StartsWith("Đường dẫn không hợp lệ", (await Fail(
            Http.PostAsync<object>($"{Api}/heritages/{heritage.Id}/media", Body("/files/../appsettings.json"), admin),
            HttpStatusCode.BadRequest, "INVALID")).Errors!["url"]);
        await Fail(Http.PostAsync<object>($"{Api}/heritages/{heritage.Id}/media", Body("/etc/passwd"), admin), HttpStatusCode.BadRequest, "INVALID");
    }

    // ---- Mô hình 3D ----

    [Fact]
    public async Task A_valid_glb_is_stored_and_served_with_its_own_type()
    {
        var admin = await _api.AdminTokenAsync();
        var glb = FileSamples.Glb();

        var stored = await Expect(UploadAsync<StoredFileDto>($"{Api}/files/models", admin, glb, fileName: "mo-hinh.glb",
            contentType: "model/gltf-binary", fields: new Dictionary<string, string>()), HttpStatusCode.Created);

        Assert.Matches(@"^/files/models/[0-9a-f]{32}\.glb$", stored.Url);
        Assert.Equal((glb.Length, "model/gltf-binary", (string?)null), (stored.Size, stored.ContentType, stored.PreviewUrl));
        using var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync(stored.Url);
        Assert.Equal("model/gltf-binary", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(glb, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_model_that_is_not_a_packed_gltf_2_with_everything_inside_is_refused()
    {
        var admin = await _api.AdminTokenAsync();
        var url = $"{Api}/files/models";
        var none = new Dictionary<string, string>();
        var before = StoredFiles().Length;

        var notGlb = await Fail(UploadAsync<object>(url, admin, FileSamples.Png(), fileName: "mo-hinh.glb", fields: none), HttpStatusCode.BadRequest, "INVALID");
        Assert.StartsWith("Tệp mô hình 3D không hợp lệ: chỉ nhận tệp .glb", notGlb.Errors!["file"]);
        await Fail(UploadAsync<object>(url, admin, "{\"asset\":{\"version\":\"2.0\"}}"u8.ToArray(), fileName: "mo-hinh.gltf", fields: none),
            HttpStatusCode.BadRequest, "INVALID");

        var old = await Fail(UploadAsync<object>(url, admin, FileSamples.Glb(version: "1.0"), fields: none), HttpStatusCode.BadRequest, "INVALID");
        Assert.Contains("glTF 2.0", old.Errors!["file"]);

        var external = FileSamples.Glb(new Dictionary<string, object?>
        {
            ["images"] = new[] { new Dictionary<string, object?> { ["uri"] = "https://example.com/texture.png" } },
        });
        var outside = await Fail(UploadAsync<object>(url, admin, external, fields: none), HttpStatusCode.BadRequest, "INVALID");
        Assert.Contains("không được tham chiếu tệp ngoài", outside.Errors!["file"]);

        await Fail(UploadAsync<object>(url, admin, null, fields: new Dictionary<string, string> { ["ghiChu"] = "không có tệp" }),
            HttpStatusCode.BadRequest, "REQUIRED");
        Assert.Equal(before, StoredFiles().Length);
    }

    [Fact]
    public async Task A_model_over_15_MB_is_refused()
    {
        var admin = await _api.AdminTokenAsync();
        var big = new byte[StoredFileRules.MaxModelBytes + 1];
        FileSamples.Glb().CopyTo(big, 0);
        var before = StoredFiles().Length;

        var response = await Fail(UploadAsync<object>($"{Api}/files/models", admin, big, fields: new Dictionary<string, string>()),
            HttpStatusCode.BadRequest, "INVALID");

        Assert.Equal("Tệp mô hình 3D không hợp lệ: tối đa 15 MB.", response.Errors!["file"]);
        Assert.Equal(before, StoredFiles().Length);
    }

    [Fact]
    public async Task An_image_far_over_the_request_limit_gets_the_same_clear_5_MB_message_with_413()
    {
        var admin = await _api.AdminTokenAsync();
        var heritage = await NewHeritageAsync(admin);
        var before = StoredFiles().Length;

        // Vượt cả phần chừa 512 KB của thân yêu cầu: trước đây máy chủ ngắt lúc đọc biểu mẫu và chỉ báo "Dữ liệu gửi lên không hợp lệ: sai định dạng".
        var huge = new byte[6 * 1024 * 1024];
        FileSamples.Jpeg().CopyTo(huge, 0);
        var tooBig = await Fail(UploadAsync<object>($"{Api}/heritages/{heritage.Id}/media/upload", admin, huge), HttpStatusCode.RequestEntityTooLarge, "INVALID");

        Assert.Equal("Tệp ảnh không hợp lệ: tối đa 5 MB.", tooBig.Errors!["file"]);
        Assert.Equal(before, StoredFiles().Length);
    }

    [Fact]
    public async Task A_model_far_over_the_request_limit_gets_the_same_clear_15_MB_message_with_413()
    {
        var admin = await _api.AdminTokenAsync();
        var huge = new byte[16 * 1024 * 1024];
        FileSamples.Glb().CopyTo(huge, 0);
        var before = StoredFiles().Length;

        var response = await Fail(UploadAsync<object>($"{Api}/files/models", admin, huge, fields: new Dictionary<string, string>()),
            HttpStatusCode.RequestEntityTooLarge, "INVALID");

        Assert.Equal("Tệp mô hình 3D không hợp lệ: tối đa 15 MB.", response.Errors!["file"]);
        Assert.Equal(before, StoredFiles().Length);
    }

    // ---- Phục vụ tệp ----

    [Fact]
    public async Task Only_files_of_the_allowed_kinds_inside_the_storage_folder_are_served()
    {
        Directory.CreateDirectory(factory.StorageRoot);
        await File.WriteAllTextAsync(Path.Combine(factory.StorageRoot, "ghi-chu.txt"), "bí mật");
        await File.WriteAllTextAsync(Path.Combine(factory.StorageRoot, "tro-giup.html"), "<script>1</script>");
        using var anonymous = factory.CreateClient();

        foreach (var path in new[]
                 {
                     "/files/ghi-chu.txt", "/files/tro-giup.html", "/files/", "/files/images/", "/files/images",
                     "/files/images/khong-co.jpg", "/files/%2e%2e/appsettings.json", "/files/..%2fappsettings.json",
                     "/files/images/%2e%2e/%2e%2e/appsettings.json",
                 })
        {
            var response = await anonymous.GetAsync(path);
            // Không được phục vụ: tệp lạ hay đường dẫn thoát thư mục không bao giờ ra 200 (rơi xuống phần còn lại của Api, chưa đăng nhập nên 401 hoặc không có nên 404).
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized, $"{path} trả {(int)response.StatusCode}");
            Assert.DoesNotContain("bí mật", await response.Content.ReadAsStringAsync());
        }
    }
}
