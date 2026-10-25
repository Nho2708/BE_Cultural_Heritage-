using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Rules;
using DSVHVN.Domain.Speech;
using DSVHVN.Infrastructure.Persistence;
using DSVHVN.Infrastructure.Speech;
using DSVHVN.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Bộ nhớ đệm giọng đọc trên SQL Server thật (database tạm của LocalDB, bảng do migration <c>ThemBoNhoDemGiongDoc</c> tạo): ràng buộc không trùng (băm, giọng) và CHECK,
/// nhiều yêu cầu cùng lúc chỉ gọi Dịch vụ AI một lần, và hai máy chủ cùng sinh một chữ chạm ràng buộc không trùng mà vẫn ra một dòng, một liên kết.
/// </summary>
public sealed class SpeechSqlServerTests : IDisposable
{
    private readonly string _database = LocalDb.NewTemporaryName();
    private readonly ServiceProvider? _root;
    private readonly FakeTtsAiHandler _ai = new();
    private readonly ConcurrentMemoryStorage _storage = new();

    public SpeechSqlServerTests()
    {
        if (!LocalDb.Available) return;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(LocalDb.ConnectionString(_database)));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        _root = services.BuildServiceProvider();
        using var scope = _root.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
    }

    public void Dispose()
    {
        _root?.Dispose();
        SqlConnection.ClearAllPools();
        if (LocalDb.Available) LocalDb.Drop(_database);
    }

    private SelfHostedTtsService NewService() => new(
        new HttpClient(_ai, disposeHandler: false), _root!.GetRequiredService<IServiceScopeFactory>(), _storage,
        Options.Create(new TtsOptions { WaitSeconds = 20, AiTimeoutSeconds = 20 }),
        Options.Create(new AiOptions { BaseUrl = "http://ai.test:8000", InternalKey = "khoa" }), NullLogger<SelfHostedTtsService>.Instance);

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = _root!.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [LocalDbFact]
    public async Task The_table_refuses_a_duplicate_hash_and_voice_and_non_positive_duration_or_size()
    {
        var hash = SpeechRules.Hash("Chào các em", SpeechRules.DefaultVoice);
        SpeechClip Clip(string voice = "vieneu-mai-anh", int duration = 1000, int size = 500) => new()
        {
            TextHash = hash, Voice = voice, StorageKey = StoredFileRules.SpeechClipKey("vieneu-mai-anh", hash), DurationMs = duration, ByteSize = size,
        };

        await DbAsync(async db =>
        {
            db.SpeechClips.Add(Clip());
            db.SpeechClips.Add(Clip(voice: "giong-khac"));
            return await db.SaveChangesAsync();
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => DbAsync(async db =>
        {
            db.SpeechClips.Add(Clip());
            return await db.SaveChangesAsync();
        }));
        await Assert.ThrowsAsync<DbUpdateException>(() => DbAsync(async db =>
        {
            db.SpeechClips.Add(Clip(voice: "giong-thu-ba", duration: 0));
            return await db.SaveChangesAsync();
        }));
        await Assert.ThrowsAsync<DbUpdateException>(() => DbAsync(async db =>
        {
            db.SpeechClips.Add(Clip(voice: "giong-thu-tu", size: 0));
            return await db.SaveChangesAsync();
        }));

        var rows = await DbAsync(db => db.SpeechClips.AsNoTracking().OrderBy(c => c.Voice).ToListAsync());
        Assert.Equal(["giong-khac", "vieneu-mai-anh"], rows.Select(r => r.Voice));
        Assert.All(rows, r => Assert.Equal(hash, r.TextHash));
        Assert.All(rows, r => Assert.NotNull(r.CreatedAt));
    }

    [LocalDbFact]
    public async Task Many_concurrent_requests_for_one_text_call_the_ai_service_once_and_leave_one_row()
    {
        var service = NewService();
        _ai.Delay = TimeSpan.FromMilliseconds(300);

        var urls = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => service.SynthesizeAsync("Cổng làng xưa có cây đa.")));

        Assert.Single(urls.Distinct());
        Assert.NotNull(urls[0]);
        Assert.Equal(1, _ai.CallsFor("Cổng làng xưa có cây đa."));
        Assert.Equal(1, await DbAsync(db => db.SpeechClips.CountAsync()));
    }

    [LocalDbFact]
    public async Task Two_servers_generating_the_same_text_collide_on_the_unique_index_and_still_agree_on_one_row_and_one_link()
    {
        var a = NewService();
        var b = NewService();
        _ai.Hold("Hai máy cùng sinh.");

        var first = a.SynthesizeAsync("Hai máy cùng sinh.");
        var second = b.SynthesizeAsync("Hai máy cùng sinh.");
        await Eventually.WaitAsync(() => _ai.CallsFor("Hai máy cùng sinh.") == 2, "cả hai máy cùng gọi Dịch vụ AI");
        _ai.Release("Hai máy cùng sinh.");
        var urls = await Task.WhenAll(first, second);

        Assert.NotNull(urls[0]);
        Assert.Equal(urls[0], urls[1]);
        Assert.Equal(1, await DbAsync(db => db.SpeechClips.CountAsync()));
    }

    [LocalDbFact]
    public async Task Prefetch_on_sql_server_caches_each_text_once()
    {
        var service = NewService();
        using var stop = new CancellationTokenSource();
        var worker = Task.Run(() => service.RunPrefetchAsync(stop.Token));

        Assert.Equal(3, service.Enqueue(["Một.", "Hai.", "Ba.", "Một."]));
        await Eventually.WaitAsync(async () => await DbAsync(db => db.SpeechClips.CountAsync()) == 3, "ba đoạn đã có đệm");
        await Eventually.WaitAsync(() => service.PendingPrefetch == 0, "hàng đợi trống");

        Assert.Equal(1, _ai.CallsFor("Một."));
        Assert.Equal(3, _ai.Calls.Count);
        await stop.CancelAsync();
        await worker;
    }
}
