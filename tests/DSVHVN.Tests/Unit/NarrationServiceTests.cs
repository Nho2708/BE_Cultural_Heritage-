using System.Text.Json;
using DSVHVN.Application;
using DSVHVN.Application.Ads;
using DSVHVN.Application.Common;
using DSVHVN.Application.Files;
using DSVHVN.Application.Narrations;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Files;
using DSVHVN.Infrastructure.Persistence;
using DSVHVN.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Dịch vụ thuyết minh tạo sẵn trên SQLite trong bộ nhớ và kho tệp giả: danh sách còn thiếu theo đúng quy tắc tóm tắt công khai (lùi về tiếng Việt, cần dịch lại,
/// di sản nháp, băm lệch), tải lên (băm, tệp MP3, thay dòng cũ, chỉ quản trị hệ thống), gỡ tệp vừa lưu khi không ghi được dòng, và tóm tắt công khai chỉ kèm
/// thuyết minh khi ngôn ngữ và băm khớp.
/// </summary>
public sealed class NarrationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly MemoryFileStorage _storage = new();
    private readonly ServiceProvider _root;
    private readonly Actor _admin;

    private static readonly string ViText = "Hội Gióng là lễ hội truyền thống ở Phù Đổng. Lễ hội diễn ra vào tháng tư âm lịch.";
    private static readonly string EnText = "The Giong Festival is a traditional festival in Phu Dong.";

    public NarrationServiceTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddApplication();
        services.AddSingleton<IFileStorage>(_storage);
        services.AddSingleton<IImageProcessor, SkiaImageProcessor>();
        services.AddSingleton(Options.Create(new AdsOptions { GateEnabled = false }));
        _root = services.BuildServiceProvider();

        using var scope = _root.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        var user = new User
        {
            Username = "quantri", Email = "quantri@example.vn", PasswordHash = "x", FullName = "Quản trị", RoleId = Roles.AdminId, Status = UserStatus.ACTIVE,
        };
        db.Users.Add(user);
        db.SaveChanges();
        _admin = new Actor(user.Id, RoleCode.ADMIN, null, "127.0.0.1");
    }

    public void Dispose()
    {
        _root.Dispose();
        _connection.Dispose();
    }

    private async Task<T> RunAsync<T>(Func<NarrationService, Task<T>> action)
    {
        await using var scope = _root.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<NarrationService>());
    }

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = _root.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>Tóm tắt công khai như học sinh đã đăng nhập xem (không qua cổng quảng cáo).</summary>
    private async Task<PublicSummaryDto> PublicSummaryAsync(string slug, LanguageCode language)
    {
        await using var scope = _root.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PublicSummaryService>().GetAsync(slug, language, new PublicViewer(true, null, false), default);
    }

    /// <summary>Di sản kèm các bản tóm tắt theo (ngôn ngữ, trạng thái, cần dịch lại); chữ của bản ngắn là <see cref="ViText"/> hoặc <see cref="EnText"/> cộng hậu tố nếu có.</summary>
    private Task<long> SeedAsync(string slug, ContentStatus status, params (LanguageCode Language, TranslationStatus Status, bool Outdated)[] summaries) =>
        DbAsync(async db =>
        {
            var heritage = new Heritage { Name = "Hội Gióng " + slug, Slug = slug, Status = status };
            db.Heritages.Add(heritage);
            await db.SaveChangesAsync();
            foreach (var (language, state, outdated) in summaries)
                db.HeritageSummaries.Add(new HeritageSummary
                {
                    HeritageId = heritage.Id, LanguageCode = language, Status = state, IsOutdated = outdated,
                    ShortText = language == LanguageCode.vi ? ViText : $"[{language}] {EnText}", DetailedText = "Bản chi tiết.",
                });
            await db.SaveChangesAsync();
            return heritage.Id;
        });

    private static string Sentences(params (string Text, int Start, int End)[] items) =>
        JsonSerializer.Serialize(items.Select(i => new { text = i.Text, startMs = i.Start, endMs = i.End }));

    private static UploadNarrationRequest Request(string hash, string? voice = "vieneu-mai-anh", int? durationMs = 9000, string? sentences = null) =>
        new(hash, voice, durationMs, sentences ?? Sentences(("Hội Gióng là lễ hội truyền thống ở Phù Đổng.", 150, 4200), ("Lễ hội diễn ra vào tháng tư âm lịch.", 4550, 8900)));

    private static UploadedFile Mp3(bool withTag = true) => new("thuyet-minh.mp3", FileSamples.Mp3(withTag));

    private static string Hash(string text) => NarrationRules.TextHash(text);

    // ---- Danh sách còn thiếu ----

    [Fact]
    public async Task The_missing_list_follows_the_public_summary_rule_and_skips_narrations_whose_hash_still_matches()
    {
        var shown = await SeedAsync("hoi-giong", ContentStatus.PUBLISHED,
            (LanguageCode.vi, TranslationStatus.APPROVED, false), (LanguageCode.en, TranslationStatus.APPROVED, false), (LanguageCode.ko, TranslationStatus.MACHINE, false));
        var pendingEnglish = await SeedAsync("van-mieu", ContentStatus.PUBLISHED,
            (LanguageCode.vi, TranslationStatus.APPROVED, true), (LanguageCode.en, TranslationStatus.PENDING, false));   // en lùi về tiếng Việt; vi cần dịch lại vẫn hiện
        await SeedAsync("thanh-co", ContentStatus.DRAFT, (LanguageCode.vi, TranslationStatus.APPROVED, false));           // nháp: không hiện
        await SeedAsync("chua-mot-cot", ContentStatus.PUBLISHED, (LanguageCode.vi, TranslationStatus.PENDING, false), (LanguageCode.ko, TranslationStatus.MACHINE, false));   // chưa duyệt tiếng Việt: không hiện ngôn ngữ nào
        await SeedAsync("da-an", ContentStatus.ARCHIVED, (LanguageCode.vi, TranslationStatus.APPROVED, false));

        var vi = await RunAsync(s => s.ListMissingAsync("vi", null, default));
        Assert.Equal(2, vi.Total);
        Assert.Equal([shown, pendingEnglish], vi.Items.Select(i => i.HeritageId));
        Assert.Equal(("hoi-giong", "Hội Gióng hoi-giong", "vi", ViText, Hash(ViText)),
            (vi.Items[0].Slug, vi.Items[0].Name, vi.Items[0].Language, vi.Items[0].ShortText, vi.Items[0].TextHash));

        var en = await RunAsync(s => s.ListMissingAsync("en", null, default));
        Assert.Equal([shown], en.Items.Select(i => i.HeritageId));   // bản tiếng Anh chờ duyệt không tính (khách nghe bản tiếng Việt)

        var ko = await RunAsync(s => s.ListMissingAsync("ko", null, default));
        Assert.Equal([shown], ko.Items.Select(i => i.HeritageId));
        Assert.Equal("ko", ko.Items.Single().Language);
        Assert.Empty((await RunAsync(s => s.ListMissingAsync("zh-Hans", null, default))).Items);
        Assert.Empty((await RunAsync(s => s.ListMissingAsync("ja", null, default))).Items);

        // Có thuyết minh khớp băm thì khỏi danh sách; băm lệch (tóm tắt đã đổi) thì vẫn còn.
        await RunAsync(s => s.UploadAsync(_admin, shown, "vi", Request(Hash(ViText)), Mp3(), default));
        await DbAsync(async db =>
        {
            db.HeritageNarrations.Add(new HeritageNarration
            {
                HeritageId = pendingEnglish, LanguageCode = LanguageCode.vi, TextHash = Hash("chữ cũ"), AudioUrl = "/files/thuyet-minh/" + new string('a', 32) + ".mp3",
                DurationMs = 1000, Sentences = "[]", Voice = "vieneu-mai-anh",
            });
            return await db.SaveChangesAsync();
        });
        var again = await RunAsync(s => s.ListMissingAsync("vi", null, default));
        Assert.Equal([pendingEnglish], again.Items.Select(i => i.HeritageId));
        Assert.Equal(1, again.Total);
    }

    [Fact]
    public async Task The_limit_caps_the_items_but_the_total_counts_everything()
    {
        for (var i = 0; i < 3; i++)
            await SeedAsync($"di-san-{i}", ContentStatus.PUBLISHED, (LanguageCode.vi, TranslationStatus.APPROVED, false));

        var page = await RunAsync(s => s.ListMissingAsync("vi", 2, default));
        Assert.Equal((2, 3), (page.Items.Count, page.Total));

        foreach (var limit in new int?[] { 0, 1001 })
        {
            var ex = await Assert.ThrowsAsync<AppException>(() => RunAsync(s => s.ListMissingAsync("vi", limit, default)));
            Assert.Equal((400, "limit"), (ex.StatusCode, ex.Errors!.Keys.Single()));
        }
        var language = await Assert.ThrowsAsync<AppException>(() => RunAsync(s => s.ListMissingAsync("zh-TW", null, default)));
        Assert.Equal((400, "language"), (language.StatusCode, language.Errors!.Keys.Single()));
    }

    // ---- Tải lên ----

    [Fact]
    public async Task Uploading_stores_the_file_the_row_and_an_audit_entry_and_the_public_summary_returns_the_narration()
    {
        var id = await SeedAsync("hoi-giong", ContentStatus.PUBLISHED, (LanguageCode.vi, TranslationStatus.APPROVED, false));

        var saved = await RunAsync(s => s.UploadAsync(_admin, id, "vi", Request(Hash(ViText)), Mp3(), default));

        Assert.Equal((id, "vi", 9000, "vieneu-mai-anh", Hash(ViText), 2), (saved.HeritageId, saved.Language, saved.DurationMs, saved.Voice, saved.TextHash, saved.SentenceCount));
        var key = NarrationRules.StoredNarrationKey(saved.AudioUrl);
        Assert.NotNull(key);
        Assert.Equal(FileSamples.Mp3(), _storage.Files[key!]);
        var row = await DbAsync(db => db.HeritageNarrations.SingleAsync());
        Assert.Equal((LanguageCode.vi, "/files/" + key), (row.LanguageCode, row.AudioUrl));
        Assert.Contains("\"startMs\":150", row.Sentences);
        var audit = await DbAsync(db => db.AuditLogs.SingleAsync());
        Assert.Equal((AuditAction.HERITAGE_NARRATION_CHANGED, "heritage_narrations", row.Id, _admin.UserId), (audit.Action, audit.TargetType, audit.TargetId, audit.UserId));
        Assert.Contains("\"sentenceCount\":2", audit.NewValue);
        Assert.DoesNotContain("thuyet-minh/", audit.NewValue);   // không ghi đường dẫn tệp

        var summary = await PublicSummaryAsync("hoi-giong", LanguageCode.vi);
        Assert.NotNull(summary.Narration);
        Assert.Equal((saved.AudioUrl, 9000, "vieneu-mai-anh"), (summary.Narration!.AudioUrl, summary.Narration.DurationMs, summary.Narration.Voice));
        Assert.Equal([("Hội Gióng là lễ hội truyền thống ở Phù Đổng.", 150, 4200), ("Lễ hội diễn ra vào tháng tư âm lịch.", 4550, 8900)],
            summary.Narration.Sentences.Select(s => (s.Text, s.StartMs, s.EndMs)));

        // Tiếng Anh chưa có bản hiện hành: lùi về tiếng Việt và kèm thuyết minh tiếng Việt.
        var fallback = await PublicSummaryAsync("hoi-giong", LanguageCode.en);
        Assert.True(fallback.Localization.IsFallback);
        Assert.Equal(saved.AudioUrl, fallback.Narration?.AudioUrl);
    }

    [Fact]
    public async Task The_public_summary_has_no_narration_when_the_language_differs_or_the_text_changed_after_recording()
    {
        var id = await SeedAsync("hoi-giong", ContentStatus.PUBLISHED,
            (LanguageCode.vi, TranslationStatus.APPROVED, false), (LanguageCode.en, TranslationStatus.APPROVED, false));
        await RunAsync(s => s.UploadAsync(_admin, id, "vi", Request(Hash(ViText)), Mp3(), default));

        // Bản tiếng Anh đang hiện nhưng chưa có thuyết minh tiếng Anh: không mượn bản tiếng Việt.
        var english = await PublicSummaryAsync("hoi-giong", LanguageCode.en);
        Assert.False(english.Localization.IsFallback);
        Assert.Null(english.Narration);

        // Sửa chữ tóm tắt tiếng Việt (vẫn đã duyệt): băm lệch, thuyết minh không còn được trả, và quay lại danh sách còn thiếu với băm mới.
        await DbAsync(async db =>
        {
            (await db.HeritageSummaries.SingleAsync(s => s.LanguageCode == LanguageCode.vi)).ShortText = ViText + " Thêm một câu.";
            return await db.SaveChangesAsync();
        });
        Assert.Null((await PublicSummaryAsync("hoi-giong", LanguageCode.vi)).Narration);
        var missing = await RunAsync(s => s.ListMissingAsync("vi", null, default));
        Assert.Equal(Hash(ViText + " Thêm một câu."), missing.Items.Single().TextHash);

        // Tải lên với băm cũ thì 409 và không nhận tệp; băm mới thì thay dòng cũ, tệp cũ vẫn trong kho.
        var stale = await Assert.ThrowsAsync<AppException>(() => RunAsync(s => s.UploadAsync(_admin, id, "vi", Request(Hash(ViText)), Mp3(), default)));
        Assert.Equal((409, MessageCodes.NarrationStale, "textHash"), (stale.StatusCode, stale.AppMessage.Code, stale.Errors!.Keys.Single()));
        Assert.Single(_storage.Files);

        var replaced = await RunAsync(s => s.UploadAsync(_admin, id, "vi", Request(Hash(ViText + " Thêm một câu."), voice: "vieneu-mai-anh-v2"), Mp3(withTag: false), default));
        Assert.Equal(1, await DbAsync(db => db.HeritageNarrations.CountAsync()));
        Assert.Equal(2, _storage.Files.Count);
        Assert.Equal("vieneu-mai-anh-v2", (await PublicSummaryAsync("hoi-giong", LanguageCode.vi)).Narration?.Voice);
        Assert.Equal(replaced.AudioUrl, (await DbAsync(db => db.HeritageNarrations.SingleAsync())).AudioUrl);
        Assert.Equal(2, await DbAsync(db => db.AuditLogs.CountAsync()));
    }

    [Fact]
    public async Task Fields_and_the_file_are_checked_before_anything_is_stored()
    {
        var id = await SeedAsync("hoi-giong", ContentStatus.PUBLISHED, (LanguageCode.vi, TranslationStatus.APPROVED, false));
        var ok = Hash(ViText);

        var cases = new (UploadNarrationRequest Request, UploadedFile? File, string Field)[]
        {
            (Request("not-a-hash"), Mp3(), "textHash"),
            (Request(ok.ToUpperInvariant()), Mp3(), "textHash"),
            (Request(ok, voice: " "), Mp3(), "voice"),
            (Request(ok, voice: new string('v', 101)), Mp3(), "voice"),
            (Request(ok, durationMs: null), Mp3(), "durationMs"),
            (Request(ok, durationMs: 0), Mp3(), "durationMs"),
            (Request(ok, durationMs: 1_800_001), Mp3(), "durationMs"),
            (Request(ok, sentences: ""), Mp3(), "sentences"),
            (Request(ok, sentences: "{\"text\":\"x\"}"), Mp3(), "sentences"),
            (Request(ok, sentences: "[]"), Mp3(), "sentences"),
            (Request(ok, sentences: Sentences(("", 0, 100))), Mp3(), "sentences"),
            (Request(ok, sentences: Sentences(("x", 100, 100))), Mp3(), "sentences"),
            (Request(ok, sentences: Sentences(("x", 0, 10_001))), Mp3(), "sentences"),   // thời lượng 9000 + 1000 biên
            (Request(ok, sentences: Sentences(("a", 500, 900), ("b", 400, 1000))), Mp3(), "sentences"),
            (Request(ok), null, "audio"),
            (Request(ok), new UploadedFile("a.mp3", []), "audio"),
            (Request(ok), new UploadedFile("a.mp3", FileSamples.Png()), "audio"),
            (Request(ok), new UploadedFile("a.mp3", FileSamples.Mp3(frames: StoredFileRules.MaxNarrationBytes / 417 + 1)), "audio"),
        };
        foreach (var (request, file, field) in cases)
        {
            var ex = await Assert.ThrowsAsync<AppException>(() => RunAsync(s => s.UploadAsync(_admin, id, "vi", request, file, default)));
            Assert.Equal((400, field), (ex.StatusCode, ex.Errors!.Keys.Single()));
        }
        Assert.Empty(_storage.Files);
        Assert.Equal(0, await DbAsync(db => db.HeritageNarrations.CountAsync()));
        Assert.Equal(0, await DbAsync(db => db.AuditLogs.CountAsync()));
    }

    [Fact]
    public async Task Missing_heritage_or_summary_is_404_and_other_roles_are_403()
    {
        var id = await SeedAsync("hoi-giong", ContentStatus.PUBLISHED, (LanguageCode.vi, TranslationStatus.APPROVED, false));

        var noHeritage = await Assert.ThrowsAsync<AppException>(() => RunAsync(s => s.UploadAsync(_admin, id + 100, "vi", Request(Hash(ViText)), Mp3(), default)));
        var noSummary = await Assert.ThrowsAsync<AppException>(() => RunAsync(s => s.UploadAsync(_admin, id, "en", Request(Hash(EnText)), Mp3(), default)));
        var badLanguage = await Assert.ThrowsAsync<AppException>(() => RunAsync(s => s.UploadAsync(_admin, id, "xx", Request(Hash(ViText)), Mp3(), default)));
        var teacher = await Assert.ThrowsAsync<AppException>(() =>
            RunAsync(s => s.UploadAsync(new Actor(99, RoleCode.TEACHER, 1, null), id, "vi", Request(Hash(ViText)), Mp3(), default)));

        Assert.Equal((404, 404, 400, 403), (noHeritage.StatusCode, noSummary.StatusCode, badLanguage.StatusCode, teacher.StatusCode));
        Assert.Empty(_storage.Files);
    }

    [Fact]
    public async Task A_storage_failure_records_nothing_and_a_failed_row_removes_the_file_just_saved()
    {
        var id = await SeedAsync("hoi-giong", ContentStatus.PUBLISHED, (LanguageCode.vi, TranslationStatus.APPROVED, false));

        _storage.FailOnSave = 1;
        await Assert.ThrowsAsync<IOException>(() => RunAsync(s => s.UploadAsync(_admin, id, "vi", Request(Hash(ViText)), Mp3(), default)));
        Assert.Empty(_storage.Files);
        Assert.Equal(0, await DbAsync(db => db.HeritageNarrations.CountAsync()));

        // Chen một dòng cùng (di sản, ngôn ngữ) ngay sau khi tệp được lưu, như hai lần tải lên cùng lúc: lần này thua, gỡ tệp của mình và báo 409.
        _storage.FailOnSave = null;
        _storage.AfterSave = _ =>
        {
            using var scope = _root.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.HeritageNarrations.Add(new HeritageNarration
            {
                HeritageId = id, LanguageCode = LanguageCode.vi, TextHash = Hash(ViText), AudioUrl = "/files/thuyet-minh/" + new string('b', 32) + ".mp3",
                DurationMs = 100, Sentences = "[]", Voice = "khac",
            });
            db.SaveChanges();
        };
        var ex = await Assert.ThrowsAsync<AppException>(() => RunAsync(s => s.UploadAsync(_admin, id, "vi", Request(Hash(ViText)), Mp3(), default)));
        Assert.Equal(409, ex.StatusCode);
        Assert.Empty(_storage.Files);
        Assert.Equal("khac", (await DbAsync(db => db.HeritageNarrations.SingleAsync())).Voice);
        Assert.Equal(0, await DbAsync(db => db.AuditLogs.CountAsync()));
    }
}
