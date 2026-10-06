using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DSVHVN.Application.Heritages;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>Tải ảnh lên trên SQL Server thật (database tạm của LocalDB): dòng media được ghi bằng migration thật, tệp phục vụ được qua đường dẫn công khai.</summary>
public sealed class FileUploadSqlServerTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>
{
    private const string Api = "/api/v1/admin";

    private readonly PlatformClient _api = new(factory);
    private HttpClient Http => _api.Http;

    [LocalDbFact]
    public async Task An_uploaded_photo_is_recorded_in_media_and_the_file_is_served()
    {
        var admin = await _api.AdminTokenAsync();
        var (created, heritage) = await Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", new { name = "Di sản tải ảnh" }, admin);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var form = new MultipartFormDataContent
        {
            { new StringContent("Nguyễn Văn An"), "author" },
            { new StringContent("CC0"), "license" },
            { new StringContent("Nhóm tự chụp"), "source" },
            { new StringContent("true"), "setAsThumbnail" },
        };
        var file = new ByteArrayContent(FileSamples.WithExif(FileSamples.Jpeg(640, 480), 6));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "anh.jpg");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/heritages/{heritage.Data!.Id}/media/upload") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);

        var response = await Http.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<Envelope<MediaDto>>(HttpTestExtensions.Json);

        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode} {body?.Message}");
        var media = body!.Data!;
        var row = await factory.WithDbAsync(db => db.Media.SingleAsync(m => m.Id == media.Id));
        Assert.Equal((media.Url, "Nguyễn Văn An · CC0 · Nhóm tự chụp", "IMAGE"), (row.Url, row.Credit, row.MediaType));
        Assert.Equal(media.Url, (await factory.WithDbAsync(db => db.Heritages.SingleAsync(h => h.Id == heritage.Data.Id))).ThumbnailUrl);

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(media.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(media.PreviewUrl)).StatusCode);
    }
}
