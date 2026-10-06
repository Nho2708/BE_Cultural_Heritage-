using System.Net;
using System.Text;
using DSVHVN.Application.Files;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Files;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Kho tệp Cloudinary với HTTP giả (không gọi Cloudinary thật): khóa thành mã công khai, đường dẫn HTTPS, ảnh thu nhỏ là địa chỉ biến đổi,
/// tải lên và xóa gửi đúng yêu cầu có ký mà không lộ bí mật API, mọi lỗi thành <see cref="FileStorageException"/>.
/// </summary>
public sealed class CloudinaryFileStorageTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private const string Secret = "unit-test-secret-do-not-leak";
    private const string ApiKey = "123456789012345";

    private static CloudinaryFileStorage Create(FakeCloudinaryHandler handler, string folder = "dsvhvn") =>
        new(Options.Create(new StorageOptions
        {
            Provider = StorageProvider.Cloudinary,
            Cloudinary = new CloudinaryStorageOptions { CloudName = "demo-cloud", ApiKey = ApiKey, ApiSecret = Secret, Folder = folder },
        }), new HttpClient(handler));

    [Fact]
    public void Urls_are_https_and_computed_from_the_key_without_calling_cloudinary()
    {
        var handler = new FakeCloudinaryHandler();
        var storage = Create(handler);

        Assert.Equal($"https://res.cloudinary.com/demo-cloud/image/upload/dsvhvn/images/{Id}.jpg", storage.PublicUrl($"images/{Id}.jpg"));
        Assert.Equal($"https://res.cloudinary.com/demo-cloud/image/upload/dsvhvn/images/{Id}.png", storage.PublicUrl($"images/{Id}.png"));
        Assert.Equal($"https://res.cloudinary.com/demo-cloud/image/upload/c_limit,w_320,q_auto/dsvhvn/images/{Id}.webp",
            storage.PublicUrl($"images/{Id}-thumb.webp"));
        Assert.Equal($"https://res.cloudinary.com/demo-cloud/raw/upload/dsvhvn/models/{Id}.glb", storage.PublicUrl($"models/{Id}.glb"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void The_thumbnail_address_matches_the_rule_that_derives_it_from_the_stored_image_address()
    {
        var storage = Create(new FakeCloudinaryHandler(), "dsvhvn-thu");
        var image = storage.PublicUrl($"images/{Id}.jpg");
        Assert.Equal(storage.PublicUrl($"images/{Id}-thumb.webp"), StoredFileRules.ThumbnailUrlOf(image));
    }

    [Fact]
    public void An_empty_folder_falls_back_to_the_default_and_the_custom_folder_is_used()
    {
        Assert.Contains("/dsvhvn/images/", Create(new FakeCloudinaryHandler(), " ").PublicUrl($"images/{Id}.jpg"));
        Assert.Contains("/dsvhvn-thu/images/", Create(new FakeCloudinaryHandler(), "dsvhvn-thu").PublicUrl($"images/{Id}.jpg"));
    }

    [Theory]
    [InlineData("../secret.jpg")]
    [InlineData("images/../x.jpg")]
    [InlineData("images/name.jpg")]
    [InlineData("/images/0123456789abcdef0123456789abcdef.jpg")]
    [InlineData("")]
    public async Task Only_keys_generated_by_the_system_are_accepted(string key)
    {
        var handler = new FakeCloudinaryHandler();
        var storage = Create(handler);
        Assert.Throws<ArgumentException>(() => storage.PublicUrl(key));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.SaveAsync(key, new byte[] { 1 }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.DeleteAsync(key, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_image_is_uploaded_as_an_image_with_a_signed_request_and_a_fixed_public_id()
    {
        var handler = new FakeCloudinaryHandler();
        var storage = Create(handler);
        var key = $"images/{Id}.jpg";

        await storage.SaveAsync(key, Encoding.ASCII.GetBytes("pretend-jpeg-bytes"), default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.cloudinary.com/v1_1/demo-cloud/image/upload", request.Url);
        Assert.Contains($"dsvhvn/images/{Id}", request.Body);
        Assert.Contains("pretend-jpeg-bytes", request.Body);
        Assert.Contains(ApiKey, request.Body);
        Assert.Contains("signature", request.Body);
        Assert.Contains("overwrite", request.Body);
        Assert.DoesNotContain(Secret, request.Body);
        Assert.DoesNotContain(Secret, request.Url);
    }

    [Fact]
    public async Task A_glb_model_is_uploaded_as_raw_and_keeps_its_extension_in_the_public_id()
    {
        var handler = new FakeCloudinaryHandler();
        var storage = Create(handler);

        await storage.SaveAsync($"models/{Id}.glb", Encoding.ASCII.GetBytes("glTF-bytes"), default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.cloudinary.com/v1_1/demo-cloud/raw/upload", request.Url);
        Assert.Contains($"dsvhvn/models/{Id}.glb", request.Body);
        Assert.DoesNotContain(Secret, request.Body);
    }

    [Fact]
    public async Task The_thumbnail_is_derived_by_url_so_saving_and_deleting_it_call_nothing()
    {
        var handler = new FakeCloudinaryHandler();
        var storage = Create(handler);
        var key = $"images/{Id}-thumb.webp";

        await storage.SaveAsync(key, new byte[] { 1, 2, 3 }, default);
        await storage.DeleteAsync(key, default);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Delete_asks_cloudinary_to_destroy_the_image_by_public_id_and_a_missing_file_counts_as_deleted()
    {
        var handler = new FakeCloudinaryHandler();
        var storage = Create(handler);

        await storage.DeleteAsync($"images/{Id}.png", default);
        handler.DestroyResult = "not found";
        await storage.DeleteAsync($"images/{Id}.png", default);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("https://api.cloudinary.com/v1_1/demo-cloud/image/destroy", r.Url);
            Assert.Contains($"dsvhvn/images/{Id}", r.Body);
            Assert.DoesNotContain(Secret, r.Body);
        });
    }

    [Fact]
    public async Task A_model_is_destroyed_as_raw()
    {
        var handler = new FakeCloudinaryHandler();
        await Create(handler).DeleteAsync($"models/{Id}.glb", default);
        Assert.Equal("https://api.cloudinary.com/v1_1/demo-cloud/raw/destroy", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task Exists_reflects_the_resource_lookup()
    {
        var handler = new FakeCloudinaryHandler();
        var storage = Create(handler);
        Assert.True(await storage.ExistsAsync($"images/{Id}.jpg", default));

        handler.ResourceStatus = HttpStatusCode.NotFound;
        Assert.False(await storage.ExistsAsync($"images/{Id}.jpg", default));

        handler.ResourceStatus = HttpStatusCode.InternalServerError;
        await Assert.ThrowsAsync<FileStorageException>(() => storage.ExistsAsync($"images/{Id}.jpg", default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_rejected_upload_becomes_a_storage_error_that_does_not_carry_secrets(HttpStatusCode status)
    {
        var handler = new FakeCloudinaryHandler { UploadStatus = status };
        var ex = await Assert.ThrowsAsync<FileStorageException>(() => Create(handler).SaveAsync($"images/{Id}.jpg", new byte[] { 1 }, default));
        Assert.DoesNotContain(Secret, ex.Message);
        Assert.DoesNotContain(ApiKey, ex.Message);
    }

    [Fact]
    public async Task A_network_failure_becomes_a_storage_error_for_upload_delete_and_exists()
    {
        var handler = new FakeCloudinaryHandler { Throw = new HttpRequestException("no route to host") };
        var storage = Create(handler);
        await Assert.ThrowsAsync<FileStorageException>(() => storage.SaveAsync($"images/{Id}.jpg", new byte[] { 1 }, default));
        await Assert.ThrowsAsync<FileStorageException>(() => storage.DeleteAsync($"images/{Id}.jpg", default));
        await Assert.ThrowsAsync<FileStorageException>(() => storage.ExistsAsync($"images/{Id}.jpg", default));
    }

    [Fact]
    public async Task A_failed_delete_response_becomes_a_storage_error()
    {
        var handler = new FakeCloudinaryHandler { DestroyStatus = HttpStatusCode.InternalServerError };
        await Assert.ThrowsAsync<FileStorageException>(() => Create(handler).DeleteAsync($"images/{Id}.jpg", default));
    }

    [Fact]
    public async Task A_reply_with_another_public_id_is_not_trusted()
    {
        var handler = new FakeCloudinaryHandler { ReplyPublicId = "someone/else" };
        await Assert.ThrowsAsync<FileStorageException>(() => Create(handler).SaveAsync($"images/{Id}.jpg", new byte[] { 1 }, default));
    }

    public sealed record Captured(HttpMethod Method, string Url, string Body);

    /// <summary>HTTP giả của Cloudinary: ghi lại yêu cầu, trả JSON giống Cloudinary theo đường dẫn.</summary>
    public sealed class FakeCloudinaryHandler : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];
        public HttpStatusCode UploadStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode DestroyStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode ResourceStatus { get; set; } = HttpStatusCode.OK;
        public string DestroyResult { get; set; } = "ok";
        public string? ReplyPublicId { get; set; }
        public Exception? Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw is not null) throw Throw;
            var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
            var body = request.Content is null ? string.Empty : Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            Requests.Add(new Captured(request.Method, url, body));

            if (url.EndsWith("/upload", StringComparison.Ordinal))
            {
                var isRaw = url.Contains("/raw/", StringComparison.Ordinal);
                var publicId = ReplyPublicId ?? ExtractPublicId(body);
                return Json(UploadStatus, UploadStatus == HttpStatusCode.OK
                    ? $$"""{"public_id":"{{publicId}}","version":1712345678,"resource_type":"{{(isRaw ? "raw" : "image")}}","bytes":10,"secure_url":"https://res.cloudinary.com/demo-cloud/{{(isRaw ? "raw" : "image")}}/upload/v1712345678/{{publicId}}"}"""
                    : """{"error":{"message":"Invalid API key"}}""");
            }
            if (url.EndsWith("/destroy", StringComparison.Ordinal))
                return Json(DestroyStatus, $$"""{"result":"{{DestroyResult}}"}""");
            return Json(ResourceStatus, ResourceStatus == HttpStatusCode.OK
                ? """{"public_id":"x","resource_type":"image","bytes":10}"""
                : """{"error":{"message":"Resource not found"}}""");
        }

        private static string ExtractPublicId(string body)
        {
            var marker = "name=\"public_id\"";
            var at = body.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return "missing";
            var start = body.IndexOf("\r\n\r\n", at, StringComparison.Ordinal) + 4;
            return body[start..body.IndexOf("\r\n", start, StringComparison.Ordinal)];
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
