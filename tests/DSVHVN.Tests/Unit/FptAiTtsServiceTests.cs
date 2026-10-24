using DSVHVN.Infrastructure.Speech;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Unit;

/// <summary>Dịch vụ chuyển văn bản thành giọng nói: chưa có khóa FPT.AI thì trả null và tuyệt đối không gọi ra ngoài.</summary>
public sealed class FptAiTtsServiceTests
{
    private sealed class FailingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Không được gọi ra ngoài khi chưa có khóa FPT.AI.");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Without_an_api_key_it_returns_null_and_never_calls_the_http_client(string apiKey)
    {
        var handler = new FailingHandler();
        var service = new FptAiTtsService(
            new HttpClient(handler),
            Options.Create(new FptAiOptions { ApiKey = apiKey }),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<FptAiTtsService>.Instance);

        var url = await service.SynthesizeAsync("Hoàng thành Thăng Long là di sản thế giới.");

        Assert.Null(url);
        Assert.Equal(0, handler.Calls);
    }
}
