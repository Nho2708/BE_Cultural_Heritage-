using System.Net;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Models;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static DSVHVN.Tests.Integration.DiscoveryTestKit;
using static DSVHVN.Tests.Integration.HeritageModelTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Mô hình 3D khi kho tệp là Cloudinary (HTTP giả): tệp tải lên loại raw, đường dẫn lưu là địa chỉ HTTPS của Cloudinary, đường dẫn dán vào chỉ nhận địa chỉ của đúng kho này,
/// dung lượng do kho đo, Cloudinary lỗi thì 503 và không ghi gì.
/// </summary>
public sealed class HeritageModelCloudinaryApiTests(CloudinaryApiFactory factory) : IClassFixture<CloudinaryApiFactory>
{
    private readonly HeritageModelTestKit _k = new(factory);
    private HttpClient Http => _k.Http;

    private const string RawPrefix = "https://res.cloudinary.com/demo-cloud/raw/upload/dsvhvn-thu/models/";

    [Fact]
    public async Task An_uploaded_model_goes_to_cloudinary_as_raw_and_the_https_address_is_what_visitors_get()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.PublishedHeritageAsync(admin);
        var glb = FileSamples.Glb();
        factory.Cloud.Requests.Clear();

        var model = await _k.UploadModelAsync(admin, heritage.Id, show: true, glb: glb);

        Assert.StartsWith(RawPrefix, model.FileUrl);
        Assert.EndsWith(".glb", model.FileUrl);
        Assert.Equal((long)glb.Length, model.FileSizeBytes);
        var upload = Assert.Single(factory.Cloud.Requests);
        Assert.Equal("https://api.cloudinary.com/v1_1/demo-cloud/raw/upload", upload.Url);
        Assert.DoesNotContain(CloudinaryApiFactory.Secret, upload.Body);
        Assert.Equal(model.FileUrl, (await _k.VisibleModelAsync(heritage.Slug!)).FileUrl);
        Assert.Equal(model.FileUrl, (await factory.WithDbAsync(db => db.HeritageModels.AsNoTracking().SingleAsync(m => m.Id == model.Id))).FileUrl);
        Assert.False(Directory.Exists(factory.StorageRoot) && Directory.GetFiles(factory.StorageRoot, "*", SearchOption.AllDirectories).Length > 0);
    }

    [Fact]
    public async Task A_pasted_address_must_be_this_cloud_and_this_folder_and_the_size_comes_from_cloudinary()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.DraftHeritageAsync(admin);
        var stored = await _k.StoreAsync(admin);
        var key = KeyOf(stored.Url);

        var model = await Expect(Http.PostAsync<HeritageModelDto>($"{AdminRoot}/heritages/{heritage.Id}/models", new
        {
            fileUrl = stored.Url, author = "Nguyễn Văn An", license = "CC0", sourceUrl = "https://sketchfab.com/models/x",
        }, admin), HttpStatusCode.Created);
        Assert.Equal((stored.Url, 10L), (model.FileUrl, model.FileSizeBytes));   // HTTP giả của Cloudinary báo 10 byte

        foreach (var other in new[]
                 {
                     $"https://res.cloudinary.com/other-cloud/raw/upload/dsvhvn-thu/{key}",
                     $"https://res.cloudinary.com/demo-cloud/raw/upload/khac/{key}",
                     $"https://res.cloudinary.com/demo-cloud/image/upload/dsvhvn-thu/{key}",
                 })
        {
            var (response, body) = await Http.PostAsync<object>($"{AdminRoot}/heritages/{heritage.Id}/models", new
            {
                fileUrl = other, author = "An", license = "CC0", sourceUrl = "https://sketchfab.com/models/x",
            }, admin);
            Assert.Equal((HttpStatusCode.BadRequest, "INVALID"), (response.StatusCode, body.ErrorCode));
        }
        Assert.Single(await _k.ListAsync(admin, heritage.Id));
    }

    [Fact]
    public async Task A_cloudinary_failure_gives_503_and_records_nothing()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.DraftHeritageAsync(admin);
        var stored = await _k.StoreAsync(admin);
        try
        {
            factory.Cloud.UploadStatus = HttpStatusCode.InternalServerError;
            var (upload, uploadBody) = await _k.UploadAsync<object>(HttpMethod.Post, $"{AdminRoot}/heritages/{heritage.Id}/models/upload", admin,
                FileSamples.Glb(), Credit());
            Assert.Equal((HttpStatusCode.ServiceUnavailable, "STORAGE_UNAVAILABLE"), (upload.StatusCode, uploadBody.ErrorCode));
            Assert.DoesNotContain(CloudinaryApiFactory.Secret, uploadBody.Message);

            factory.Cloud.ResourceStatus = HttpStatusCode.InternalServerError;   // không đo được dung lượng
            var (declare, declareBody) = await Http.PostAsync<object>($"{AdminRoot}/heritages/{heritage.Id}/models", new
            {
                fileUrl = stored.Url, author = "An", license = "CC0", sourceUrl = "https://sketchfab.com/models/x",
            }, admin);
            Assert.Equal((HttpStatusCode.ServiceUnavailable, "STORAGE_UNAVAILABLE"), (declare.StatusCode, declareBody.ErrorCode));
        }
        finally
        {
            factory.Cloud.UploadStatus = HttpStatusCode.OK;
            factory.Cloud.ResourceStatus = HttpStatusCode.OK;
        }
        Assert.Empty(await _k.ListAsync(admin, heritage.Id));
    }
}
