using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DSVHVN.Application.Files;
using DSVHVN.Application.Heritages;
using DSVHVN.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Chỉ chạy khi đặt biến môi trường <c>DSVHVN_CLOUDINARY_LIVE=1</c>: đây là lần thử THẬT duy nhất với Cloudinary, mặc định bị bỏ qua nên
/// <c>dotnet test</c> bình thường không gọi mạng. Các khóa lấy từ User Secrets của <c>src/DSVHVN.Api</c> (chỉ ba khóa <c>Storage:Cloudinary:*</c>,
/// không nạp phần còn lại của User Secrets) và không bao giờ được in ra.
/// </summary>
public sealed class LiveCloudinaryFactAttribute : FactAttribute
{
    public const string Switch = "DSVHVN_CLOUDINARY_LIVE";

    public LiveCloudinaryFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Switch) != "1")
            Skip = $"Lần thử thật với Cloudinary: đặt {Switch}=1 để chạy.";
    }
}

public sealed class CloudinaryLiveApiFactory : ApiFactory
{
    public const string TestFolder = "dsvhvn-thu";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var secrets = new ConfigurationBuilder().AddUserSecrets("dsvhvn-api-3f6b1c2e").AddEnvironmentVariables().Build();
        var settings = new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Cloudinary",
            ["Storage:Cloudinary:Folder"] = TestFolder,
        };
        foreach (var key in new[] { "CloudName", "ApiKey", "ApiSecret" })
            settings[$"Storage:Cloudinary:{key}"] = secrets[$"Storage:Cloudinary:{key}"] ?? secrets[$"Storage__Cloudinary__{key}"];
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
    }
}

public sealed class CloudinaryLiveTests(CloudinaryLiveApiFactory factory) : IClassFixture<CloudinaryLiveApiFactory>
{
    private readonly PlatformClient _api = new(factory);

    [LiveCloudinaryFact]
    public async Task One_small_image_goes_through_the_backend_to_cloudinary_opens_over_https_and_is_deleted_through_the_api()
    {
        var token = await _api.AdminTokenAsync();
        var heritage = await DiscoveryTestKit.Expect(_api.Http.PostAsync<HeritageDetailDto>("/api/v1/admin/heritages",
            new { name = "Thử Cloudinary " + PlatformClient.Unique() }, token), HttpStatusCode.Created);

        using var form = new MultipartFormDataContent();
        foreach (var (key, value) in new Dictionary<string, string>
                 { ["author"] = "Nhóm DSVHVN", ["license"] = "Ảnh nhóm tự chụp", ["source"] = "Ảnh thử tự sinh" })
            form.Add(new StringContent(value), key);
        form.Add(new ByteArrayContent(FileSamples.Png(64, 32)), "file", "thu.png");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/heritages/{heritage.Id}/media/upload") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _api.Http.SendAsync(request);
        var body = (await response.Content.ReadFromJsonAsync<Envelope<MediaDto>>(HttpTestExtensions.Json))!;
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode} {body.ErrorCode}");

        var media = body.Data!;
        Assert.NotNull(media.PreviewUrl);
        var mediaUrl = media.Url!;
        var storage = factory.Services.GetRequiredService<IFileStorage>();
        var storedKey = mediaUrl[(mediaUrl.IndexOf("/" + CloudinaryLiveApiFactory.TestFolder + "/", StringComparison.Ordinal) + CloudinaryLiveApiFactory.TestFolder.Length + 2)..];
        try
        {
            Assert.StartsWith("https://res.cloudinary.com/", mediaUrl);
            Assert.Contains("/" + CloudinaryLiveApiFactory.TestFolder + "/images/", mediaUrl);

            // Ảnh gốc và ảnh thu nhỏ (địa chỉ biến đổi) đều mở được qua HTTPS và đúng là ảnh.
            using var cdn = new HttpClient();
            foreach (var url in new[] { mediaUrl, media.PreviewUrl! })
            {
                var image = await cdn.GetAsync(url);
                Assert.True(image.StatusCode == HttpStatusCode.OK, $"GET ảnh trả {(int)image.StatusCode}");
                Assert.StartsWith("image/", image.Content.Headers.ContentType?.MediaType ?? string.Empty);
                Assert.True((await image.Content.ReadAsByteArrayAsync()).Length > 0);
            }

            Assert.True(await storage.ExistsAsync(storedKey, default), "Ảnh phải có trên Cloudinary sau khi tải lên.");
        }
        finally
        {
            // Xóa bằng API của Cloudinary, kể cả khi một kiểm tra ở trên hỏng, để không để rác trong thư mục thử.
            await storage.DeleteAsync(storedKey, default);
        }

        Assert.False(await storage.ExistsAsync(storedKey, default), "Ảnh phải không còn trên Cloudinary sau khi xóa bằng API.");
    }
}
