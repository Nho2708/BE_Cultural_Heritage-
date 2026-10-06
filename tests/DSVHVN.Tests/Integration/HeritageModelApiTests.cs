using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.Discovery;
using DSVHVN.Application.Files;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Models;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static DSVHVN.Tests.Integration.DiscoveryTestKit;
using static DSVHVN.Tests.Integration.HeritageModelTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Mô hình 3D di sản qua HTTP: quyền, tải tệp và khai báo, ghi công và giấy phép mở bắt buộc, đường dẫn chỉ trong kho tệp, một mô hình hiển thị mỗi di sản,
/// hiện, ẩn, thay, gỡ, đọc công khai miễn phí, xem trước bản nháp và nhật ký thao tác.
/// </summary>
public sealed class HeritageModelApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HeritageModelTestKit _k = new(factory);
    private HttpClient Http => _k.Http;

    private static string ModelsOf(long heritageId) => $"{AdminRoot}/heritages/{heritageId}/models";

    private static object Declaration(string fileUrl, bool? show = null, string license = "CC BY 4.0", string? author = "Nguyễn Văn An",
        string? sourceUrl = "https://sketchfab.com/models/khue-van-cac") =>
        new { title = "Khuê Văn Các", fileUrl, author, license, licenseUrl = "https://creativecommons.org/licenses/by/4.0/", sourceUrl, show };

    private int StoredModelFiles() => Directory.Exists(Path.Combine(factory.StorageRoot, "models"))
        ? Directory.GetFiles(Path.Combine(factory.StorageRoot, "models")).Length
        : 0;

    // ---- Quyền ----

    [Fact]
    public async Task Only_the_system_admin_can_declare_or_change_models_but_everyone_can_read_the_public_model()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.PublishedHeritageAsync(admin);
        var model = await _k.UploadModelAsync(admin, heritage.Id, show: true);
        var (_, orgAdmin) = await _k.Discovery.Api.OrgWithAdminAsync();
        var (_, teacher) = await _k.Discovery.Api.TeacherAsync(orgAdmin);
        var before = StoredModelFiles();

        foreach (var token in new string?[] { null, orgAdmin, teacher })
        {
            var expected = token is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            var calls = new (HttpMethod Method, string Url, object? Body)[]
            {
                (HttpMethod.Get, ModelsOf(heritage.Id), null),
                (HttpMethod.Post, ModelsOf(heritage.Id), Declaration("/files/models/" + new string('a', 32) + ".glb")),
                (HttpMethod.Put, $"{AdminRoot}/models/{model.Id}", Declaration("/files/models/" + new string('a', 32) + ".glb")),
                (HttpMethod.Post, $"{AdminRoot}/models/{model.Id}/hide", null),
                (HttpMethod.Post, $"{AdminRoot}/models/{model.Id}/show", null),
                (HttpMethod.Delete, $"{AdminRoot}/models/{model.Id}", null),
            };
            foreach (var (method, url, body) in calls)
            {
                var (response, envelope) = await Http.SendJsonAsync<object>(method, url, body, token);
                Assert.Equal(expected, response.StatusCode);
                if (token is not null) Assert.Equal("FORBIDDEN", envelope.ErrorCode);
            }
            foreach (var url in new[] { $"{ModelsOf(heritage.Id)}/upload", $"{AdminRoot}/models/{model.Id}/file" })
            {
                var (response, _) = await _k.UploadAsync<object>(url.EndsWith("/file") ? HttpMethod.Put : HttpMethod.Post, url, token, FileSamples.Glb(), Credit());
                Assert.Equal(expected, response.StatusCode);
            }
        }
        Assert.Equal(before, StoredModelFiles());
        Assert.Single(await _k.ListAsync(admin, heritage.Id));

        // Không cần đăng nhập để xem.
        var visible = await _k.VisibleModelAsync(heritage.Slug!);
        Assert.Equal(model.Id, visible.Id);
    }

    // ---- Tải lên và khai báo ----

    [Fact]
    public async Task Uploading_a_glb_declares_a_draft_with_credit_and_measured_size_and_the_file_is_served()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.PublishedHeritageAsync(admin);
        var glb = FileSamples.Glb();

        var model = await _k.UploadModelAsync(admin, heritage.Id, glb: glb);

        Assert.Equal((heritage.Id, "Khuê Văn Các", ModelFormat.GLB, (long)glb.Length), (model.HeritageId, model.Title, model.FileFormat, model.FileSizeBytes));
        Assert.Matches(@"^/files/models/[0-9a-f]{32}\.glb$", model.FileUrl);
        Assert.Equal(("Nguyễn Văn An", "CC BY 4.0", "https://sketchfab.com/models/khue-van-cac"), (model.Author, model.License, model.SourceUrl));
        Assert.Equal("Nguyễn Văn An · CC BY 4.0 · https://sketchfab.com/models/khue-van-cac", model.Credit);
        Assert.Equal((ContentStatus.DRAFT, "Nháp", false), (model.Status, model.StatusLabel, model.IsVisibleToPublic));

        using var anonymous = factory.CreateClient();
        var file = await anonymous.GetAsync(model.FileUrl);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("model/gltf-binary", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal(glb, await file.Content.ReadAsByteArrayAsync());

        // Chưa hiện thì khách chưa thấy nút "Xem 3D" và không đọc được mô hình.
        Assert.False(await _k.HasModelAsync(heritage.Slug!));
        var (response, body) = await _k.PublicModelAsync(heritage.Slug!);
        Assert.Equal((HttpStatusCode.NotFound, MessageCodes.ModelUnavailable), (response.StatusCode, body.ErrorCode));
        Assert.Equal(ModelMessages.ViewerFallback, body.Message);
    }

    [Fact]
    public async Task A_file_that_is_not_a_single_gltf_2_glb_is_refused_before_anything_is_stored_or_recorded()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.PublishedHeritageAsync(admin);
        var before = StoredModelFiles();
        var url = $"{ModelsOf(heritage.Id)}/upload";

        async Task Refuse(byte[]? file, string expectedPart, string code = "INVALID")
        {
            var (response, body) = await _k.UploadAsync<object>(HttpMethod.Post, url, admin, file, Credit());
            Assert.Equal((HttpStatusCode.BadRequest, code), (response.StatusCode, body.ErrorCode));
            Assert.Contains(expectedPart, body.Errors!["file"]);
        }
        await Refuse(null, "Vui lòng nhập tệp mô hình 3D", "REQUIRED");
        await Refuse(FileSamples.Png(), "chỉ nhận tệp .glb");
        await Refuse(FileSamples.Glb(version: "1.0"), "chỉ nhận glTF 2.0");
        await Refuse(FileSamples.Glb(new Dictionary<string, object?> { ["images"] = new[] { new { uri = "https://evil.example/t.png" } } }), "không được tham chiếu tệp ngoài");
        var big = new byte[StoredFileRules.MaxModelBytes + 1];
        FileSamples.Glb().CopyTo(big, 0);
        await Refuse(big, "tối đa 15 MB");

        Assert.Equal(before, StoredModelFiles());
        Assert.Empty(await _k.ListAsync(admin, heritage.Id));
    }

    [Fact]
    public async Task Credit_is_mandatory_and_only_open_licenses_are_accepted_and_nothing_is_stored_when_it_is_wrong()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.PublishedHeritageAsync(admin);
        var before = StoredModelFiles();
        var url = $"{ModelsOf(heritage.Id)}/upload";

        async Task Refuse(Dictionary<string, string> fields, string field, string expectedPart)
        {
            var (response, body) = await _k.UploadAsync<object>(HttpMethod.Post, url, admin, FileSamples.Glb(), fields);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(expectedPart, body.Errors![field]);
        }
        var noAuthor = Credit(); noAuthor.Remove("author");
        await Refuse(noAuthor, "author", "Vui lòng nhập tác giả");
        var noLicense = Credit(); noLicense.Remove("license");
        await Refuse(noLicense, "license", "Vui lòng nhập giấy phép");
        await Refuse(Credit(license: "CC BY-NC 4.0"), "license", "chỉ dùng CC0, CC BY, CC BY-SA");
        await Refuse(Credit(license: "CC BY-ND"), "license", "chỉ dùng CC0, CC BY, CC BY-SA");
        await Refuse(Credit(license: "All rights reserved"), "license", "chỉ dùng CC0, CC BY, CC BY-SA");
        await Refuse(Credit(sourceUrl: string.Empty), "sourceUrl", "Vui lòng nhập đường dẫn nguồn");
        await Refuse(Credit(sourceUrl: "sketchfab.com/khue-van-cac"), "sourceUrl", "phải là đường dẫn http hoặc https");
        await Refuse(Credit(author: "An · Bình"), "author", "không chứa dấu");
        await Refuse(Credit(author: new string('a', 256)), "author", "tối đa 255 ký tự");

        Assert.Equal(before, StoredModelFiles());
        Assert.Empty(await _k.ListAsync(admin, heritage.Id));
    }

    [Theory]
    [InlineData("CC0")]
    [InlineData("CC BY 4.0")]
    [InlineData("CC BY-SA 3.0")]
    [InlineData("Public domain")]
    [InlineData("Phạm vi công cộng")]
    public async Task Each_open_license_is_accepted(string license)
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.DraftHeritageAsync(admin);

        var model = await _k.UploadModelAsync(admin, heritage.Id, fields: Credit(license: license));

        Assert.Equal(license, model.License);
        Assert.Contains($" · {license} · ", model.Credit);
    }

    [Fact]
    public async Task A_team_built_model_may_leave_the_source_empty_and_the_credit_names_the_team()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.DraftHeritageAsync(admin);

        var model = await _k.UploadModelAsync(admin, heritage.Id, fields: Credit(author: "Nhóm thực hiện", license: "Mô hình nhóm tự dựng", sourceUrl: string.Empty));

        Assert.Equal("Nhóm thực hiện · Mô hình nhóm tự dựng · Nhóm thực hiện", model.Credit);
        Assert.Null(model.SourceUrl);
    }

    // ---- Khai báo bằng đường dẫn đã lưu ----

    [Fact]
    public async Task A_model_can_be_declared_with_a_stored_path_and_the_size_is_measured_by_the_server()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.DraftHeritageAsync(admin);
        var glb = FileSamples.Glb(new Dictionary<string, object?> { ["extras"] = new { note = new string('x', 500) } });
        var stored = await _k.StoreAsync(admin, glb);

        var (response, body) = await Http.PostAsync<HeritageModelDto>(ModelsOf(heritage.Id), new
        {
            title = "Khuê Văn Các", fileUrl = stored.Url, fileSizeBytes = 1, author = "Nguyễn Văn An", license = "CC0",
            sourceUrl = "https://sketchfab.com/models/khue-van-cac",
        }, admin);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal((stored.Url, (long)glb.Length), (body.Data!.FileUrl, body.Data.FileSizeBytes));   // số khách gửi không được tin
        Assert.Equal(ContentStatus.DRAFT, body.Data.Status);
    }

    [Fact]
    public async Task A_path_outside_the_file_store_is_refused()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.DraftHeritageAsync(admin);
        var stored = await _k.StoreAsync(admin);
        var key = KeyOf(stored.Url);
        var unknownKey = "models/" + new string('b', 32) + ".glb";

        async Task Refuse(string fileUrl, string expectedPart, string code = "INVALID")
        {
            var (response, body) = await Http.PostAsync<object>(ModelsOf(heritage.Id), Declaration(fileUrl), admin);
            Assert.Equal((HttpStatusCode.BadRequest, code), (response.StatusCode, body.ErrorCode));
            Assert.Contains(expectedPart, body.Errors!["fileUrl"]);
        }
        await Refuse("https://evil.example/models/khue-van-cac.glb", "không dùng đường dẫn tới trang ngoài");
        await Refuse("https://evil.example/files/" + key, "không thuộc kho tệp");     // đúng dạng nhưng của máy chủ khác
        await Refuse("/files/images/" + new string('c', 32) + ".jpg", "không dùng đường dẫn tới trang ngoài");
        await Refuse("/files/" + key + "?x=1", "không dùng đường dẫn tới trang ngoài");
        await Refuse("/files/../" + key, "không dùng đường dẫn tới trang ngoài");
        await Refuse("/files/" + unknownKey, "không có tệp này trong kho");
        await Refuse(string.Empty, "Vui lòng nhập đường dẫn tệp mô hình", "REQUIRED");

        Assert.Empty(await _k.ListAsync(admin, heritage.Id));
    }

    // ---- Một mô hình hiển thị mỗi di sản ----

    [Fact]
    public async Task A_heritage_has_at_most_one_visible_model_and_hiding_or_removing_frees_the_place()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.PublishedHeritageAsync(admin);
        var first = await _k.UploadModelAsync(admin, heritage.Id, show: true);
        var second = await _k.UploadModelAsync(admin, heritage.Id);
        Assert.Equal(ContentStatus.PUBLISHED, first.Status);
        Assert.True(first.IsVisibleToPublic);

        // Hiện mô hình thứ hai khi mô hình thứ nhất còn hiển thị: bị chặn; khai báo mới kèm show cũng vậy.
        var (response, body) = await Http.PostAsync<object>($"{AdminRoot}/models/{second.Id}/show", null, admin);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, MessageCodes.Blocked), (response.StatusCode, body.ErrorCode));
        Assert.Equal(ModelMessages.AnotherVisible.Text, body.Message);
        var before = StoredModelFiles();
        var (upload, uploadBody) = await _k.UploadAsync<object>(HttpMethod.Post, $"{ModelsOf(heritage.Id)}/upload", admin, FileSamples.Glb(),
            new Dictionary<string, string>(Credit()) { ["show"] = "true" });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, MessageCodes.Blocked), (upload.StatusCode, uploadBody.ErrorCode));
        Assert.Equal(before, StoredModelFiles());   // báo lỗi trước khi nhận tệp, không có tệp thừa

        // Ẩn mô hình thứ nhất thì hiện được mô hình thứ hai; mô hình đã ẩn hiện lại được khi chỗ trống.
        var hidden = await _k.HideAsync(admin, first.Id);
        Assert.Equal((ContentStatus.ARCHIVED, "Đã ẩn", false), (hidden.Status, hidden.StatusLabel, hidden.IsVisibleToPublic));
        Assert.False(await _k.HasModelAsync(heritage.Slug!));
        await _k.ShowAsync(admin, second.Id);
        Assert.Equal(second.Id, (await _k.VisibleModelAsync(heritage.Slug!)).Id);
        await Fail(Http.PostAsync<object>($"{AdminRoot}/models/{first.Id}/show", null, admin), HttpStatusCode.UnprocessableEntity, MessageCodes.Blocked);

        // Gỡ mô hình đang hiển thị cũng trả chỗ.
        Assert.Equal(HttpStatusCode.OK, (await Http.DeleteAsync<object>($"{AdminRoot}/models/{second.Id}", admin)).Response.StatusCode);
        await _k.ShowAsync(admin, first.Id);
        Assert.Equal(first.Id, (await _k.VisibleModelAsync(heritage.Slug!)).Id);
        Assert.Equal([first.Id], (await _k.ListAsync(admin, heritage.Id)).Select(m => m.Id));
    }

    [Fact]
    public async Task Showing_or_hiding_twice_is_refused_with_a_clear_reason()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.PublishedHeritageAsync(admin);
        var model = await _k.UploadModelAsync(admin, heritage.Id, show: true);

        var shown = await Fail(Http.PostAsync<object>($"{AdminRoot}/models/{model.Id}/show", null, admin), HttpStatusCode.UnprocessableEntity, MessageCodes.Blocked);
        Assert.Equal("Không thể hiện mô hình 3D. Mô hình đang được hiển thị.", shown.Message);
        await _k.HideAsync(admin, model.Id);
        var hidden = await Fail(Http.PostAsync<object>($"{AdminRoot}/models/{model.Id}/hide", null, admin), HttpStatusCode.UnprocessableEntity, MessageCodes.Blocked);
        Assert.Equal("Không thể ẩn mô hình 3D. Mô hình không đang hiển thị.", hidden.Message);
    }

    // ---- Hiển thị theo di sản ----

    [Fact]
    public async Task A_visible_model_reaches_visitors_only_while_the_heritage_is_published()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.PublishedHeritageAsync(admin);
        var model = await _k.UploadModelAsync(admin, heritage.Id, show: true);

        Assert.True(await _k.HasModelAsync(heritage.Slug!));
        var visible = await _k.VisibleModelAsync(heritage.Slug!);
        Assert.Equal((model.Id, model.FileUrl, "model/gltf-binary", model.FileSizeBytes), (visible.Id, visible.FileUrl, visible.ContentType, visible.FileSizeBytes));
        Assert.Equal(("Nguyễn Văn An", "CC BY 4.0", model.Credit), (visible.Author, visible.License, visible.Credit));
        Assert.Equal("https://creativecommons.org/licenses/by/4.0/", visible.LicenseUrl);
        Assert.Equal((model.Id, true), ((await _k.ListAsync(admin, heritage.Id)).Single().Id, (await _k.ListAsync(admin, heritage.Id)).Single().IsVisibleToPublic));

        // Di sản bị ẩn thì mô hình ẩn theo mà trạng thái của mô hình không đổi.
        await Expect(Http.PostAsync<HeritageDetailDto>($"{AdminRoot}/heritages/{heritage.Id}/archive", null, admin));
        var (response, body) = await _k.PublicModelAsync(heritage.Slug!);
        Assert.Equal((HttpStatusCode.NotFound, "BLOCKED"), (response.StatusCode, body.ErrorCode));
        var archived = (await _k.ListAsync(admin, heritage.Id)).Single();
        Assert.Equal((ContentStatus.PUBLISHED, false), (archived.Status, archived.IsVisibleToPublic));

        // Hiện lại di sản thì mô hình hiện lại.
        await Expect(Http.PostAsync<HeritageDetailDto>($"{AdminRoot}/heritages/{heritage.Id}/restore", null, admin));
        Assert.Equal(model.Id, (await _k.VisibleModelAsync(heritage.Slug!)).Id);
    }

    [Fact]
    public async Task A_model_waiting_on_a_draft_heritage_appears_when_the_heritage_is_published()
    {
        var admin = await _k.AdminAsync();
        var draft = await _k.DraftHeritageAsync(admin);
        var model = await _k.UploadModelAsync(admin, draft.Id, show: true);
        Assert.False(model.IsVisibleToPublic);

        Assert.Equal(HttpStatusCode.NotFound, (await _k.PublicModelAsync(draft.Slug!)).Response.StatusCode);
        await Expect(Http.PostAsync<HeritageDetailDto>($"{AdminRoot}/heritages/{draft.Id}/publish", null, admin));

        Assert.True(await _k.HasModelAsync(draft.Slug!));
        Assert.Equal(model.Id, (await _k.VisibleModelAsync(draft.Slug!)).Id);
        Assert.True((await _k.ListAsync(admin, draft.Id)).Single().IsVisibleToPublic);
    }

    [Fact]
    public async Task The_public_model_endpoint_does_not_reveal_unknown_or_malformed_heritages()
    {
        foreach (var slug in new[] { "khong-co-di-san-nay", "DI SAN LA", "a..b" })
        {
            var (response, body) = await _k.PublicModelAsync(slug);
            Assert.Equal((HttpStatusCode.NotFound, "BLOCKED"), (response.StatusCode, body.ErrorCode));
        }
        var admin = await _k.AdminAsync();
        await Fail(Http.GetAsync<object>(ModelsOf(long.MaxValue), admin), HttpStatusCode.NotFound, "BLOCKED");
    }

    // ---- Sửa, thay tệp, gỡ ----

    [Fact]
    public async Task Editing_the_credit_keeps_the_file_and_recomposes_the_credit_line()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.PublishedHeritageAsync(admin);
        var model = await _k.UploadModelAsync(admin, heritage.Id, show: true);

        var updated = await Expect(Http.PutAsync<HeritageModelDto>($"{AdminRoot}/models/{model.Id}", new
        {
            title = "Khuê Văn Các (bản 2)", author = "Trần Thị Bình", license = "CC BY-SA 4.0", sourceUrl = "https://example.org/mo-hinh", sortOrder = 3,
            posterUrl = "https://example.org/poster.webp",
        }, admin));

        Assert.Equal((model.FileUrl, model.FileSizeBytes, ContentStatus.PUBLISHED), (updated.FileUrl, updated.FileSizeBytes, updated.Status));
        Assert.Equal("Trần Thị Bình · CC BY-SA 4.0 · https://example.org/mo-hinh", updated.Credit);
        Assert.Equal(("Khuê Văn Các (bản 2)", 3, "https://example.org/poster.webp"), (updated.Title, updated.SortOrder, updated.PosterUrl));
        var visible = await _k.VisibleModelAsync(heritage.Slug!);
        Assert.Equal((updated.Credit, "CC BY-SA 4.0", "https://example.org/poster.webp"), (visible.Credit, visible.License, visible.PosterUrl));

        await Fail(Http.PutAsync<object>($"{AdminRoot}/models/{model.Id}", new { author = "An", license = "CC BY-NC", sourceUrl = "https://example.org" }, admin),
            HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal(updated.Credit, (await _k.ListAsync(admin, heritage.Id)).Single().Credit);
    }

    [Fact]
    public async Task Replacing_the_file_by_upload_or_by_stored_path_keeps_the_old_file_in_the_store()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.PublishedHeritageAsync(admin);
        var model = await _k.UploadModelAsync(admin, heritage.Id, show: true);
        using var anonymous = factory.CreateClient();

        // Thay bằng tệp tải lên.
        var bigger = FileSamples.Glb(new Dictionary<string, object?> { ["extras"] = new { note = new string('y', 2000) } });
        var replaced = await Expect(_k.UploadAsync<HeritageModelDto>(HttpMethod.Put, $"{AdminRoot}/models/{model.Id}/file", admin, bigger));
        Assert.NotEqual(model.FileUrl, replaced.FileUrl);
        Assert.Equal(((long)bigger.Length, model.Credit, ContentStatus.PUBLISHED), (replaced.FileSizeBytes, replaced.Credit, replaced.Status));
        Assert.Equal(replaced.FileUrl, (await _k.VisibleModelAsync(heritage.Slug!)).FileUrl);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(model.FileUrl)).StatusCode);   // tệp cũ vẫn còn

        // Thay bằng tệp đã có trong kho, cùng lúc sửa thông tin.
        var stored = await _k.StoreAsync(admin);
        var byPath = await Expect(Http.PutAsync<HeritageModelDto>($"{AdminRoot}/models/{model.Id}",
            Declaration(stored.Url, license: "CC0"), admin));
        Assert.Equal((stored.Url, stored.Size, "CC0"), (byPath.FileUrl, byPath.FileSizeBytes, byPath.License));

        // Tệp hỏng không thay được, mô hình giữ nguyên.
        var (response, body) = await _k.UploadAsync<object>(HttpMethod.Put, $"{AdminRoot}/models/{model.Id}/file", admin, FileSamples.Png());
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID"), (response.StatusCode, body.ErrorCode));
        Assert.Equal(stored.Url, (await _k.ListAsync(admin, heritage.Id)).Single().FileUrl);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.UploadAsync<object>(HttpMethod.Put, $"{AdminRoot}/models/999999/file", admin, FileSamples.Glb())).Response.StatusCode);
    }

    [Fact]
    public async Task Removing_a_model_hides_it_from_everywhere_but_keeps_the_stored_file()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.PublishedHeritageAsync(admin);
        var model = await _k.UploadModelAsync(admin, heritage.Id, show: true);

        var (response, body) = await Http.DeleteAsync<object>($"{AdminRoot}/models/{model.Id}", admin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Gỡ mô hình 3D thành công.", body.Message);

        Assert.Empty(await _k.ListAsync(admin, heritage.Id));
        Assert.False(await _k.HasModelAsync(heritage.Slug!));
        Assert.Equal(HttpStatusCode.NotFound, (await _k.PublicModelAsync(heritage.Slug!)).Response.StatusCode);
        Assert.True(File.Exists(_k.FilePath(model.FileUrl)));
        foreach (var call in new[] { Http.DeleteAsync<object>($"{AdminRoot}/models/{model.Id}", admin), Http.PostAsync<object>($"{AdminRoot}/models/{model.Id}/show", null, admin) })
            Assert.Equal(HttpStatusCode.NotFound, (await call).Response.StatusCode);
    }

    // ---- Danh sách di sản của quản trị ----

    [Fact]
    public async Task The_heritage_list_shows_and_filters_by_having_a_visible_3D_model()
    {
        var admin = await _k.AdminAsync();
        var tag = Unique();
        var with = await Discovery().PublishedAsync(admin, new Spec($"Có mô hình {tag}"));
        var without = await Discovery().PublishedAsync(admin, new Spec($"Không mô hình {tag}"));
        var draftOnly = await Discovery().PublishedAsync(admin, new Spec($"Mô hình nháp {tag}"));
        await _k.UploadModelAsync(admin, with.Id, show: true);
        await _k.UploadModelAsync(admin, draftOnly.Id);

        async Task<List<(long Id, bool HasModel)>> Rows(string query) =>
            (await Expect(Http.GetAsync<PagedResult<HeritageSummaryDto>>($"{AdminRoot}/heritages?keyword={Uri.EscapeDataString(tag)}&pageSize=50{query}", admin)))
            .Items.Select(h => (h.Id, h.HasModel)).OrderBy(x => x.Id).ToList();

        Assert.Equal([(with.Id, true), (without.Id, false), (draftOnly.Id, false)], await Rows(string.Empty));
        Assert.Equal([(with.Id, true)], await Rows("&hasModel=true"));
        Assert.Equal([(without.Id, false), (draftOnly.Id, false)], await Rows("&hasModel=false"));
    }

    private DiscoveryTestKit Discovery() => _k.Discovery;

    // ---- Xem trước bản nháp ----

    [Fact]
    public async Task The_draft_preview_shows_the_view_3D_button_and_serves_the_model_only_to_the_admin_with_the_code()
    {
        var admin = await _k.AdminAsync();
        var draft = await _k.DraftHeritageAsync(admin);
        var model = await _k.UploadModelAsync(admin, draft.Id, show: true);
        var link = await Expect(Http.PostAsync<HeritagePreviewLinkDto>($"{AdminRoot}/heritages/{draft.Id}/preview", null, admin));

        var preview = await Expect(Http.GetAsync<HeritagePreviewDto>($"{PreviewRoot}/{link.Code}", admin));
        Assert.True(preview.Heritage.HasModel);
        var served = await Expect(Http.GetAsync<PublicModelDto>($"{PreviewRoot}/{link.Code}/model", admin));
        Assert.Equal((model.Id, model.FileUrl), (served.Id, served.FileUrl));

        // Không đăng nhập quản trị hệ thống, hoặc mã sai: cùng một 404 như trang xem trước.
        await Fail(Http.GetAsync<object>($"{PreviewRoot}/{link.Code}/model"), HttpStatusCode.NotFound, MessageCodes.PreviewInvalid);
        await Fail(Http.GetAsync<object>($"{PreviewRoot}/khong-dung/model", admin), HttpStatusCode.NotFound, MessageCodes.PreviewInvalid);

        // Bản nháp chưa có mô hình hiển thị: có trang xem trước nhưng không có nút và mô hình 404.
        await _k.HideAsync(admin, model.Id);
        Assert.False((await Expect(Http.GetAsync<HeritagePreviewDto>($"{PreviewRoot}/{link.Code}", admin))).Heritage.HasModel);
        await Fail(Http.GetAsync<object>($"{PreviewRoot}/{link.Code}/model", admin), HttpStatusCode.NotFound, MessageCodes.ModelUnavailable);
    }

    // ---- Nhật ký thao tác ----

    [Fact]
    public async Task Every_change_is_written_to_the_audit_log_without_the_file_path()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.PublishedHeritageAsync(admin);
        var model = await _k.UploadModelAsync(admin, heritage.Id);
        await _k.ShowAsync(admin, model.Id);
        await Expect(Http.PutAsync<HeritageModelDto>($"{AdminRoot}/models/{model.Id}", Declaration(model.FileUrl, license: "CC BY-SA 4.0"), admin));
        await Expect(_k.UploadAsync<HeritageModelDto>(HttpMethod.Put, $"{AdminRoot}/models/{model.Id}/file", admin, FileSamples.Glb()));
        await _k.HideAsync(admin, model.Id);
        await Http.DeleteAsync<object>($"{AdminRoot}/models/{model.Id}", admin);

        var logs = await factory.WithDbAsync(db => db.AuditLogs.AsNoTracking()
            .Where(l => l.Action == AuditAction.HERITAGE_MODEL_CHANGED && l.TargetId == model.Id).OrderBy(l => l.Id).ToListAsync());

        var actions = logs.Select(l => System.Text.Json.JsonDocument.Parse(l.NewValue!).RootElement.GetProperty("action").GetString()).ToList();
        Assert.Equal(["added", "shown", "updated", "replaced", "hidden", "removed"], actions);
        Assert.All(logs, l =>
        {
            Assert.Equal((ActorRole.ADMIN, AuditTargets.HeritageModels), (l.ActorRole, l.TargetType));
            Assert.DoesNotContain("/files/", l.NewValue);
            Assert.DoesNotContain(".glb", l.NewValue);
            Assert.Contains("\"sizeBytes\":", l.NewValue);
            Assert.Contains($"\"heritageId\":{heritage.Id}", l.NewValue);
        });
        Assert.Contains("\"license\":\"CC BY-SA 4.0\"", logs[2].NewValue);
        Assert.Equal("Thêm, thay, gỡ, ẩn hoặc hiện mô hình 3D của di sản", DSVHVN.Application.Audit.AuditLabels.Of(AuditAction.HERITAGE_MODEL_CHANGED));
    }

    [Fact]
    public async Task A_failed_request_leaves_no_audit_row_and_no_model()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.PublishedHeritageAsync(admin);
        var logsBefore = await factory.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == AuditAction.HERITAGE_MODEL_CHANGED));

        await _k.UploadAsync<object>(HttpMethod.Post, $"{ModelsOf(heritage.Id)}/upload", admin, FileSamples.Glb(), Credit(license: "CC BY-NC"));
        await Http.PostAsync<object>(ModelsOf(heritage.Id), Declaration("https://evil.example/x.glb"), admin);

        Assert.Equal(logsBefore, await factory.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == AuditAction.HERITAGE_MODEL_CHANGED)));
        Assert.Empty(await _k.ListAsync(admin, heritage.Id));
    }

    // ---- Danh sách ----

    [Fact]
    public async Task The_list_puts_the_visible_model_first_then_by_order()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.DraftHeritageAsync(admin);
        var a = await _k.UploadModelAsync(admin, heritage.Id, fields: Credit());
        var b = await _k.UploadModelAsync(admin, heritage.Id, fields: Credit());
        var c = await _k.UploadModelAsync(admin, heritage.Id, show: true);

        var order = (await _k.ListAsync(admin, heritage.Id)).Select(m => m.Id).ToList();

        Assert.Equal(c.Id, order[0]);
        Assert.Equal([b.Id, a.Id], order.Skip(1));   // cùng không có thứ tự: mới thêm trước
    }
}
