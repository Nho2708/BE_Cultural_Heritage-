using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DSVHVN.Application.Auth;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Ảnh đại diện của hồ sơ cá nhân qua HTTP (quản trị hệ thống, quản trị trường, giáo viên): tải ảnh lên, kiểm bằng nội dung tệp,
/// giới hạn 5 MB, bỏ EXIF, cắt vuông, tên ngẫu nhiên, gỡ ảnh, giữ tệp cũ trong kho, chỉ đổi được ảnh của chính mình.
/// </summary>
public sealed class ProfileAvatarApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Url = "/api/v1/me/avatar";

    private readonly PlatformClient _api = new(factory);
    private HttpClient Http => _api.Http;

    private async Task<(HttpResponseMessage Response, Envelope<T> Body)> UploadAsync<T>(
        string? token, byte[]? file, string fileName = "toi.jpg", string contentType = "image/jpeg")
    {
        using var form = new MultipartFormDataContent();
        if (file is not null)
        {
            var part = new ByteArrayContent(file);
            part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(part, "file", fileName);
        }
        else
        {
            form.Add(new StringContent("khong-co-tep"), "ghiChu");     // biểu mẫu có phần khác nhưng thiếu tệp
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = form };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await Http.SendAsync(request);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(HttpTestExtensions.Json);
        return (response, envelope!);
    }

    private async Task<ProfileDto> UploadedAsync(string token, byte[] file, string fileName = "toi.jpg", string contentType = "image/jpeg")
    {
        var (res, body) = await UploadAsync<ProfileDto>(token, file, fileName, contentType);
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"Mong 200, nhận {(int)res.StatusCode} {body.ErrorCode}: {body.Message}");
        return body.Data!;
    }

    private async Task<Envelope<object>> RefusedAsync(string token, byte[]? file, string code, string fileName = "toi.jpg", string contentType = "image/jpeg")
    {
        var (res, body) = await UploadAsync<object>(token, file, fileName, contentType);
        Assert.Equal((HttpStatusCode.BadRequest, code), (res.StatusCode, body.ErrorCode));
        return body;
    }

    private string[] StoredFiles() => Directory.Exists(factory.StorageRoot)
        ? Directory.GetFiles(factory.StorageRoot, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".tmp")).ToArray()
        : [];

    private string PathOf(string publicUrl) =>
        Path.Combine(factory.StorageRoot, publicUrl[StoredFileRules.PublicPathPrefix.Length..].Replace('/', Path.DirectorySeparatorChar));

    private async Task<string?> AvatarOfAsync(string token) => (await Http.GetAsync<ProfileDto>("/api/v1/me", token)).Body.Data!.AvatarUrl;

    [Fact]
    public async Task Without_signing_in_the_avatar_cannot_be_changed_or_removed()
    {
        var before = StoredFiles().Length;

        var (upload, _) = await UploadAsync<object>(null, FileSamples.Jpeg());
        var (remove, _) = await Http.DeleteAsync<object>(Url);

        Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, remove.StatusCode);
        Assert.Equal(before, StoredFiles().Length);
    }

    [Fact]
    public async Task Every_staff_role_can_upload_an_avatar_and_it_is_served_publicly()
    {
        var admin = await _api.AdminTokenAsync();
        var (_, orgAdmin) = await _api.OrgWithAdminAsync();
        var (_, teacher) = await _api.TeacherAsync(orgAdmin);

        foreach (var token in new[] { admin, orgAdmin, teacher })
        {
            var profile = await UploadedAsync(token, FileSamples.Png(120, 80));

            Assert.Matches(@"^/files/images/[0-9a-f]{32}\.webp$", profile.AvatarUrl);
            Assert.Equal(profile.AvatarUrl, await AvatarOfAsync(token));
            using var anonymous = factory.CreateClient();
            var image = await anonymous.GetAsync(profile.AvatarUrl);
            Assert.Equal(HttpStatusCode.OK, image.StatusCode);
            Assert.Equal("image/webp", image.Content.Headers.ContentType!.MediaType);
        }
    }

    [Fact]
    public async Task The_success_message_and_the_profile_are_returned()
    {
        var (_, orgAdmin) = await _api.OrgWithAdminAsync();

        var (res, body) = await UploadAsync<ProfileDto>(orgAdmin, FileSamples.Jpeg());

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Cập nhật ảnh đại diện thành công.", body.Message);
        Assert.Equal(RoleCode.ORG_ADMIN, body.Data!.Role);
    }

    [Fact]
    public async Task The_stored_avatar_is_a_square_webp_without_exif_under_a_random_name()
    {
        var (_, orgAdmin) = await _api.OrgWithAdminAsync();
        var original = FileSamples.WithExif(FileSamples.Jpeg(1200, 800), orientation: 1);
        Assert.True(FileSamples.Contains(original, FileSamples.Marker));

        var profile = await UploadedAsync(orgAdmin, original, fileName: "IMG_20260101_nha-rieng.jpg");

        Assert.DoesNotContain("IMG_2026", profile.AvatarUrl);
        var bytes = await File.ReadAllBytesAsync(PathOf(profile.AvatarUrl!));
        Assert.False(FileSamples.Contains(bytes, FileSamples.Marker));
        Assert.False(FileSamples.Contains(bytes, "Exif"));
        using var stored = SKBitmap.Decode(bytes);
        Assert.Equal((512, 512), (stored.Width, stored.Height));
        Assert.True(bytes.Length < original.Length);
    }

    [Fact]
    public async Task The_format_is_decided_by_the_content_and_the_three_formats_are_accepted()
    {
        var (_, orgAdmin) = await _api.OrgWithAdminAsync();

        // PNG và WEBP khai tên .jpg và loại nội dung image/jpeg: vẫn được nhận.
        await UploadedAsync(orgAdmin, FileSamples.Jpeg());
        await UploadedAsync(orgAdmin, FileSamples.Png(), fileName: "anh.jpg", contentType: "image/jpeg");
        await UploadedAsync(orgAdmin, FileSamples.Webp(), fileName: "anh.jpg", contentType: "image/jpeg");
    }

    [Fact]
    public async Task A_file_that_is_not_a_jpg_png_or_webp_image_is_refused_even_with_an_image_name()
    {
        var (_, orgAdmin) = await _api.OrgWithAdminAsync();
        await UploadedAsync(orgAdmin, FileSamples.Png());
        var current = await AvatarOfAsync(orgAdmin);
        var before = StoredFiles().Length;

        var gif = await RefusedAsync(orgAdmin, FileSamples.Gif(), "INVALID", fileName: "toi.jpg");
        Assert.Equal("Tệp ảnh không hợp lệ: chỉ nhận định dạng JPG, PNG hoặc WEBP.", gif.Errors!["file"]);
        var script = await RefusedAsync(orgAdmin, "<script>alert(1)</script>"u8.ToArray(), "INVALID", fileName: "toi.png", contentType: "image/png");
        Assert.Equal("Tệp ảnh không hợp lệ: không đọc được, tệp không phải ảnh hoặc đã bị hỏng.", script.Errors!["file"]);
        await RefusedAsync(orgAdmin, "MZ\u0090\0\u0003\0\0\0"u8.ToArray(), "INVALID", fileName: "toi.jpg");
        var cut = FileSamples.Jpeg(300, 300);
        await RefusedAsync(orgAdmin, cut[..(cut.Length / 2)], "INVALID");

        Assert.Equal(current, await AvatarOfAsync(orgAdmin));
        Assert.Equal(before, StoredFiles().Length);
    }

    [Fact]
    public async Task A_file_over_5_MB_or_a_missing_file_is_refused_and_a_file_exactly_at_the_limit_is_accepted()
    {
        var (_, orgAdmin) = await _api.OrgWithAdminAsync();
        var before = StoredFiles().Length;

        var big = FileSamples.Jpeg();
        Array.Resize(ref big, StoredFileRules.MaxImageBytes + 1);
        var tooBig = await RefusedAsync(orgAdmin, big, "INVALID");
        Assert.Equal("Tệp ảnh không hợp lệ: tối đa 5 MB.", tooBig.Errors!["file"]);

        var none = await RefusedAsync(orgAdmin, null, "REQUIRED");
        Assert.Equal("Vui lòng nhập tệp ảnh.", none.Errors!["file"]);
        await RefusedAsync(orgAdmin, [], "REQUIRED");
        Assert.Equal(before, StoredFiles().Length);
        Assert.Null(await AvatarOfAsync(orgAdmin));

        // JPEG hợp lệ cộng phần đệm sau dấu kết thúc ảnh cho đủ đúng 5 MB.
        var padded = new byte[StoredFileRules.MaxImageBytes];
        FileSamples.Jpeg().CopyTo(padded, 0);
        Assert.NotNull((await UploadedAsync(orgAdmin, padded)).AvatarUrl);
    }

    [Fact]
    public async Task Replacing_the_avatar_keeps_the_old_file_and_each_user_changes_only_their_own()
    {
        var (_, orgAdmin) = await _api.OrgWithAdminAsync();
        var (_, teacher) = await _api.TeacherAsync(orgAdmin);

        var first = (await UploadedAsync(orgAdmin, FileSamples.Jpeg())).AvatarUrl!;
        var second = (await UploadedAsync(orgAdmin, FileSamples.Png())).AvatarUrl!;

        Assert.NotEqual(first, second);
        Assert.Equal(second, await AvatarOfAsync(orgAdmin));
        Assert.True(File.Exists(PathOf(first)));
        Assert.True(File.Exists(PathOf(second)));
        Assert.Null(await AvatarOfAsync(teacher));      // ảnh của người khác không bị đổi
    }

    [Fact]
    public async Task Removing_the_avatar_clears_it_keeps_the_file_and_can_be_repeated()
    {
        var (_, orgAdmin) = await _api.OrgWithAdminAsync();
        var uploaded = (await UploadedAsync(orgAdmin, FileSamples.Jpeg())).AvatarUrl!;

        var (res, body) = await Http.DeleteAsync<ProfileDto>(Url, orgAdmin);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Gỡ ảnh đại diện thành công.", body.Message);
        Assert.Null(body.Data!.AvatarUrl);
        Assert.Null(await AvatarOfAsync(orgAdmin));
        Assert.True(File.Exists(PathOf(uploaded)));

        var (again, againBody) = await Http.DeleteAsync<ProfileDto>(Url, orgAdmin);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Null(againBody.Data!.AvatarUrl);
    }

    [Fact]
    public async Task Saving_the_profile_text_does_not_touch_the_avatar_and_a_posted_address_is_ignored()
    {
        var (_, orgAdmin) = await _api.OrgWithAdminAsync();
        var avatar = (await UploadedAsync(orgAdmin, FileSamples.Jpeg())).AvatarUrl;

        var (res, body) = await Http.PutAsync<ProfileDto>("/api/v1/me",
            new { fullName = "Nguyễn Thị Ánh Tuyết", phone = "0912345678", avatarUrl = "https://anh.example.vn/khac.png" }, orgAdmin);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Nguyễn Thị Ánh Tuyết", body.Data!.FullName);
        Assert.Equal(avatar, body.Data.AvatarUrl);
        Assert.Equal(avatar, await AvatarOfAsync(orgAdmin));
    }

    [Fact]
    public async Task A_deleted_account_cannot_upload_and_leaves_no_file_behind()
    {
        var (_, orgAdmin) = await _api.OrgWithAdminAsync();
        var (teacher, token) = await _api.TeacherAsync(orgAdmin);
        await factory.WithDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == teacher.Id);
            user.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return 0;
        });
        var before = StoredFiles().Length;

        var (res, _) = await UploadAsync<object>(token, FileSamples.Jpeg());

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(before, StoredFiles().Length);
    }
}
