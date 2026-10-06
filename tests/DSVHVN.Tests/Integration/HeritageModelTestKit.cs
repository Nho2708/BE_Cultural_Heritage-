using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DSVHVN.Application.Files;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Models;
using DSVHVN.Tests.Support;
using static DSVHVN.Tests.Integration.DiscoveryTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Dựng dữ liệu và gọi API cho các test mô hình 3D: di sản đã xuất bản hoặc nháp, tải tệp <c>.glb</c> (lên kho hoặc kèm khai báo), khai báo, hiện, ẩn, gỡ
/// và đọc công khai.
/// </summary>
public sealed class HeritageModelTestKit(ApiFactory factory)
{
    public const string AdminRoot = "/api/v1/admin";
    public const string PublicRoot = "/api/v1/public";
    public const string PreviewRoot = "/api/v1/preview/heritages";

    public DiscoveryTestKit Discovery { get; } = new(factory);
    public ApiFactory Factory { get; } = factory;
    public HttpClient Http => Discovery.Http;

    public Task<string> AdminAsync() => Discovery.Api.AdminTokenAsync();

    /// <summary>Ghi công hợp lệ mặc định: CC BY 4.0, có trang nguồn.</summary>
    public static Dictionary<string, string> Credit(string? author = null, string license = "CC BY 4.0", string? sourceUrl = "https://sketchfab.com/models/khue-van-cac") =>
        new()
        {
            ["title"] = "Khuê Văn Các",
            ["author"] = author ?? "Nguyễn Văn An",
            ["license"] = license,
            ["licenseUrl"] = "https://creativecommons.org/licenses/by/4.0/",
            ["sourceUrl"] = sourceUrl ?? string.Empty,
        };

    public Task<HeritageDetailDto> PublishedHeritageAsync(string admin) =>
        Discovery.PublishedAsync(admin, new Spec("Văn Miếu " + Unique()));

    public Task<HeritageDetailDto> DraftHeritageAsync(string admin) =>
        Discovery.DraftAsync(admin, new Spec("Văn Miếu nháp " + Unique()));

    public async Task<(HttpResponseMessage Response, Envelope<T> Body)> UploadAsync<T>(
        HttpMethod method, string url, string? token, byte[]? file, IDictionary<string, string>? fields = null, string fileName = "mo-hinh.glb")
    {
        using var form = new MultipartFormDataContent();
        foreach (var (key, value) in fields ?? new Dictionary<string, string>()) form.Add(new StringContent(value), key);
        if (file is not null)
        {
            var part = new ByteArrayContent(file);
            part.Headers.ContentType = new MediaTypeHeaderValue("model/gltf-binary");
            form.Add(part, "file", fileName);
        }
        using var request = new HttpRequestMessage(method, url) { Content = form };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await Http.SendAsync(request);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(HttpTestExtensions.Json);
        return (response, envelope!);
    }

    /// <summary>Tải tệp lên kho (chưa gắn vào di sản nào) và trả đường dẫn trong kho.</summary>
    public async Task<StoredFileDto> StoreAsync(string admin, byte[]? glb = null) =>
        await Expect(UploadAsync<StoredFileDto>(HttpMethod.Post, $"{AdminRoot}/files/models", admin, glb ?? FileSamples.Glb()), HttpStatusCode.Created);

    /// <summary>Tải tệp lên và khai báo cho di sản; <paramref name="show"/> true thì hiện luôn.</summary>
    public async Task<HeritageModelDto> UploadModelAsync(string admin, long heritageId, bool show = false, byte[]? glb = null,
        IDictionary<string, string>? fields = null)
    {
        var all = new Dictionary<string, string>(fields ?? Credit());
        if (show) all["show"] = "true";
        return await Expect(UploadAsync<HeritageModelDto>(HttpMethod.Post, $"{AdminRoot}/heritages/{heritageId}/models/upload", admin,
            glb ?? FileSamples.Glb(), all), HttpStatusCode.Created);
    }

    public Task<IReadOnlyList<HeritageModelDto>> ListAsync(string admin, long heritageId) =>
        Expect(Http.GetAsync<IReadOnlyList<HeritageModelDto>>($"{AdminRoot}/heritages/{heritageId}/models", admin));

    public Task<HeritageModelDto> ShowAsync(string admin, long modelId) =>
        Expect(Http.PostAsync<HeritageModelDto>($"{AdminRoot}/models/{modelId}/show", null, admin));

    public Task<HeritageModelDto> HideAsync(string admin, long modelId) =>
        Expect(Http.PostAsync<HeritageModelDto>($"{AdminRoot}/models/{modelId}/hide", null, admin));

    public Task<(HttpResponseMessage Response, Envelope<PublicModelDto> Body)> PublicModelAsync(string slug) =>
        Http.GetAsync<PublicModelDto>($"{PublicRoot}/heritages/{slug}/model");

    public async Task<PublicModelDto> VisibleModelAsync(string slug) => await Expect(PublicModelAsync(slug));

    public async Task<bool> HasModelAsync(string slug) =>
        (await Expect(Http.GetAsync<DSVHVN.Application.Discovery.PublicHeritageDetailDto>($"{PublicRoot}/heritages/{slug}"))).HasModel;

    /// <summary>Đường dẫn trong kho (<c>models/{mã}.glb</c>) của một đường dẫn công khai.</summary>
    public static string KeyOf(string url) => url[(url.IndexOf("models/", StringComparison.Ordinal))..];

    public string FilePath(string url) => Path.Combine(Factory.StorageRoot, KeyOf(url).Replace('/', Path.DirectorySeparatorChar));
}
