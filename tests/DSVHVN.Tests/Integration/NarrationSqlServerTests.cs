using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using DSVHVN.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static DSVHVN.Tests.Integration.DiscoveryTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Thuyết minh tạo sẵn trên SQL Server thật (database tạm của LocalDB, lược đồ do migration <c>ThemThuyetMinhTaoSan</c> tạo): danh sách còn thiếu, tải lên, thay dòng cũ,
/// đọc công khai kèm thuyết minh với chữ năm ngôn ngữ nguyên vẹn, và các ràng buộc của chính CSDL (một dòng mỗi di sản và ngôn ngữ, CHECK ngôn ngữ và thời lượng, khóa ngoại).
/// </summary>
public sealed class NarrationSqlServerTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>
{
    private readonly NarrationTestKit _k = new(factory);

    [LocalDbFact]
    public async Task List_upload_replace_and_read_work_end_to_end_on_sql_server()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.ApprovedHeritageAsync(admin);
        var id = seed.Heritage.Id;
        var slug = seed.Heritage.Slug!;

        var ko = await _k.MissingItemAsync(admin, "ko", id);
        Assert.NotNull(ko);
        var sentences = NarrationTestKit.Sentences(("경복궁은 조선 왕조의 법궁이다.", 150, 4200), ("두 번째 문장입니다.", 4550, 8900));
        var saved = await Expect(_k.UploadRawAsync<DSVHVN.Application.Narrations.HeritageNarrationDto>(admin, id, "ko",
            NarrationTestKit.Fields(ko!.TextHash, voice: "melo-kr", sentences: sentences), FileSamples.Mp3()));
        Assert.Equal(("ko", "melo-kr", 2), (saved.Language, saved.Voice, saved.SentenceCount));

        // Dòng lưu đúng: băm char(64), chữ Hàn trong JSON câu đọc lại nguyên vẹn; danh sách còn thiếu không còn mục này.
        var row = await factory.WithDbAsync(db => db.HeritageNarrations.AsNoTracking().SingleAsync(n => n.HeritageId == id));
        Assert.Equal((LanguageCode.ko, ko.TextHash, 9000), (row.LanguageCode, row.TextHash, row.DurationMs));
        Assert.Contains("경복궁은 조선 왕조의 법궁이다.", row.Sentences);
        Assert.Null(await _k.MissingItemAsync(admin, "ko", id));

        var summary = await _k.Translations.SummaryAsync(slug, "ko");
        Assert.True(summary.Localization.IsMachineTranslated);
        Assert.Equal("경복궁은 조선 왕조의 법궁이다.", summary.Narration?.Sentences[0].Text);
        Assert.Null((await _k.Translations.SummaryAsync(slug, "vi")).Narration);

        // Tải lên lần nữa thay dòng cũ, vẫn một dòng.
        var replaced = await _k.UploadAsync(admin, id, "ko", ko.TextHash, FileSamples.Mp3(withTag: false), voice: "melo-kr-v2");
        Assert.NotEqual(saved.AudioUrl, replaced.AudioUrl);
        Assert.Equal(1, await factory.WithDbAsync(db => db.HeritageNarrations.CountAsync(n => n.HeritageId == id)));
        Assert.Equal("melo-kr-v2", (await _k.Translations.SummaryAsync(slug, "ko")).Narration?.Voice);
        Assert.Equal(2, await factory.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == AuditAction.HERITAGE_NARRATION_CHANGED && l.TargetId == row.Id)));
    }

    [LocalDbFact]
    public async Task The_database_itself_keeps_one_narration_per_heritage_and_language_and_refuses_bad_rows()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.Translations.Discovery.DraftAsync(admin, new Spec("Chùa Một Cột " + Unique()));
        var now = DateTime.UtcNow;
        var hash = NarrationRules.TextHash("x");

        async Task Insert(long heritageId, string language = "vi", int durationMs = 1000) =>
            await factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO heritage_narrations (heritage_id, language_code, text_hash, audio_url, duration_ms, sentences, voice, created_at)
                VALUES ({heritageId}, {language}, {hash}, N'/files/thuyet-minh/x.mp3', {durationMs}, N'[]', N'vieneu-mai-anh', {now})"));
        async Task Refused(Func<Task> action, string constraint)
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(action);
            var inner = error as SqlException ?? error.InnerException as SqlException;
            Assert.NotNull(inner);
            Assert.Contains(constraint, inner!.Message);
        }

        await Insert(heritage.Id);
        await Insert(heritage.Id, language: "ja");
        await Refused(() => Insert(heritage.Id), "UX_heritage_narrations_heritage_language");
        await Refused(() => Insert(heritage.Id, language: "zh-Hans"), "CK_heritage_narrations_language_code");   // CSDL chỉ lưu mã ngắn
        await Refused(() => Insert(heritage.Id, language: "en", durationMs: 0), "CK_heritage_narrations_duration");
        await Refused(() => Insert(long.MaxValue), "fk_heritage_narrations_heritage_id");

        // Di sản chỉ có thuyết minh (không ảnh, không giai đoạn) không xóa cứng được vì còn thuyết minh trỏ tới (Restrict, như tóm tắt).
        var bare = "tran-quoc-" + Unique();
        await factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO heritages (name, slug, status, created_at) VALUES (N'Trấn Quốc', {bare}, 'DRAFT', {now})"));
        var bareId = await factory.WithDbAsync(db => db.Heritages.Where(h => h.Slug == bare).Select(h => h.Id).SingleAsync());
        await Insert(bareId);
        await Refused(() => factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM heritages WHERE id = {bareId}")),
            "fk_heritage_narrations_heritage_id");

        var hashType = await factory.WithDbAsync(db => db.Database.SqlQuery<string>($@"
            SELECT TYPE_NAME(c.user_type_id) + '(' + CAST(c.max_length AS nvarchar(10)) + ')' AS [Value]
            FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id WHERE t.name = 'heritage_narrations' AND c.name = 'text_hash'").SingleAsync());
        Assert.Equal("char(64)", hashType);
    }
}
