using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DSVHVN.Application.Narrations;
using DSVHVN.Tests.Support;
using static DSVHVN.Tests.Integration.DiscoveryTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Dựng dữ liệu và gọi API cho các test thuyết minh tạo sẵn: di sản có tóm tắt đã duyệt (qua bản giả lập AI của luồng Đa ngôn ngữ), danh sách còn thiếu,
/// tải tệp MP3 kèm mốc câu lên, đọc tóm tắt công khai.
/// </summary>
public sealed class NarrationTestKit(ApiFactory factory)
{
    public const string AdminRoot = "/api/v1/admin";

    public TranslationTestKit Translations { get; } = new(factory);
    public ApiFactory Factory { get; } = factory;
    public HttpClient Http => Translations.Http;

    public Task<string> AdminAsync() => Translations.AdminAsync();

    /// <summary>Di sản đã xuất bản, đã dịch và tóm tắt bằng AI giả lập, tóm tắt tiếng Việt và tiếng Anh đã duyệt (Hàn, Trung, Nhật là bản máy, hiện ngay).</summary>
    public async Task<TranslationTestKit.Seeded> ApprovedHeritageAsync(string admin)
    {
        var seed = await Translations.SeedAsync(admin);
        await Translations.GenerateAsync(admin, seed.Heritage.Id);
        await Translations.ApproveAllAsync(admin, seed.Heritage.Id);
        return seed;
    }

    public Task<(HttpResponseMessage Response, Envelope<MissingNarrationsDto> Body)> MissingRawAsync(string? token, string language, int? limit = null) =>
        Http.GetAsync<MissingNarrationsDto>($"{AdminRoot}/narrations/missing?language={Uri.EscapeDataString(language)}" + (limit is null ? "" : $"&limit={limit}"), token);

    public async Task<MissingNarrationsDto> MissingAsync(string token, string language, int? limit = null) => await Expect(MissingRawAsync(token, language, limit));

    /// <summary>Mục của một di sản trong danh sách còn thiếu của một ngôn ngữ; null nếu không có.</summary>
    public async Task<MissingNarrationItemDto?> MissingItemAsync(string token, string language, long heritageId) =>
        (await MissingAsync(token, language)).Items.SingleOrDefault(i => i.HeritageId == heritageId);

    /// <summary>Các trường chữ của biểu mẫu tải lên với hai câu mẫu và giọng mặc định.</summary>
    public static Dictionary<string, string> Fields(string textHash, string voice = "vieneu-mai-anh", int durationMs = 9000, string? sentences = null) =>
        new()
        {
            ["textHash"] = textHash,
            ["voice"] = voice,
            ["durationMs"] = durationMs.ToString(),
            ["sentences"] = sentences ?? Sentences(("Câu thứ nhất của tóm tắt.", 150, 4200), ("Câu thứ hai của tóm tắt.", 4550, 8900)),
        };

    public static string Sentences(params (string Text, int Start, int End)[] items) =>
        JsonSerializer.Serialize(items.Select(i => new { text = i.Text, startMs = i.Start, endMs = i.End }));

    public async Task<(HttpResponseMessage Response, Envelope<T> Body)> UploadRawAsync<T>(
        string? token, long heritageId, string language, IDictionary<string, string>? fields, byte[]? audio, string fileName = "thuyet-minh.mp3")
    {
        using var form = new MultipartFormDataContent();
        foreach (var (key, value) in fields ?? new Dictionary<string, string>()) form.Add(new StringContent(value), key);
        if (audio is not null)
        {
            var part = new ByteArrayContent(audio);
            part.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            form.Add(part, "audio", fileName);
        }
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{AdminRoot}/heritages/{heritageId}/narrations/{language}") { Content = form };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await Http.SendAsync(request);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(HttpTestExtensions.Json);
        return (response, envelope!);
    }

    /// <summary>Tải thuyết minh lên với băm lấy từ danh sách còn thiếu của ngôn ngữ đó.</summary>
    public async Task<HeritageNarrationDto> UploadAsync(string admin, long heritageId, string language, string textHash, byte[]? audio = null, string voice = "vieneu-mai-anh") =>
        await Expect(UploadRawAsync<HeritageNarrationDto>(admin, heritageId, language, Fields(textHash, voice), audio ?? FileSamples.Mp3()));

    public string FilePath(string url) =>
        Path.Combine(Factory.StorageRoot, url[(url.IndexOf("thuyet-minh/", StringComparison.Ordinal))..].Replace('/', Path.DirectorySeparatorChar));
}
