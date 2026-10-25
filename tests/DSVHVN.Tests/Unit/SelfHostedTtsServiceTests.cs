using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Persistence;
using DSVHVN.Infrastructure.Speech;
using DSVHVN.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Giọng đọc tự host trên SQLite dạng tệp (mỗi lượt truy cập một kết nối riêng như SQL Server), kho tệp giả và Dịch vụ AI giả:
/// bộ nhớ đệm (sai rồi trúng, chỉ một lời gọi), mỗi chữ một lần sinh dù nhiều yêu cầu cùng lúc, mỗi lúc một lời gọi sang Dịch vụ AI,
/// yêu cầu nghe ngay đi trước việc tạo trước, chờ có hạn, lỗi và 503 trả null, hàng đợi tạo trước (bỏ trùng, thử lại, có đệm thì bỏ qua),
/// hai máy chủ cùng sinh một chữ, và nhật ký không bao giờ chứa nội dung chữ.
/// </summary>
public sealed class SelfHostedTtsServiceTests : IDisposable
{
    private const string Secret = "cánh-diều-77";

    private readonly string _dbFile = Path.Combine(Path.GetTempPath(), "dsvhvn_giong_doc_" + Guid.NewGuid().ToString("N")[..12] + ".db");
    private readonly ServiceProvider _root;
    private readonly FakeTtsAiHandler _ai = new();
    private readonly ConcurrentMemoryStorage _storage = new();
    private readonly ListLogger<SelfHostedTtsService> _log = new();
    private readonly TtsOptions _options = new() { WaitSeconds = 10, AiTimeoutSeconds = 10, PrefetchRetrySeconds = 0.01, PrefetchMaxAttempts = 3 };
    private readonly SelfHostedTtsService _service;
    private readonly List<CancellationTokenSource> _workers = [];

    public SelfHostedTtsServiceTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbFile}"));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        _root = services.BuildServiceProvider();
        using (var scope = _root.CreateScope()) scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        _service = NewService(_log);
    }

    private SelfHostedTtsService NewService(ListLogger<SelfHostedTtsService> log) => new(
        new HttpClient(_ai, disposeHandler: false), _root.GetRequiredService<IServiceScopeFactory>(), _storage, Options.Create(_options),
        Options.Create(new AiOptions { BaseUrl = "http://ai.test:8000/", InternalKey = "khoa-noi-bo" }), log);

    public void Dispose()
    {
        foreach (var worker in _workers) worker.Cancel();
        _root.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbFile, _dbFile + "-wal", _dbFile + "-shm", _dbFile + "-journal" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Thư mục tạm của máy phát triển.
            }
        }
    }

    private static string Url(string normalizedText) =>
        "/files/" + StoredFileRules.SpeechClipKey(SpeechRules.DefaultVoice, SpeechRules.Hash(normalizedText, SpeechRules.DefaultVoice));

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = _root.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<int> RowsAsync() => DbAsync(db => db.SpeechClips.CountAsync());

    private Task<bool> IsCachedAsync(string normalizedText) =>
        DbAsync(db => db.SpeechClips.AnyAsync(c => c.TextHash == SpeechRules.Hash(normalizedText, SpeechRules.DefaultVoice)));

    private void StartWorker(SelfHostedTtsService? service = null)
    {
        var cts = new CancellationTokenSource();
        _workers.Add(cts);
        _ = Task.Run(() => (service ?? _service).RunPrefetchAsync(cts.Token));
    }

    // ---- Bộ nhớ đệm ----

    [Fact]
    public async Task A_miss_calls_the_ai_service_once_and_stores_the_clip_and_a_hit_returns_the_same_link_without_calling_again()
    {
        var first = await _service.SynthesizeAsync("  Chào   các em ");

        Assert.Equal(Url("Chào các em"), first);
        var call = Assert.Single(_ai.Calls);
        Assert.Equal(("Chào các em", SpeechRules.DefaultVoice, "khoa-noi-bo", "/v1/tts"), (call.Text, call.Voice, call.InternalKey, call.Path));

        var key = StoredFileRules.SpeechClipKey(SpeechRules.DefaultVoice, SpeechRules.Hash("Chào các em", SpeechRules.DefaultVoice));
        Assert.True(_storage.Files.TryGetValue(key, out var stored));
        Assert.True(NarrationRules.IsMp3(stored!));
        var row = await DbAsync(db => db.SpeechClips.AsNoTracking().SingleAsync());
        Assert.Equal((SpeechRules.Hash("Chào các em", SpeechRules.DefaultVoice), SpeechRules.DefaultVoice, key, 2345, stored!.Length),
            (row.TextHash, row.Voice, row.StorageKey, row.DurationMs, row.ByteSize));
        Assert.NotNull(row.CreatedAt);

        Assert.Equal(first, await _service.SynthesizeAsync("Chào các em"));
        Assert.Equal(first, await _service.SynthesizeAsync("\tChào\r\ncác em  "));
        Assert.Single(_ai.Calls);
        Assert.Equal(1, await RowsAsync());
    }

    [Fact]
    public async Task Another_voice_is_another_clip_for_the_same_text()
    {
        await _service.SynthesizeAsync("Chào các em");
        _options.Voice = "giong-khac";

        var other = await _service.SynthesizeAsync("Chào các em");

        Assert.Equal("/files/" + StoredFileRules.SpeechClipKey("giong-khac", SpeechRules.Hash("Chào các em", "giong-khac")), other);
        Assert.Equal(["vieneu-mai-anh", "giong-khac"], _ai.Calls.Select(c => c.Voice));
        Assert.Equal(2, await RowsAsync());
    }

    [Fact]
    public async Task A_voice_code_that_is_not_a_safe_folder_name_falls_back_to_the_default_voice()
    {
        _options.Voice = "../Mai Anh";

        var url = await _service.SynthesizeAsync("Chào các em");

        Assert.Equal(Url("Chào các em"), url);
        Assert.Equal(SpeechRules.DefaultVoice, Assert.Single(_ai.Calls).Voice);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    public async Task Empty_text_returns_null_without_calling_the_ai_service(string? text)
    {
        Assert.Null(await _service.SynthesizeAsync(text!));
        Assert.Empty(_ai.Calls);
    }

    [Fact]
    public async Task Text_longer_than_the_limit_returns_null_without_calling_the_ai_service()
    {
        Assert.Null(await _service.SynthesizeAsync(new string('a', SpeechRules.TextMaxLength + 1)));
        Assert.Empty(_ai.Calls);
        Assert.Equal(0, await RowsAsync());
    }

    // ---- Một lần sinh dùng chung, mỗi lúc một lời gọi ----

    [Fact]
    public async Task Concurrent_requests_for_the_same_text_share_one_generation()
    {
        _ai.Delay = TimeSpan.FromMilliseconds(200);

        var urls = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            _service.SynthesizeAsync(i % 2 == 0 ? "Hội Gióng là lễ hội lớn." : "  Hội Gióng   là lễ hội lớn.  ")));

        Assert.All(urls, u => Assert.Equal(Url("Hội Gióng là lễ hội lớn."), u));
        Assert.Single(_ai.Calls);
        Assert.Equal(1, await RowsAsync());
    }

    [Fact]
    public async Task Different_texts_are_sent_to_the_ai_service_one_at_a_time()
    {
        _ai.Delay = TimeSpan.FromMilliseconds(60);
        var texts = Enumerable.Range(1, 6).Select(i => $"Câu số {i}.").ToList();

        var urls = await Task.WhenAll(texts.Select(t => _service.SynthesizeAsync(t)));

        Assert.All(urls, Assert.NotNull);
        Assert.Equal(texts.Count, urls.Distinct().Count());
        Assert.Equal(texts.Count, _ai.Calls.Count);
        Assert.Equal(1, _ai.MaxInFlight);
        Assert.Equal(texts.Count, await RowsAsync());
    }

    [Fact]
    public async Task A_listener_request_goes_to_the_ai_service_before_background_work_that_is_waiting()
    {
        _ai.Hold("Đang sinh.");
        var running = _service.SynthesizeAsync("Đang sinh.");
        await Eventually.WaitAsync(() => _ai.CallsFor("Đang sinh.") == 1, "lời gọi đầu tới Dịch vụ AI");

        StartWorker();
        _service.Enqueue(["Việc nền."]);
        await Eventually.WaitAsync(() => _service.WaitingForAi == 1, "việc nền đã xếp hàng");
        var listener = _service.SynthesizeAsync("Em bấm nghe.");
        await Eventually.WaitAsync(() => _service.WaitingForAi == 2, "yêu cầu nghe đã xếp hàng");

        _ai.Release("Đang sinh.");
        await Task.WhenAll(running, listener);
        await Eventually.WaitAsync(() => IsCachedAsync("Việc nền."), "việc nền xong");

        Assert.Equal(["Đang sinh.", "Em bấm nghe.", "Việc nền."], _ai.Calls.Select(c => c.Text));
        Assert.Equal(1, _ai.MaxInFlight);
    }

    [Fact]
    public async Task A_listener_asking_for_the_text_a_background_job_is_waiting_on_moves_that_job_ahead()
    {
        _ai.Hold("Đang sinh.");
        var running = _service.SynthesizeAsync("Đang sinh.");
        await Eventually.WaitAsync(() => _ai.CallsFor("Đang sinh.") == 1, "lời gọi đầu tới Dịch vụ AI");

        StartWorker();
        _service.Enqueue(["Việc nền."]);
        await Eventually.WaitAsync(() => _service.WaitingForAi == 1, "việc nền đã xếp hàng");
        var other = _service.SynthesizeAsync("Người nghe khác.");
        await Eventually.WaitAsync(() => _service.WaitingForAi == 2, "người nghe khác đã xếp hàng");

        // Một em khác cần đúng đoạn chữ của việc nền: việc nền thành ưu tiên cao, vẫn dùng chung một lần sinh.
        var same = _service.SynthesizeAsync("Việc nền.");
        await Task.Delay(300);   // đủ để yêu cầu tra bộ nhớ đệm và nâng bậc việc nền trước khi cổng được nhả
        _ai.Release("Đang sinh.");
        await Task.WhenAll(running, other, same);

        Assert.Equal(["Đang sinh.", "Việc nền.", "Người nghe khác."], _ai.Calls.Select(c => c.Text));
        Assert.Equal(Url("Việc nền."), await same);
    }

    // ---- Chờ có hạn ----

    [Fact]
    public async Task A_listener_gives_up_after_the_wait_but_the_generation_finishes_and_is_cached_for_the_next_time()
    {
        _options.WaitSeconds = 0.15;
        _ai.Hold("Câu dài.");

        var started = DateTime.UtcNow;
        Assert.Null(await _service.SynthesizeAsync("Câu dài."));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal(0, await RowsAsync());

        _ai.Release("Câu dài.");
        await Eventually.WaitAsync(() => IsCachedAsync("Câu dài."), "lần sinh chạy tiếp và được nhớ đệm");

        _options.WaitSeconds = 5;
        Assert.Equal(Url("Câu dài."), await _service.SynthesizeAsync("Câu dài."));
        Assert.Equal(1, _ai.CallsFor("Câu dài."));
    }

    [Fact]
    public async Task A_cancelled_request_throws_but_does_not_stop_the_generation()
    {
        _ai.Hold("Bị hủy.");
        using var cancel = new CancellationTokenSource();
        var waiting = _service.SynthesizeAsync("Bị hủy.", cancel.Token);
        await Eventually.WaitAsync(() => _ai.CallsFor("Bị hủy.") == 1, "lời gọi tới Dịch vụ AI");

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        _ai.Release("Bị hủy.");
        await Eventually.WaitAsync(() => IsCachedAsync("Bị hủy."), "lần sinh vẫn được nhớ đệm");
    }

    // ---- Lỗi ----

    [Fact]
    public async Task A_503_returns_null_stores_nothing_and_the_next_request_tries_again()
    {
        _ai.Override = (text, nth) => nth == 1 ? FakeTtsAiHandler.Unavailable() : null;

        Assert.Null(await _service.SynthesizeAsync($"Mô hình đang nạp. {Secret}"));
        Assert.Equal(0, await RowsAsync());
        Assert.Empty(_storage.Files);

        Assert.NotNull(await _service.SynthesizeAsync($"Mô hình đang nạp. {Secret}"));
        Assert.Equal(2, _ai.Calls.Count);
        Assert.Equal(1, await RowsAsync());
    }

    [Theory]
    [InlineData("422")]
    [InlineData("network")]
    [InlineData("not-mp3")]
    [InlineData("empty")]
    [InlineData("timeout")]
    public async Task Every_failure_of_the_ai_service_returns_null_and_stores_nothing(string failure)
    {
        if (failure == "timeout") _options.AiTimeoutSeconds = 0.1;
        _ai.Override = (_, _) => failure switch
        {
            "422" => new HttpResponseMessage(System.Net.HttpStatusCode.UnprocessableEntity),
            "network" => throw new HttpRequestException("Không nối được."),
            "not-mp3" => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent("<html>lỗi</html>"u8.ToArray()) },
            "empty" => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([]) },
            _ => null,
        };
        if (failure == "timeout") _ai.Delay = TimeSpan.FromSeconds(5);

        Assert.Null(await _service.SynthesizeAsync($"Câu thử lỗi. {Secret}"));

        Assert.Equal(0, await RowsAsync());
        Assert.Empty(_storage.Files);
    }

    [Fact]
    public async Task Without_an_ai_address_or_key_nothing_is_called()
    {
        var service = new SelfHostedTtsService(new HttpClient(_ai, disposeHandler: false), _root.GetRequiredService<IServiceScopeFactory>(), _storage,
            Options.Create(_options), Options.Create(new AiOptions()), _log);

        Assert.Null(await service.SynthesizeAsync("Chào các em"));
        Assert.Empty(_ai.Calls);
    }

    [Fact]
    public async Task Logs_never_contain_the_text_even_when_the_ai_service_fails()
    {
        var text = $"Bé Lan kể: {Secret} là bí mật của lớp 3A.";
        _ai.Override = (_, nth) => nth switch
        {
            1 => FakeTtsAiHandler.Unavailable(),
            2 => throw new HttpRequestException("Không nối được Dịch vụ AI."),
            _ => null,
        };

        Assert.Null(await _service.SynthesizeAsync(text));
        Assert.Null(await _service.SynthesizeAsync(text));
        Assert.NotNull(await _service.SynthesizeAsync(text));
        _service.Enqueue([text + " Thêm một câu."]);
        StartWorker();
        await Eventually.WaitAsync(() => IsCachedAsync(text + " Thêm một câu."), "đoạn tạo trước xong");

        Assert.NotEmpty(_log.Messages);
        Assert.All(_log.Messages, m =>
        {
            Assert.DoesNotContain(Secret, m);
            Assert.DoesNotContain("Bé Lan", m);
            Assert.DoesNotContain("lớp 3A", m);
        });
        Assert.Contains(_log.Messages, m => m.Contains("503"));
    }

    // ---- Tạo trước ----

    [Fact]
    public async Task Enqueue_counts_only_new_usable_texts_and_the_worker_caches_them_and_skips_what_is_cached()
    {
        await _service.SynthesizeAsync("Đã có sẵn.");
        _ai.Hold("Mới một.");

        var queued = _service.Enqueue(["Mới một.", " Mới   một. ", "", "   ", null, new string('a', SpeechRules.TextMaxLength + 1), "Mới hai.", "Đã có sẵn."]);

        Assert.Equal(3, queued);
        Assert.Equal(3, _service.PendingPrefetch);
        Assert.Equal(0, _service.Enqueue(["Mới một."]));

        StartWorker();
        await Eventually.WaitAsync(() => _ai.CallsFor("Mới một.") == 1, "đoạn đầu tới Dịch vụ AI");
        _ai.Release("Mới một.");
        await Eventually.WaitAsync(() => IsCachedAsync("Mới hai."), "đoạn thứ hai xong");
        await Eventually.WaitAsync(() => _service.PendingPrefetch == 0, "hàng đợi trống");

        Assert.True(await IsCachedAsync("Mới một."));
        Assert.Equal(1, _ai.CallsFor("Đã có sẵn."));   // chỉ lần nghe ngay đầu tiên, tạo trước bỏ qua đoạn đã có đệm
        Assert.Equal(3, await RowsAsync());
        Assert.Equal(0, _service.Enqueue(Array.Empty<string>()));
    }

    [Fact]
    public async Task A_background_job_that_fails_is_retried_a_few_times_then_dropped()
    {
        _options.PrefetchMaxAttempts = 3;
        _ai.Override = (text, nth) => text == "Luôn lỗi." || nth == 1 ? FakeTtsAiHandler.Unavailable() : null;
        StartWorker();

        _service.Enqueue(["Lỗi một lần.", "Luôn lỗi."]);
        await Eventually.WaitAsync(() => IsCachedAsync("Lỗi một lần."), "đoạn lỗi một lần được thử lại và xong");
        await Eventually.WaitAsync(() => _service.PendingPrefetch == 0, "đoạn luôn lỗi bị bỏ sau số lần thử");

        Assert.Equal(2, _ai.CallsFor("Lỗi một lần."));
        Assert.Equal(3, _ai.CallsFor("Luôn lỗi."));
        Assert.False(await IsCachedAsync("Luôn lỗi."));

        // Bỏ rồi vẫn xếp lại được ở lần sau.
        Assert.Equal(1, _service.Enqueue(["Luôn lỗi."]));
    }

    [Fact]
    public async Task A_listener_waiting_on_a_text_that_is_being_prefetched_shares_that_generation()
    {
        _ai.Hold("Dùng chung.");
        StartWorker();
        _service.Enqueue(["Dùng chung."]);
        await Eventually.WaitAsync(() => _ai.CallsFor("Dùng chung.") == 1, "việc nền tới Dịch vụ AI");

        var listener = _service.SynthesizeAsync("Dùng chung.");
        _ai.Release("Dùng chung.");

        Assert.Equal(Url("Dùng chung."), await listener);
        Assert.Equal(1, _ai.CallsFor("Dùng chung."));
    }

    // ---- Hai máy chủ cùng sinh một chữ ----

    [Fact]
    public async Task Two_servers_generating_the_same_text_end_with_one_row_and_the_same_link()
    {
        var other = NewService(new ListLogger<SelfHostedTtsService>());
        _ai.Hold("Hai máy.");

        var a = _service.SynthesizeAsync("Hai máy.");
        var b = other.SynthesizeAsync("Hai máy.");
        await Eventually.WaitAsync(() => _ai.CallsFor("Hai máy.") == 2, "cả hai máy cùng gọi Dịch vụ AI");
        _ai.Release("Hai máy.");

        var urls = await Task.WhenAll(a, b);

        Assert.All(urls, u => Assert.Equal(Url("Hai máy."), u));
        Assert.Equal(1, await RowsAsync());
    }
}
