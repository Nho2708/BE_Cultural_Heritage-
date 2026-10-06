using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DSVHVN.Application.Files;
using DSVHVN.Application.Heritages;
using DSVHVN.Infrastructure.Files;
using DSVHVN.Tests.Support;
using DSVHVN.Tests.Unit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Integration;

/// <summary>Api chạy với <c>Storage:Provider = Cloudinary</c> nhưng gọi HTTP giả (không gọi Cloudinary thật).</summary>
public sealed class CloudinaryApiFactory : ApiFactory
{
    public const string Secret = "api-factory-secret-do-not-leak";

    public CloudinaryFileStorageTests.FakeCloudinaryHandler Cloud { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Cloudinary",
            ["Storage:Cloudinary:CloudName"] = "demo-cloud",
            ["Storage:Cloudinary:ApiKey"] = "123456789012345",
            ["Storage:Cloudinary:ApiSecret"] = Secret,
            ["Storage:Cloudinary:Folder"] = "dsvhvn-thu",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFileStorage>();
            services.AddSingleton<IFileStorage>(sp =>
                new CloudinaryFileStorage(sp.GetRequiredService<IOptions<StorageOptions>>(), new HttpClient(Cloud)));
        });
    }
}

/// <summary>
/// Tải ảnh và mô hình 3D khi dùng Cloudinary: địa chỉ lưu vào di sản là URL HTTPS của Cloudinary, ảnh thu nhỏ là địa chỉ biến đổi (không tải thêm tệp),
/// Cloudinary lỗi thì 503 và di sản giữ nguyên, bí mật API không xuất hiện trong phản hồi.
/// </summary>
public sealed class CloudinaryUploadApiTests(CloudinaryApiFactory factory) : IClassFixture<CloudinaryApiFactory>
{
    private const string Api = "/api/v1/admin";
    private readonly PlatformClient _api = new(factory);
    private HttpClient Http => _api.Http;

    private async Task<(HttpResponseMessage Response, Envelope<T> Body)> UploadAsync<T>(string url, string token, byte[] file, string name)
    {
        using var form = new MultipartFormDataContent();
        foreach (var (key, value) in new Dictionary<string, string>
                 { ["author"] = "Nguyễn Văn An", ["license"] = "CC BY-SA 4.0", ["source"] = "Nhóm tự chụp, 2026" })
            form.Add(new StringContent(value), key);
        form.Add(new ByteArrayContent(file), "file", name);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await Http.SendAsync(request);
        return (response, (await response.Content.ReadFromJsonAsync<Envelope<T>>(HttpTestExtensions.Json))!);
    }

    private async Task<(string Token, HeritageDetailDto Heritage)> HeritageAsync()
    {
        var token = await _api.AdminTokenAsync();
        var heritage = await DiscoveryTestKit.Expect(
            Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", new { name = "Di sản đám mây " + PlatformClient.Unique() }, token), HttpStatusCode.Created);
        return (token, heritage);
    }

    [Fact]
    public async Task An_uploaded_image_is_stored_on_cloudinary_and_the_https_address_goes_into_the_gallery()
    {
        var (token, heritage) = await HeritageAsync();
        factory.Cloud.Requests.Clear();

        var (res, body) = await UploadAsync<MediaDto>($"{Api}/heritages/{heritage.Id}/media/upload", token, FileSamples.Png(), "anh.png");

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var media = body.Data!;
        Assert.StartsWith("https://res.cloudinary.com/demo-cloud/image/upload/dsvhvn-thu/images/", media.Url);
        Assert.EndsWith(".png", media.Url);
        Assert.StartsWith("https://res.cloudinary.com/demo-cloud/image/upload/c_limit,w_320,q_auto/dsvhvn-thu/images/", media.PreviewUrl);
        Assert.EndsWith(".webp", media.PreviewUrl);

        // Chỉ một lần tải ảnh gốc; ảnh thu nhỏ do Cloudinary tự sinh bằng địa chỉ biến đổi.
        var upload = Assert.Single(factory.Cloud.Requests);
        Assert.Equal("https://api.cloudinary.com/v1_1/demo-cloud/image/upload", upload.Url);
        Assert.DoesNotContain(CloudinaryApiFactory.Secret, upload.Body);

        var stored = await factory.WithDbAsync(db => db.Media.AsNoTracking().SingleAsync(m => m.Id == media.Id));
        Assert.Equal(media.Url, stored.Url);
        Assert.False(Directory.Exists(factory.StorageRoot) && Directory.GetFiles(factory.StorageRoot, "*", SearchOption.AllDirectories).Length > 0);
    }

    [Fact]
    public async Task A_cloudinary_failure_gives_503_with_the_retry_message_and_leaves_the_heritage_untouched()
    {
        var (token, heritage) = await HeritageAsync();
        factory.Cloud.UploadStatus = HttpStatusCode.InternalServerError;
        try
        {
            var (res, body) = await UploadAsync<MediaDto>($"{Api}/heritages/{heritage.Id}/media/upload", token, FileSamples.Jpeg(), "anh.jpg");

            Assert.Equal((HttpStatusCode.ServiceUnavailable, "STORAGE_UNAVAILABLE"), (res.StatusCode, body.ErrorCode));
            Assert.Contains("thử tải lại", body.Message);
            Assert.DoesNotContain(CloudinaryApiFactory.Secret, body.Message);
            Assert.Equal(0, await factory.WithDbAsync(db => db.Media.CountAsync(m => m.HeritageId == heritage.Id)));
        }
        finally
        {
            factory.Cloud.UploadStatus = HttpStatusCode.OK;
        }

        var (retry, _) = await UploadAsync<MediaDto>($"{Api}/heritages/{heritage.Id}/media/upload", token, FileSamples.Jpeg(), "anh.jpg");
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task A_network_failure_is_also_a_503_and_the_image_rules_still_apply_before_any_call_to_cloudinary()
    {
        var (token, heritage) = await HeritageAsync();
        factory.Cloud.Requests.Clear();

        var (bad, badBody) = await UploadAsync<MediaDto>($"{Api}/heritages/{heritage.Id}/media/upload", token, "không phải ảnh"u8.ToArray(), "x.jpg");
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID"), (bad.StatusCode, badBody.ErrorCode));
        Assert.Empty(factory.Cloud.Requests);

        factory.Cloud.Throw = new HttpRequestException("no route to host");
        try
        {
            var (res, body) = await UploadAsync<MediaDto>($"{Api}/heritages/{heritage.Id}/media/upload", token, FileSamples.Jpeg(), "anh.jpg");
            Assert.Equal((HttpStatusCode.ServiceUnavailable, "STORAGE_UNAVAILABLE"), (res.StatusCode, body.ErrorCode));
        }
        finally
        {
            factory.Cloud.Throw = null;
        }
    }

    [Fact]
    public async Task A_glb_model_goes_to_cloudinary_as_a_raw_file_and_returns_its_https_address()
    {
        var token = await _api.AdminTokenAsync();
        factory.Cloud.Requests.Clear();

        var (res, body) = await UploadAsync<StoredFileDto>($"{Api}/files/models", token, FileSamples.Glb(), "mo-hinh.glb");

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.StartsWith("https://res.cloudinary.com/demo-cloud/raw/upload/dsvhvn-thu/models/", body.Data!.Url);
        Assert.EndsWith(".glb", body.Data.Url);
        Assert.Equal("https://api.cloudinary.com/v1_1/demo-cloud/raw/upload", Assert.Single(factory.Cloud.Requests).Url);
    }
}
