using System.Net;
using System.Net.Http.Headers;

namespace DSVHVN.Tests.Integration;

/// <summary>Chuyển văn bản thành giọng nói qua HTTP: phải đăng nhập; máy chủ chưa có khóa FPT.AI thì trả liên kết null để client dùng giọng đọc của trình duyệt.</summary>
public sealed class TtsApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Url = "/api/v1/tts";

    private readonly PlatformClient _api = new(factory);
    private HttpClient Http => _api.Http;

    [Fact]
    public async Task Anonymous_request_returns_401()
    {
        var (res, _) = await Http.PostAsync<object>(Url, new { text = "Xin chào" });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task The_old_audio_download_route_no_longer_serves_anything()
    {
        var token = await _api.AdminTokenAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, Url + "/audio/abc");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await Http.SendAsync(request);

        Assert.True(res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"Mong 404, nhận {(int)res.StatusCode}");
    }

    [Fact]
    public async Task Signed_in_user_without_a_configured_key_gets_200_with_a_null_audio_url()
    {
        var token = await _api.AdminTokenAsync();

        var (res, body) = await Http.PostAsync<TtsData>(Url, new { text = "Xin chào" }, token);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(body.Success);
        Assert.Null(body.Data!.AudioUrl);
    }

    private sealed record TtsData(string? AudioUrl);
}
