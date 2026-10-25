using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading.Channels;
using DSVHVN.Application.Common;
using DSVHVN.Application.Files;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Speech;
using DSVHVN.Domain.Rules;
using DSVHVN.Domain.Speech;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Speech;

/// <summary>
/// Giọng đọc tự host (<c>Tts:Provider = SelfHosted</c>): nghe ngay và tạo trước.
/// <list type="bullet">
/// <item><b>Bộ nhớ đệm bền.</b> Chữ được chuẩn hóa khoảng trắng (1..1000 ký tự) rồi băm cùng giọng (<see cref="SpeechRules.Hash"/>). Bảng <c>speech_clips</c> có dòng thì trả ngay
/// đường dẫn công khai của tệp MP3; chưa có thì gọi <c>POST {Ai:BaseUrl}/v1/tts</c> bằng khóa nội bộ, lưu MP3 vào kho tệp (<c>giong-doc/{giọng}/{băm}.mp3</c>) và thêm dòng.</item>
/// <item><b>Mỗi chữ chỉ sinh một lần.</b> Các yêu cầu cùng băm đồng thời dùng chung một lần sinh (single-flight). Dịch vụ AI nhận <b>đúng một yêu cầu cùng lúc</b> (<see cref="PriorityGate"/>);
/// yêu cầu nghe ngay luôn được chọn trước việc tạo trước chạy nền, và nếu việc tạo trước của đúng đoạn chữ đó đang chờ thì được nâng bậc.</item>
/// <item><b>Chờ có hạn.</b> Yêu cầu nghe ngay chờ tối đa <see cref="TtsOptions.WaitSeconds"/> giây (mặc định 30) rồi trả null; lần sinh vẫn chạy tiếp và được nhớ đệm cho lần sau.
/// Lỗi, 503 (mô hình chưa nạp xong) hay Dịch vụ AI không nối được cũng trả null để giao diện lùi về giọng của thiết bị.</item>
/// <item><b>Hàng đợi tạo trước.</b> <see cref="Enqueue"/> xếp chữ vào kênh có giới hạn, bỏ trùng; <see cref="RunPrefetchAsync"/> (do tác vụ nền gọi) lấy từng đoạn, bỏ đoạn đã có đệm và sinh lần lượt ở bậc thấp.</item>
/// </list>
/// Nhật ký không bao giờ ghi nội dung chữ (có thể là nội dung bài học), chỉ ghi mã trả về, loại lỗi và vài ký tự đầu của băm.
/// </summary>
public sealed class SelfHostedTtsService : ITtsService, ISpeechPrefetcher
{
    public const string Path = "v1/tts";
    public const string KeyHeader = "X-Internal-Key";
    public const string CorrelationHeader = "X-Correlation-Id";
    public const string DurationHeader = "X-Duration-Ms";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private sealed record PrefetchItem(string Text, string Hash, string Voice, int Attempt);

    private sealed record GeneratedAudio(byte[] Content, int DurationMs);

    /// <summary>Một lần sinh đang chạy cho một băm; mọi yêu cầu cùng băm chờ chung <see cref="Result"/>.</summary>
    private sealed class Flight(bool high)
    {
        public PriorityGate.Request Request { get; } = new(high);

        public TaskCompletionSource<string?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly HttpClient _http;
    private readonly IServiceScopeFactory _scopes;
    private readonly IFileStorage _storage;
    private readonly IOptions<TtsOptions> _tts;
    private readonly IOptions<AiOptions> _ai;
    private readonly ILogger<SelfHostedTtsService> _logger;
    private readonly CancellationToken _shutdown;

    private readonly ConcurrentDictionary<string, Flight> _flights = new();
    private readonly ConcurrentDictionary<string, byte> _pending = new();
    private readonly Channel<PrefetchItem> _queue = Channel.CreateBounded<PrefetchItem>(
        new BoundedChannelOptions(SpeechRules.PrefetchQueueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly PriorityGate _gate = new();

    public SelfHostedTtsService(
        HttpClient http,
        IServiceScopeFactory scopes,
        IFileStorage storage,
        IOptions<TtsOptions> tts,
        IOptions<AiOptions> ai,
        ILogger<SelfHostedTtsService> logger,
        CancellationToken shutdown = default)
    {
        _http = http;
        _scopes = scopes;
        _storage = storage;
        _tts = tts;
        _ai = ai;
        _logger = logger;
        _shutdown = shutdown;
    }

    public bool Enabled => true;

    /// <summary>Số đoạn đang chờ trong hàng đợi tạo trước (để kiểm thử).</summary>
    public int PendingPrefetch => _pending.Count;

    /// <summary>Số lần sinh đang xếp hàng chờ tới lượt gọi Dịch vụ AI, không tính lần đang chạy (để kiểm thử).</summary>
    public int WaitingForAi => _gate.Waiting;

    private string Voice => SpeechRules.IsValidVoice(_tts.Value.Voice) ? _tts.Value.Voice : SpeechRules.DefaultVoice;

    // ---- Nghe ngay ----

    public async Task<string?> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        var normalized = SpeechRules.Normalize(text);
        if (normalized is null) return null;
        var voice = Voice;
        var hash = SpeechRules.Hash(normalized, voice);

        try
        {
            if (await LookupAsync(hash, voice, ct) is { } cached) return cached;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Không tra được bộ nhớ đệm giọng đọc; giao diện sẽ dùng giọng của thiết bị.");
            return null;
        }

        var flight = JoinOrStart(hash, voice, normalized, high: true);
        try
        {
            return await flight.Result.Task.WaitAsync(_tts.Value.Wait, ct);
        }
        catch (TimeoutException)
        {
            _logger.LogInformation("Giọng đọc {Hash} chưa sinh xong sau {Seconds} giây; trả liên kết null, lần sinh vẫn chạy tiếp.",
                Short(hash), _tts.Value.Wait.TotalSeconds);
            return null;
        }
    }

    // ---- Tạo trước ----

    public int Enqueue(IEnumerable<string?> texts)
    {
        var voice = Voice;
        var queued = 0;
        foreach (var text in texts)
        {
            var normalized = SpeechRules.Normalize(text);
            if (normalized is null) continue;
            var hash = SpeechRules.Hash(normalized, voice);
            if (!_pending.TryAdd(hash, 0)) continue;
            if (!_queue.Writer.TryWrite(new PrefetchItem(normalized, hash, voice, 1)))
            {
                _pending.TryRemove(hash, out _);
                _logger.LogInformation("Hàng đợi tạo giọng đọc đã đầy; bỏ bớt đoạn mới.");
                break;
            }
            queued++;
        }
        return queued;
    }

    /// <summary>Vòng lặp của tác vụ nền: lấy từng đoạn trong hàng đợi và sinh ở bậc thấp cho tới khi <paramref name="stoppingToken"/> báo dừng.</summary>
    public async Task RunPrefetchAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                var failed = false;
                try
                {
                    failed = !await PrefetchOneAsync(item, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Tạo trước giọng đọc {Hash} gặp lỗi.", Short(item.Hash));
                    failed = true;
                }

                // Đoạn lỗi được xếp lại (giữ nguyên chỗ trong danh sách chờ để không bị xếp trùng) cho tới hết số lần thử.
                var requeued = failed
                    && item.Attempt < Math.Max(1, _tts.Value.PrefetchMaxAttempts)
                    && _queue.Writer.TryWrite(item with { Attempt = item.Attempt + 1 });
                if (!requeued) _pending.TryRemove(item.Hash, out _);

                if (failed) await Task.Delay(_tts.Value.PrefetchRetry, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Dừng máy chủ.
        }
    }

    /// <summary>True khi đoạn đã có đệm (sẵn từ trước hoặc vừa sinh xong); false khi lần sinh lỗi và nên thử lại.</summary>
    private async Task<bool> PrefetchOneAsync(PrefetchItem item, CancellationToken stoppingToken)
    {
        if (await LookupAsync(item.Hash, item.Voice, stoppingToken) is not null) return true;
        var flight = JoinOrStart(item.Hash, item.Voice, item.Text, high: false);
        return await flight.Result.Task.WaitAsync(stoppingToken) is not null;
    }

    // ---- Một lần sinh dùng chung ----

    private Flight JoinOrStart(string hash, string voice, string text, bool high)
    {
        while (true)
        {
            if (_flights.TryGetValue(hash, out var running))
            {
                if (high) running.Request.Promote();
                return running;
            }

            var flight = new Flight(high);
            if (!_flights.TryAdd(hash, flight)) continue;
            // Chạy tách khỏi yêu cầu đã khởi động nó: người nghe hết kiên nhẫn thì lần sinh vẫn xong và được nhớ đệm.
            _ = Task.Run(() => RunFlightAsync(flight, hash, voice, text), CancellationToken.None);
            return flight;
        }
    }

    private async Task RunFlightAsync(Flight flight, string hash, string voice, string text)
    {
        string? url = null;
        try
        {
            GeneratedAudio? audio;
            using (await _gate.AcquireAsync(flight.Request, _shutdown))
            {
                // Có thể một lần sinh khác (hoặc máy chủ khác) đã lưu xong lúc ta xếp hàng.
                if (await LookupAsync(hash, voice, _shutdown) is { } existing)
                {
                    url = existing;
                    return;
                }
                audio = await CallAiAsync(hash, voice, text);
            }

            if (audio is not null) url = await StoreAsync(hash, voice, audio);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Dừng máy chủ giữa chừng.
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Giọng đọc {Hash} chưa sinh được ({ErrorType}).", Short(hash), ex.GetType().Name);
        }
        finally
        {
            // Dòng bộ nhớ đệm đã ghi trước khi gỡ lần sinh, nên yêu cầu đến sau luôn thấy kết quả khi tra lại.
            _flights.TryRemove(hash, out _);
            flight.Result.TrySetResult(url);
        }
    }

    private async Task<GeneratedAudio?> CallAiAsync(string hash, string voice, string text)
    {
        var settings = _ai.Value;
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out _) || string.IsNullOrWhiteSpace(settings.InternalKey))
        {
            _logger.LogInformation("Giọng đọc tự host đang bật nhưng thiếu Ai:BaseUrl hoặc Ai:InternalKey; không sinh được.");
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
        timeout.CancelAfter(_tts.Value.AiTimeout);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, settings.BaseUrl.TrimEnd('/') + "/" + Path)
            {
                Content = JsonContent.Create(new { text, voice }, options: Json),
            };
            message.Headers.TryAddWithoutValidation(KeyHeader, settings.InternalKey);
            message.Headers.TryAddWithoutValidation(CorrelationHeader, Guid.NewGuid().ToString("N"));

            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Dịch vụ AI trả mã {Status} khi sinh giọng đọc {Hash}.", (int)response.StatusCode, Short(hash));
                return null;
            }

            var content = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            if (content.Length == 0 || content.Length > StoredFileRules.MaxNarrationBytes || !NarrationRules.IsMp3(content))
            {
                _logger.LogInformation("Dịch vụ AI trả tệp âm thanh không dùng được khi sinh giọng đọc {Hash} ({Bytes} byte).", Short(hash), content.Length);
                return null;
            }
            return new GeneratedAudio(content, DurationOf(response, content.Length));
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
        {
            _logger.LogInformation("Dịch vụ AI không trả lời kịp {Seconds} giây khi sinh giọng đọc {Hash}.", _tts.Value.AiTimeout.TotalSeconds, Short(hash));
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogInformation(ex, "Không nối được Dịch vụ AI khi sinh giọng đọc {Hash}.", Short(hash));
            return null;
        }
    }

    /// <summary>Thời lượng do dịch vụ AI báo ở tiêu đề <c>X-Duration-Ms</c>; thiếu hoặc sai thì ước lượng theo 64 kbps để bảo đảm cột luôn dương.</summary>
    private static int DurationOf(HttpResponseMessage response, int byteCount)
    {
        if (response.Headers.TryGetValues(DurationHeader, out var values)
            && int.TryParse(values.FirstOrDefault(), out var reported) && reported > 0)
            return reported;
        return Math.Max(1, (int)(byteCount * 8L / 64));
    }

    // ---- Kho tệp và CSDL ----

    private async Task<string?> LookupAsync(string hash, string voice, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var key = await db.SpeechClips.AsNoTracking()
            .Where(c => c.TextHash == hash && c.Voice == voice)
            .Select(c => c.StorageKey)
            .FirstOrDefaultAsync(ct);
        return key is null ? null : _storage.PublicUrl(key);
    }

    private async Task<string?> StoreAsync(string hash, string voice, GeneratedAudio audio)
    {
        var key = StoredFileRules.SpeechClipKey(voice, hash);
        await _storage.SaveAsync(key, audio.Content, _shutdown);

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        db.SpeechClips.Add(new SpeechClip
        {
            TextHash = hash, Voice = voice, StorageKey = key, DurationMs = audio.DurationMs, ByteSize = audio.Content.Length,
        });
        try
        {
            await db.SaveChangesAsync(_shutdown);
        }
        catch (DbUpdateException)
        {
            // Hai lần sinh của cùng một băm chạm nhau ở ràng buộc không trùng (vd hai máy chủ): dòng kia đã có, dùng lại nó.
            if (await LookupAsync(hash, voice, _shutdown) is { } existing) return existing;
            throw;
        }
        return _storage.PublicUrl(key);
    }

    private static string Short(string hash) => hash[..8];
}

/// <summary><c>Tts:Provider = None</c>: máy chủ không cung cấp giọng, luôn trả liên kết null để giao diện dùng giọng của thiết bị.</summary>
public sealed class NoTtsService : ITtsService
{
    public Task<string?> SynthesizeAsync(string text, CancellationToken ct = default) => Task.FromResult<string?>(null);
}
