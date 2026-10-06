using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.Discovery;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static DSVHVN.Tests.Integration.DiscoveryTestKit;
using static DSVHVN.Tests.Integration.TranslationTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Đa ngôn ngữ trên SQL Server thật (database tạm của LocalDB, collation Vietnamese_CI_AI, lược đồ do migration tạo): dịch và tóm tắt bằng AI, duyệt, hàng chờ gộp hai bảng,
/// đọc công khai theo ngôn ngữ, tìm khớp bản dịch (kể cả ký tự đại diện của LIKE), đánh dấu cần dịch lại, xóa theo bản gốc và các ràng buộc CHECK, UNIQUE, Cascade của CSDL.
/// </summary>
public sealed class HeritageTranslationSqlServerTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>
{
    private readonly TranslationTestKit _k = new(factory);
    private HttpClient Http => _k.Http;

    [LocalDbFact]
    public async Task Translate_review_read_and_search_work_end_to_end_on_sql_server()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var other = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        var slug = seed.Heritage.Slug!;

        // Dịch và tóm tắt: trạng thái đúng từng ngôn ngữ.
        var overview = await _k.GenerateAsync(admin, id);
        Assert.Equal(TranslationStatus.PENDING, Cell(overview, TranslationKind.TRANSLATION, "en").Status);
        Assert.All(new[] { "ko", "zh-Hans", "ja" }, l => Assert.Equal(TranslationStatus.MACHINE, Cell(overview, TranslationKind.TRANSLATION, l).Status));
        Assert.Equal(TranslationStatus.PENDING, Cell(overview, TranslationKind.SUMMARY, "vi").Status);
        Assert.Equal(5, (await _k.GenerationsAsync(id)).Count(g => g.Status == AiTaskStatus.SUCCEEDED && g.LessonId == null));

        // Hàng chờ gộp bản dịch và tóm tắt trong CSDL, phân trang đúng.
        var queue = $"{AdminRoot}/translations?heritageId={id}";
        var pending = await Expect(Http.GetAsync<PagedResult<TranslationQueueItemDto>>(queue, admin));
        Assert.Equal(3, pending.TotalItems);
        Assert.Equal(new[] { (TranslationKind.SUMMARY, "en"), (TranslationKind.SUMMARY, "vi"), (TranslationKind.TRANSLATION, "en") }.Order(),
            pending.Items.Select(i => (i.Kind, i.Language)).Order());
        var machine = await Expect(Http.GetAsync<PagedResult<TranslationQueueItemDto>>($"{queue}&tab=MACHINE&pageSize=4&page=2", admin));
        Assert.Equal((2, 6, 2), (machine.Items.Count, machine.TotalItems, machine.TotalPages));
        var counts = await Expect(Http.GetAsync<TranslationCountsDto>($"{AdminRoot}/translations/counts", admin));
        Assert.True(counts.Pending >= 3 && counts.Machine >= 6);

        // Công khai trước khi duyệt: tiếng Anh lùi về tiếng Việt, bản máy hiện kèm cờ, chưa có tóm tắt.
        Assert.True((await _k.DetailAsync(slug, "en")).Localization!.IsFallback);
        var ko = await _k.DetailAsync(slug, "ko");
        Assert.Equal(($"[ko] {seed.Heritage.Name}", true), (ko.Name, ko.Localization!.IsMachineTranslated));
        await Fail(Http.GetAsync<object>($"{PublicRoot}/heritages/{slug}/summary?lang=ko"), HttpStatusCode.NotFound, MessageCodes.SummaryUnavailable);

        // Duyệt: tiếng Anh hiện, bản máy được dịch lại từ bản tiếng Anh, tóm tắt hiện sau khi tóm tắt tiếng Việt được duyệt.
        var result = await _k.ApproveAsync(admin, id, "en", "BOTH");
        Assert.True(result.MachineTranslationsRefreshed);
        await _k.ApproveAsync(admin, id, "vi", "SUMMARY");
        Assert.Equal($"[en] {seed.Heritage.Name}", (await _k.DetailAsync(slug, "en")).Name);
        Assert.Equal($"[ko] [en] {seed.Heritage.Name}", (await _k.DetailAsync(slug, "ko")).Name);
        var summary = await _k.SummaryAsync(slug, "ja");
        Assert.Equal(("ja", true), (summary.Localization.Language, summary.Localization.IsMachineTranslated));
        Assert.True((await _k.DetailAsync(slug)).HasSummary);

        // Tìm khớp bản tiếng Việt và bản dịch; ký tự đại diện của LIKE là chữ thường.
        var unique = "Citadel" + Unique();
        await factory.WithDbAsync(async db =>
        {
            await db.HeritageTranslations.Where(t => t.HeritageId == id && t.LanguageCode == LanguageCode.en)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Name, $"Imperial {unique} 100%_[x]"));
            return 0;
        });
        Assert.Equal([id], (await _k.SearchAsync(K(unique), "en")).Items.Select(i => i.Id));
        Assert.Equal([id], (await _k.SearchAsync(K($"imperial {unique.ToUpperInvariant()}"), "en")).Items.Select(i => i.Id));
        Assert.Equal([id], (await _k.SearchAsync(K("100%_[x]"), "en")).Items.Select(i => i.Id));
        Assert.Empty((await _k.SearchAsync(K("100%_[y]"), "en")).Items);
        Assert.Equal([id], (await _k.SearchAsync(K($"hoang thanh {seed.Tag}"), "en")).Items.Select(i => i.Id));       // tiếng Việt không dấu vẫn khớp
        Assert.Empty((await _k.SearchAsync(K(unique))).Items);
        Assert.Contains(other.Heritage.Id, (await _k.SearchAsync(K(other.Tag), "ko")).Items.Select(i => i.Id));       // chưa dịch: vẫn tìm bằng tiếng Việt
        Assert.True((await _k.SearchAsync(K(other.Tag), "ko")).Items.Single().IsFallback);

        // Sửa chữ tiếng Việt: mọi bản đánh dấu cần dịch lại, vẫn hiện bản cũ; lọc theo tình trạng bản dịch ở danh sách quản lý.
        await Expect(Http.PutAsync<TimelineDto>($"{AdminRoot}/timelines/{seed.Phase.Id}",
            new { name = "Tên mới", description = "Mô tả mới", startYear = 1010, endYear = 1225 }, admin));
        var outdated = await _k.OverviewAsync(admin, id);
        Assert.Equal(9, outdated.OutdatedCount);
        Assert.Contains(id, (await Expect(Http.GetAsync<PagedResult<HeritageSummaryDto>>($"{AdminRoot}/heritages?translationState=OUTDATED&pageSize=100", admin)))
            .Items.Where(i => i.TranslationState == TranslationState.OUTDATED).Select(i => i.Id));
        Assert.DoesNotContain(other.Heritage.Id, (await Expect(Http.GetAsync<PagedResult<HeritageSummaryDto>>(
            $"{AdminRoot}/heritages?translationState=OUTDATED&pageSize=100", admin))).Items.Select(i => i.Id));
        Assert.False((await _k.DetailAsync(slug, "en")).Localization!.IsFallback);

        // Dịch lại thay các dòng cũ tại chỗ (không vi phạm UNIQUE), rồi xóa giai đoạn xóa luôn bản dịch của nó (Cascade của CSDL).
        await _k.GenerateAsync(admin, id);
        Assert.Equal(0, (await _k.OverviewAsync(admin, id)).OutdatedCount);
        Assert.Equal(4, await factory.WithDbAsync(db => db.TimelineTranslations.CountAsync(t => t.TimelineId == seed.Phase.Id)));
        Assert.Equal(4, await factory.WithDbAsync(db => db.HeritageTranslations.CountAsync(t => t.HeritageId == id)));
        await Expect(Http.DeleteAsync<object>($"{AdminRoot}/media/{seed.EventMediaId}", admin));
        Assert.Equal(0, await factory.WithDbAsync(db => db.MediaTranslations.CountAsync(t => t.MediaId == seed.EventMediaId)));
        await Expect(Http.DeleteAsync<object>($"{AdminRoot}/timelines/{seed.Phase.Id}", admin));
        Assert.Equal(0, await factory.WithDbAsync(db => db.TimelineTranslations.CountAsync(t => t.TimelineId == seed.Phase.Id)));
        Assert.Equal(0, await factory.WithDbAsync(db => db.EventTranslations.CountAsync(t => t.EventId == seed.Event.Id)));

        // Duyệt, từ chối đều có nhật ký thao tác.
        await _k.RejectAsync(admin, id, "en", "TRANSLATION", "Sai tên riêng");
        Assert.Equal(1, await factory.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == AuditAction.TRANSLATION_REJECTED && l.NewValue!.Contains("Sai tên riêng"))));
        Assert.True(await factory.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == AuditAction.TRANSLATION_APPROVED)) >= 3);
    }

    [LocalDbFact]
    public async Task The_database_itself_refuses_rows_that_break_the_language_and_status_rules()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        await _k.GenerateAsync(admin, id);

        async Task<int> Sql(FormattableString sql) => await factory.WithDbAsync(async db => await db.Database.ExecuteSqlInterpolatedAsync(sql));
        async Task Refused(FormattableString sql, string constraint)
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => Sql(sql));
            var inner = error as SqlException ?? error.InnerException as SqlException;
            Assert.NotNull(inner);
            Assert.Contains(constraint, inner!.Message);
        }
        var now = DateTime.UtcNow;

        // Không có dòng bản dịch cho tiếng Việt; tiếng Anh chỉ chờ duyệt, đã duyệt, bị từ chối; Hàn, Trung, Nhật luôn là bản dịch máy.
        await Refused($"INSERT INTO heritage_translations (heritage_id, language_code, name, status, is_outdated, created_at) VALUES ({seed.Heritage.Id + 100000}, 'vi', N'x', 'PENDING', 0, {now})", "CK_heritage_translations");
        await Refused($@"DELETE FROM heritage_translations WHERE heritage_id = {id} AND language_code = 'ko';
                         INSERT INTO heritage_translations (heritage_id, language_code, name, status, is_outdated, created_at) VALUES ({id}, 'ko', N'x', 'APPROVED', 0, {now})",
            "CK_heritage_translations_status");
        await Refused($"UPDATE heritage_translations SET status = 'MACHINE' WHERE heritage_id = {id} AND language_code = 'en'", "CK_heritage_translations_status");
        await Refused($"UPDATE heritage_summaries SET status = 'APPROVED' WHERE heritage_id = {id} AND language_code = 'ja'", "CK_heritage_summaries_status");
        await Refused($"UPDATE heritage_summaries SET status = 'MACHINE' WHERE heritage_id = {id} AND language_code = 'vi'", "CK_heritage_summaries_status");
        await Refused($"UPDATE heritage_translations SET language_code = 'fr' WHERE heritage_id = {id} AND language_code = 'ja'", "CK_heritage_translations_language_code");
        await Refused($"INSERT INTO timeline_translations (timeline_id, language_code, name, created_at) VALUES ({seed.Phase.Id}, 'vi', N'x', {now})", "CK_timeline_translations_language");
        await Refused($"INSERT INTO event_translations (event_id, language_code, name, created_at) VALUES ({seed.Event.Id}, 'vi', N'x', {now})", "CK_event_translations_language");
        await Refused($"INSERT INTO media_translations (media_id, language_code, caption, created_at) VALUES ({seed.EventMediaId}, 'vi', N'x', {now})", "CK_media_translations_language");

        // Mỗi (bản gốc, ngôn ngữ) đúng một dòng.
        await Refused($"INSERT INTO heritage_translations (heritage_id, language_code, name, status, is_outdated, created_at) VALUES ({id}, 'en', N'x', 'PENDING', 0, {now})", "UX_heritage_translations_heritage_language");
        await Refused($"INSERT INTO heritage_summaries (heritage_id, language_code, short_text, detailed_text, status, is_outdated, created_at) VALUES ({id}, 'vi', N'a', N'b', 'PENDING', 0, {now})", "UX_heritage_summaries_heritage_language");
        await Refused($"INSERT INTO timeline_translations (timeline_id, language_code, name, created_at) VALUES ({seed.Phase.Id}, 'en', N'x', {now})", "UX_timeline_translations_timeline_language");
        await Refused($"INSERT INTO event_translations (event_id, language_code, name, created_at) VALUES ({seed.Event.Id}, 'en', N'x', {now})", "UX_event_translations_event_language");
        await Refused($"INSERT INTO media_translations (media_id, language_code, caption, created_at) VALUES ({seed.EventMediaId}, 'en', N'x', {now})", "UX_media_translations_media_language");

        // Khóa ngoại: không trỏ vào di sản không có; không xóa cứng di sản đã có bản dịch (Restrict), người duyệt và lần gọi AI cũng vậy.
        await Refused($"INSERT INTO heritage_summaries (heritage_id, language_code, short_text, detailed_text, status, is_outdated, created_at) VALUES (987654321, 'en', N'a', N'b', 'PENDING', 0, {now})", "fk_heritage_summaries_heritage_id");
        await Refused($"DELETE FROM heritages WHERE id = {id}", "REFERENCE constraint");
        await Refused($"DELETE FROM ai_generations WHERE heritage_id = {id}", "REFERENCE constraint");
        await Refused($"DELETE FROM users WHERE username = {ApiFactory.AdminUsername}", "REFERENCE constraint");

        // Cột bit mặc định 0; giá trị hợp lệ được nhận.
        Assert.Equal(1, await Sql($"UPDATE heritage_translations SET status = 'REJECTED' WHERE heritage_id = {id} AND language_code = 'en'"));
        Assert.Equal(5, await factory.WithDbAsync(db => db.HeritageSummaries.CountAsync(s => s.HeritageId == id && !s.IsOutdated)));
    }
}
