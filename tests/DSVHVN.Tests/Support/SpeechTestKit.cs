using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DSVHVN.Application.Files;

namespace DSVHVN.Tests.Support;

/// <summary>
/// Dịch vụ AI giả của giọng đọc (<c>POST /v1/tts</c>): ghi lại từng lời gọi (chữ, giọng, khóa nội bộ), đếm số lời gọi chạy cùng lúc, giữ lời gọi của một đoạn chữ
/// cho tới khi kiểm thử cho đi (<see cref="Hold"/>), trễ cố định và ép phản hồi riêng (503, tệp hỏng, lỗi mạng) qua <see cref="Override"/>.
/// </summary>
public sealed class FakeTtsAiHandler : HttpMessageHandler
{
    public sealed record Call(string Text, string Voice, string? InternalKey, string Path);

    private readonly object _lock = new();
    private readonly List<Call> _calls = [];
    private readonly Dictionary<string, TaskCompletionSource> _holds = [];
    private int _inFlight;

    public int DurationMs { get; set; } = 2345;

    /// <summary>Trễ mỗi lời gọi trước khi trả lời.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>(chữ, lần gọi thứ mấy của chính chữ đó, từ 1) → phản hồi riêng, hoặc null để trả MP3 hợp lệ. Có thể ném <see cref="HttpRequestException"/>.</summary>
    public Func<string, int, HttpResponseMessage?>? Override { get; set; }

    public int MaxInFlight { get; private set; }

    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_lock) return _calls.ToList();
        }
    }

    public int CallsFor(string text)
    {
        lock (_lock) return _calls.Count(c => c.Text == text);
    }

    /// <summary>Giữ lời gọi của <paramref name="text"/> cho tới khi gọi <see cref="Release"/>.</summary>
    public void Hold(string text)
    {
        lock (_lock) _holds[text] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Release(string text)
    {
        TaskCompletionSource? hold;
        lock (_lock) _holds.Remove(text, out hold);
        hold?.TrySetResult();
    }

    public static HttpResponseMessage Unavailable(int retryAfterSeconds = 5)
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(body);
        var text = json.RootElement.GetProperty("text").GetString()!;
        var voice = json.RootElement.GetProperty("voice").GetString()!;
        request.Headers.TryGetValues("X-Internal-Key", out var keys);

        TaskCompletionSource? hold;
        int nth;
        lock (_lock)
        {
            _calls.Add(new Call(text, voice, keys?.FirstOrDefault(), request.RequestUri!.AbsolutePath));
            nth = _calls.Count(c => c.Text == text);
            _holds.TryGetValue(text, out hold);
            MaxInFlight = Math.Max(MaxInFlight, ++_inFlight);
        }

        try
        {
            if (hold is not null) await hold.Task.WaitAsync(cancellationToken);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            if (Override?.Invoke(text, nth) is { } custom) return custom;

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(FileSamples.Mp3()) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            response.Headers.TryAddWithoutValidation("X-Duration-Ms", DurationMs.ToString());
            response.Headers.TryAddWithoutValidation("X-Voice", voice);
            return response;
        }
        finally
        {
            lock (_lock) _inFlight--;
        }
    }
}

/// <summary>Kho tệp giả trong bộ nhớ an toàn khi nhiều luồng ghi cùng lúc (khác <c>MemoryFileStorage</c> dùng từ một luồng).</summary>
public sealed class ConcurrentMemoryStorage : IFileStorage
{
    public ConcurrentDictionary<string, byte[]> Files { get; } = new();

    public Task SaveAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        Files[key] = content.ToArray();
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Files.ContainsKey(key));

    public Task<long?> SizeOfAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<long?>(Files.TryGetValue(key, out var content) ? content.Length : null);

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        Files.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public string PublicUrl(string key) => "/files/" + key;
}

public static class Eventually
{
    /// <summary>Chờ tới 10 giây cho tới khi điều kiện đúng; không đúng thì hỏng test kèm <paramref name="what"/>.</summary>
    public static async Task WaitAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 1000 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Điều kiện không thành sau 10 giây: " + what);
    }

    public static async Task WaitAsync(Func<Task<bool>> condition, string what)
    {
        for (var i = 0; i < 1000; i++)
        {
            if (await condition()) return;
            await Task.Delay(10);
        }
        Assert.True(await condition(), "Điều kiện không thành sau 10 giây: " + what);
    }
}
