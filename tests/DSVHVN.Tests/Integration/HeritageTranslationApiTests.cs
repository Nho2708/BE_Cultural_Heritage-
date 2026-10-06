using System.Net;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Heritages;
using DSVHVN.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;
using static DSVHVN.Tests.Integration.DiscoveryTestKit;
using static DSVHVN.Tests.Integration.TranslationTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Dịch và tóm tắt di sản bằng AI, duyệt bản dịch (quản trị hệ thống) trên Api thật với dịch vụ AI giả lập: trạng thái từng ô, nhật ký <c>ai_generations</c>,
/// không tính hạn mức lượt AI của trường, duyệt, từ chối, sửa tại chỗ, dịch lại thay bản cũ, lỗi dịch vụ AI không làm mất bản cũ, đánh dấu cần dịch lại.
/// </summary>
public sealed class HeritageTranslationApiTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly TranslationTestKit _k = new(factory);
    private HttpClient Http => _k.Http;

    public Task InitializeAsync()
    {
        factory.HeritageAi.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        factory.HeritageAi.Reset();
        return Task.CompletedTask;
    }

    private static TranslationCellDto Translation(TranslationOverviewDto o, string language) => Cell(o, TranslationKind.TRANSLATION, language);

    private static TranslationCellDto Summary(TranslationOverviewDto o, string language) => Cell(o, TranslationKind.SUMMARY, language);

    // ---- Quyền ----

    [Fact]
    public async Task Only_system_administrators_can_translate_and_review()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var (_, orgAdminToken) = await _k.Discovery.Api.OrgWithAdminAsync();
        var (_, teacherToken) = await _k.Discovery.Api.TeacherAsync(orgAdminToken);
        var id = seed.Heritage.Id;

        var calls = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Get, $"{AdminRoot}/translations", null),
            (HttpMethod.Get, $"{AdminRoot}/translations/counts", null),
            (HttpMethod.Get, $"{AdminRoot}/heritages/{id}/translations", null),
            (HttpMethod.Post, $"{AdminRoot}/heritages/{id}/translations/generate", new { }),
            (HttpMethod.Get, $"{AdminRoot}/heritages/{id}/translations/en/translation", null),
            (HttpMethod.Put, $"{AdminRoot}/heritages/{id}/translations/en/translation", new { name = "x" }),
            (HttpMethod.Get, $"{AdminRoot}/heritages/{id}/translations/en/summary", null),
            (HttpMethod.Put, $"{AdminRoot}/heritages/{id}/translations/en/summary", new { shortText = "x" }),
            (HttpMethod.Post, $"{AdminRoot}/heritages/{id}/translations/en/approve", new { kind = "BOTH" }),
            (HttpMethod.Post, $"{AdminRoot}/heritages/{id}/translations/en/reject", new { kind = "BOTH" }),
        };
        foreach (var (method, url, body) in calls)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Http.SendJsonAsync<object>(method, url, body)).Response.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Http.SendJsonAsync<object>(method, url, body, teacherToken)).Response.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Http.SendJsonAsync<object>(method, url, body, orgAdminToken)).Response.StatusCode);
        }
        Assert.Equal(0, factory.HeritageAi.SummarizeCalls);
    }

    // ---- Dịch và tóm tắt ----

    [Fact]
    public async Task Generating_translates_four_languages_and_summarizes_in_five_with_the_status_each_language_requires()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;

        var before = await _k.OverviewAsync(admin, id);
        Assert.True(before.CanGenerate);
        Assert.All(before.Translations.Concat(before.Summaries), c =>
        {
            Assert.Null(c.Status);
            Assert.Equal("Chưa có", c.Label);
        });
        Assert.Equal(["en", "ko", "zh-Hans", "ja"], before.Translations.Select(c => c.Language));
        Assert.Equal(["vi", "en", "ko", "zh-Hans", "ja"], before.Summaries.Select(c => c.Language));

        var (response, body) = await Http.PostAsync<TranslationOverviewDto>($"{AdminRoot}/heritages/{id}/translations/generate", null, admin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TranslationMessages.Generated.Text, body.Message);
        var after = body.Data!;

        Assert.Equal((TranslationStatus.PENDING, "Chờ duyệt"), (Translation(after, "en").Status!.Value, Translation(after, "en").Label));
        foreach (var machine in new[] { "ko", "zh-Hans", "ja" })
        {
            Assert.Equal((TranslationStatus.MACHINE, "Bản dịch máy"), (Translation(after, machine).Status!.Value, Translation(after, machine).Label));
            Assert.Equal(TranslationStatus.MACHINE, Summary(after, machine).Status);
        }
        Assert.Equal(TranslationStatus.PENDING, Summary(after, "vi").Status);
        Assert.Equal(TranslationStatus.PENDING, Summary(after, "en").Status);
        Assert.All(after.Translations.Concat(after.Summaries), c => Assert.Null(c.ReviewedAt));
        Assert.Equal((0, null), (after.OutdatedCount, after.OutdatedNotice));

        // Mỗi phần chữ có bản dịch cho từng ngôn ngữ: di sản, giai đoạn, sự kiện, chú thích của ảnh di sản và của ảnh sự kiện.
        var counts = await factory.WithDbAsync(async db => new[]
        {
            await db.HeritageTranslations.CountAsync(t => t.HeritageId == id),
            await db.TimelineTranslations.CountAsync(t => t.Timeline!.HeritageId == id),
            await db.EventTranslations.CountAsync(t => t.Event!.Timeline!.HeritageId == id),
            await db.MediaTranslations.CountAsync(t => t.MediaId == seed.HeritageMediaId || t.MediaId == seed.EventMediaId),
            await db.HeritageSummaries.CountAsync(s => s.HeritageId == id),
        });
        Assert.Equal([4, 4, 4, 8, 5], counts);
        var english = await factory.WithDbAsync(db => db.HeritageTranslations.AsNoTracking().SingleAsync(t => t.HeritageId == id && t.LanguageCode == LanguageCode.en));
        Assert.Equal($"[en] Hoàng thành {seed.Tag}", english.Name);
        Assert.StartsWith("[en] Trung tâm quyền lực", english.ShortDescription);
        Assert.StartsWith("[en] ", english.Content);
        Assert.Null(english.Address);   // địa chỉ giữ như văn bản xếp hạng, không dịch
        Assert.False(english.IsOutdated);
        Assert.Null(english.ReviewedBy);

        // Nhật ký AI: một dòng tóm tắt và bốn dòng dịch, đều của di sản (không có bài học), do quản trị hệ thống yêu cầu.
        var generations = await _k.GenerationsAsync(id);
        Assert.Equal(1, generations.Count(g => g.TaskType == AiTaskType.HERITAGE_SUMMARY));
        Assert.Equal(4, generations.Count(g => g.TaskType == AiTaskType.HERITAGE_TRANSLATION));
        Assert.All(generations, g =>
        {
            Assert.Equal((id, null, AiTaskStatus.SUCCEEDED, SimulatedHeritageAiClient.Name), (g.HeritageId, g.LessonId, g.Status, g.Model));
            Assert.NotNull(g.InputSnapshot);
            Assert.NotNull(g.RawOutput);
            Assert.NotNull(g.CompletedAt);
        });
        var adminId = await factory.WithDbAsync(db => db.Users.Where(u => u.Username == ApiFactory.AdminUsername).Select(u => u.Id).SingleAsync());
        Assert.All(generations, g => Assert.Equal(adminId, g.RequestedBy));
        var links = await factory.WithDbAsync(async db => (
            await db.HeritageTranslations.Where(t => t.HeritageId == id).Select(t => t.AiGenerationId).ToListAsync(),
            await db.HeritageSummaries.Where(s => s.HeritageId == id).Select(s => s.AiGenerationId).ToListAsync()));
        Assert.All(links.Item1.Concat(links.Item2), g => Assert.Contains(g, generations.Select(x => (long?)x.Id)));
        Assert.Equal(generations.Single(g => g.TaskType == AiTaskType.HERITAGE_SUMMARY).Id,
            await factory.WithDbAsync(db => db.HeritageSummaries.Where(s => s.HeritageId == id && s.LanguageCode == LanguageCode.vi).Select(s => s.AiGenerationId!.Value).SingleAsync()));

        // Dữ liệu gửi cho AI chỉ có chữ của di sản.
        var summarized = Assert.Single(factory.HeritageAi.SummarizeRequests);
        Assert.Equal(($"Hoàng thành {seed.Tag}", 1, 1, 1), (summarized.Source.Name, summarized.Source.Timelines.Count, summarized.Source.Events.Count,
            summarized.Source.Media.Count(m => m.Id == seed.EventMediaId)));
        Assert.Equal(["en", "ja", "ko", "zh-Hans"], factory.HeritageAi.TranslateRequests.Select(r => r.TargetLanguage).Order());
        Assert.All(factory.HeritageAi.TranslateRequests, r => Assert.Equal(("vi", "vi"), (r.ContentLanguage, r.SummaryLanguage)));
    }

    [Fact]
    public async Task Generating_does_not_use_the_school_ai_quota_that_lesson_drafting_uses()
    {
        var admin = await _k.AdminAsync();
        var lessons = new LessonTestKit(factory);
        var (school, schoolClass) = await lessons.SchoolWithClassAsync(2);
        var seed = await _k.SeedAsync(admin);
        Task<AiQuotaDto> Quota() => Expect(Http.GetAsync<AiQuotaDto>("/api/v1/teacher/lessons/ai-quota", school.TeacherToken));
        var used = (await Quota()).Used;

        await _k.GenerateAsync(admin, seed.Heritage.Id);
        await _k.GenerateAsync(admin, seed.Heritage.Id);

        Assert.Equal(used, (await Quota()).Used);
        Assert.Equal(10, (await _k.GenerationsAsync(seed.Heritage.Id)).Count);   // nhưng nhật ký vẫn ghi từng lần gọi
        Assert.NotNull(schoolClass);
    }

    [Fact]
    public async Task Generating_needs_vietnamese_content_and_a_visible_heritage()
    {
        var admin = await _k.AdminAsync();
        var tag = Unique();

        // Chưa có nội dung tiếng Việt: chỉ có tên.
        var empty = await Expect(Http.PostAsync<HeritageDetailDto>($"{AdminRoot}/heritages", new { name = $"Chỉ có tên {tag}" }, admin), HttpStatusCode.Created);
        var overview = await _k.OverviewAsync(admin, empty.Id);
        Assert.False(overview.CanGenerate);
        Assert.Contains("chưa có nội dung tiếng Việt", overview.CannotGenerateReason);
        await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/{empty.Id}/translations/generate", null, admin), HttpStatusCode.UnprocessableEntity, "BLOCKED");

        // Chỉ có mô tả ngắn là đủ để dịch.
        var withShort = await Expect(Http.PostAsync<HeritageDetailDto>($"{AdminRoot}/heritages",
            new { name = $"Có mô tả ngắn {tag}", shortDescription = "Một mô tả ngắn." }, admin), HttpStatusCode.Created);
        var ok = await _k.GenerateAsync(admin, withShort.Id);       // di sản Nháp dịch được, chưa cần xuất bản
        Assert.Equal(TranslationStatus.PENDING, Summary(ok, "vi").Status);

        // Di sản đang bị ẩn thì không dịch.
        var seed = await _k.SeedAsync(admin);
        await Expect(Http.PostAsync<HeritageDetailDto>($"{AdminRoot}/heritages/{seed.Heritage.Id}/archive", null, admin));
        Assert.False((await _k.OverviewAsync(admin, seed.Heritage.Id)).CanGenerate);
        await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/{seed.Heritage.Id}/translations/generate", null, admin), HttpStatusCode.UnprocessableEntity, "BLOCKED");

        // Không có di sản, hoặc đã xóa: 404.
        await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/999999/translations/generate", null, admin), HttpStatusCode.NotFound, "BLOCKED");
        await Fail(Http.GetAsync<object>($"{AdminRoot}/heritages/999999/translations", admin), HttpStatusCode.NotFound, "BLOCKED");
        await Expect(Http.DeleteAsync<object>($"{AdminRoot}/heritages/{empty.Id}", admin));
        await Fail(Http.GetAsync<object>($"{AdminRoot}/heritages/{empty.Id}/translations", admin), HttpStatusCode.NotFound, "BLOCKED");
    }

    [Fact]
    public async Task A_second_request_while_one_is_running_is_refused_but_a_dead_run_does_not_block()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        var adminId = await factory.WithDbAsync(db => db.Users.Where(u => u.Username == ApiFactory.AdminUsername).Select(u => u.Id).SingleAsync());
        var running = await factory.WithDbAsync(async db =>
        {
            var row = new AiGeneration { TaskType = AiTaskType.HERITAGE_SUMMARY, HeritageId = id, RequestedBy = adminId, Status = AiTaskStatus.PROCESSING };
            db.AiGenerations.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        });

        Assert.False((await _k.OverviewAsync(admin, id)).CanGenerate);
        await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/{id}/translations/generate", null, admin), HttpStatusCode.Conflict, MessageCodes.TranslationBusy);
        Assert.Equal(0, factory.HeritageAi.SummarizeCalls);

        // Dòng treo quá lâu coi như đã chết, không chặn mãi.
        await factory.WithDbAsync(async db =>
        {
            await db.AiGenerations.Where(g => g.Id == running).ExecuteUpdateAsync(s => s.SetProperty(g => g.CreatedAt, DateTime.UtcNow.AddHours(-1)));
            return 0;
        });
        Assert.True((await _k.OverviewAsync(admin, id)).CanGenerate);
        await _k.GenerateAsync(admin, id);
    }

    // ---- Lỗi Dịch vụ AI ----

    [Fact]
    public async Task When_the_ai_service_fails_nothing_is_saved_the_old_versions_stay_and_every_call_is_logged()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        await _k.GenerateAsync(admin, id);
        await _k.ApproveAllAsync(admin, id);
        var oldEnglish = await factory.WithDbAsync(db => db.HeritageTranslations.AsNoTracking().SingleAsync(t => t.HeritageId == id && t.LanguageCode == LanguageCode.en));
        var generationsBefore = (await _k.GenerationsAsync(id)).Count;

        // Một trong bốn lần dịch hỏng hẳn (sau cả các lần thử lại).
        factory.HeritageAi.TranslateRequests.Clear();
        factory.HeritageAi.TranslateBehavior = (_, request, ct) => request.TargetLanguage == "ja"
            ? throw new AiServiceException("Dịch vụ AI quá tải.", retryable: true)
            : new SimulatedHeritageAiClient().TranslateHeritageAsync(request, "x", ct);
        var fail = await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/{id}/translations/generate", null, admin),
            HttpStatusCode.ServiceUnavailable, MessageCodes.AiUnavailable);
        Assert.Contains("giữ nguyên", fail.Message);

        // Bản cũ giữ nguyên, kể cả trạng thái đã duyệt.
        var after = await factory.WithDbAsync(db => db.HeritageTranslations.AsNoTracking().SingleAsync(t => t.HeritageId == id && t.LanguageCode == LanguageCode.en));
        Assert.Equal((TranslationStatus.APPROVED, oldEnglish.Name, oldEnglish.AiGenerationId, oldEnglish.ReviewedAt), (after.Status, after.Name, after.AiGenerationId, after.ReviewedAt));
        var overview = await _k.OverviewAsync(admin, id);
        Assert.Equal(TranslationStatus.APPROVED, Translation(overview, "en").Status);
        Assert.Equal(TranslationStatus.APPROVED, Summary(overview, "vi").Status);

        // Nhật ký: một dòng tóm tắt thành công, bốn dòng dịch (ba thành công, một FAILED kèm lỗi); không dòng nào kẹt ở "đang xử lý".
        var rows = (await _k.GenerationsAsync(id)).Skip(generationsBefore).ToList();
        Assert.Equal(5, rows.Count);
        Assert.DoesNotContain(rows, g => g.Status == AiTaskStatus.PROCESSING);
        var failed = Assert.Single(rows, g => g.Status == AiTaskStatus.FAILED);
        Assert.Equal("Dịch vụ AI quá tải.", failed.ErrorMessage);
        Assert.Null(failed.RawOutput);
        Assert.Equal(AiTaskType.HERITAGE_TRANSLATION, failed.TaskType);
        Assert.Equal(3, factory.HeritageAi.TranslateRequests.Count(r => r.TargetLanguage == "ja"));   // lần đầu và hai lần thử lại

        // Bấm lại khi dịch vụ đã khỏe: dịch được ngay.
        factory.HeritageAi.TranslateBehavior = null;
        var ok = await _k.GenerateAsync(admin, id);
        Assert.Equal(TranslationStatus.PENDING, Translation(ok, "en").Status);
    }

    [Fact]
    public async Task A_permanent_error_is_not_retried_and_a_failed_summary_stops_before_any_translation()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        factory.HeritageAi.SummarizeBehavior = (_, _, _) => throw new AiServiceException("Sai khóa nội bộ.", retryable: false);

        await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/{id}/translations/generate", null, admin), HttpStatusCode.ServiceUnavailable, MessageCodes.AiUnavailable);

        Assert.Equal((1, 0), (factory.HeritageAi.SummarizeCalls, factory.HeritageAi.TranslateCalls));
        var row = Assert.Single(await _k.GenerationsAsync(id));
        Assert.Equal((AiTaskType.HERITAGE_SUMMARY, AiTaskStatus.FAILED, "Sai khóa nội bộ."), (row.TaskType, row.Status, row.ErrorMessage));
        Assert.Equal(0, await factory.WithDbAsync(db => db.HeritageSummaries.CountAsync(s => s.HeritageId == id)));
        Assert.All((await _k.OverviewAsync(admin, id)).Translations, c => Assert.Null(c.Status));
    }

    [Fact]
    public async Task A_result_that_breaks_the_contract_is_retried_and_a_slow_service_times_out()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;

        // Kết quả thiếu bản chi tiết: thử lại tới khi đạt.
        factory.HeritageAi.SummarizeBehavior = (call, request, ct) => call == 1
            ? Task.FromResult(new AiSummarizeResult("Ngắn", "", "m", new AiUsage(1, 1)))
            : new SimulatedHeritageAiClient().SummarizeHeritageAsync(request, "x", ct);
        await _k.GenerateAsync(admin, id);
        Assert.Equal(2, factory.HeritageAi.SummarizeCalls);

        // Dịch vụ chậm hơn thời gian chờ mỗi lần (1 giây trong kiểm thử): mọi lần đều quá hạn.
        factory.HeritageAi.Reset();
        factory.HeritageAi.SummarizeBehavior = async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new AiSummarizeResult("Ngắn", "Dài", "m", new AiUsage(1, 1));
        };
        await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/{id}/translations/generate", null, admin), HttpStatusCode.ServiceUnavailable, MessageCodes.AiUnavailable);
        var last = (await _k.GenerationsAsync(id)).Last();
        Assert.Equal(AiTaskStatus.FAILED, last.Status);
        Assert.Contains("thời gian chờ", last.ErrorMessage);
        Assert.Equal(3, factory.HeritageAi.SummarizeCalls);
    }

    // ---- Duyệt và từ chối ----

    [Fact]
    public async Task Approving_records_who_and_when_and_writes_the_audit_log_for_the_unit_that_was_approved()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        await _k.GenerateAsync(admin, id);

        var (response, body) = await Http.PostAsync<ReviewResultDto>($"{AdminRoot}/heritages/{id}/translations/vi/approve", new { kind = "SUMMARY" }, admin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("Đã duyệt tóm tắt tiếng Việt.", body.Message);
        var vi = Summary(body.Data!.Overview, "vi");
        Assert.Equal((TranslationStatus.APPROVED, "Đã duyệt", "Quản trị kiểm thử"), (vi.Status!.Value, vi.Label, vi.ReviewedByName));
        Assert.NotNull(vi.ReviewedAt);
        Assert.False(body.Data.MachineTranslationsRefreshed);
        // Duyệt tóm tắt tiếng Việt không đụng tới bản dịch hay tóm tắt tiếng Anh.
        Assert.Equal(TranslationStatus.PENDING, Translation(body.Data.Overview, "en").Status);
        Assert.Equal(TranslationStatus.PENDING, Summary(body.Data.Overview, "en").Status);

        var both = (await _k.ApproveAsync(admin, id, "en", "BOTH")).Overview;
        Assert.Equal(TranslationStatus.APPROVED, Translation(both, "en").Status);
        Assert.Equal(TranslationStatus.APPROVED, Summary(both, "en").Status);

        var logs = await factory.WithDbAsync(db => db.AuditLogs.AsNoTracking()
            .Where(l => l.Action == AuditAction.TRANSLATION_APPROVED && l.NewValue!.Contains($"\"heritageId\":{id},")).OrderBy(l => l.Id).ToListAsync());
        Assert.Equal(["heritage_summaries", "heritage_translations", "heritage_summaries"], logs.Select(l => l.TargetType));
        Assert.All(logs, l => Assert.Equal((ActorRole.ADMIN, "203.0.113.10"), (l.ActorRole, l.IpAddress)));
        Assert.Contains("\"language\":\"vi\"", logs[0].NewValue);
        Assert.Contains("\"kind\":\"SUMMARY\"", logs[0].NewValue);
        Assert.Contains("\"language\":\"en\"", logs[1].NewValue);
        Assert.Contains("\"kind\":\"TRANSLATION\"", logs[1].NewValue);

        var row = await factory.WithDbAsync(db => db.HeritageTranslations.AsNoTracking().SingleAsync(t => t.HeritageId == id && t.LanguageCode == LanguageCode.en));
        Assert.Equal(logs[1].UserId, row.ReviewedBy);
        Assert.Equal(logs[1].TargetId, row.Id);
    }

    [Fact]
    public async Task Rejecting_hides_the_version_and_keeps_the_reason_only_in_the_audit_log()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        await _k.GenerateAsync(admin, id);

        var (response, body) = await Http.PostAsync<ReviewResultDto>($"{AdminRoot}/heritages/{id}/translations/en/reject",
            new { kind = "TRANSLATION", reason = "Dịch sai tên riêng \"Thăng Long\"." }, admin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("Đã từ chối bản dịch tiếng Anh.", body.Message);
        Assert.Contains("dịch lại", body.Message);
        var cell = Translation(body.Data!.Overview, "en");
        Assert.Equal((TranslationStatus.REJECTED, "Bị từ chối"), (cell.Status!.Value, cell.Label));
        Assert.NotNull(cell.ReviewedAt);
        Assert.Equal(TranslationStatus.PENDING, Summary(body.Data.Overview, "en").Status);   // chỉ phần bị chọn

        var log = await factory.WithDbAsync(db => db.AuditLogs.AsNoTracking().SingleAsync(l => l.Action == AuditAction.TRANSLATION_REJECTED && l.NewValue!.Contains($"\"heritageId\":{id},")));
        Assert.Equal("heritage_translations", log.TargetType);
        Assert.Contains("Dịch sai tên riêng", log.NewValue);
        Assert.Contains("\"language\":\"en\"", log.NewValue);

        // Lý do không bắt buộc; quá dài thì bị từ chối.
        await _k.RejectAsync(admin, id, "vi", "SUMMARY");
        Assert.DoesNotContain("reason", (await factory.WithDbAsync(db => db.AuditLogs.AsNoTracking()
            .OrderByDescending(l => l.Id).FirstAsync(l => l.Action == AuditAction.TRANSLATION_REJECTED))).NewValue);
        await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/{id}/translations/en/reject",
            new { kind = "SUMMARY", reason = new string('x', 501) }, admin), HttpStatusCode.BadRequest, "INVALID");

        // Bản bị từ chối không duyệt lại được nếu chưa dịch lại hay sửa.
        await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/{id}/translations/en/approve", new { kind = "TRANSLATION" }, admin),
            HttpStatusCode.UnprocessableEntity, "BLOCKED");
        // Dịch lại thay bản bị từ chối bằng bản mới chờ duyệt.
        var again = await _k.GenerateAsync(admin, id);
        Assert.Equal(TranslationStatus.PENDING, Translation(again, "en").Status);
        Assert.Null(Translation(again, "en").ReviewedAt);
    }

    [Fact]
    public async Task Review_rules_cover_machine_languages_scopes_and_things_that_are_not_waiting()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        await _k.GenerateAsync(admin, id);

        // Hàn, Trung, Nhật là bản dịch máy: không có bước duyệt, kể cả mã zh viết ngắn.
        foreach (var language in new[] { "ko", "zh-Hans", "zh", "ja" })
        {
            await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/{id}/translations/{language}/approve", new { kind = "BOTH" }, admin), HttpStatusCode.UnprocessableEntity, "BLOCKED");
            await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/{id}/translations/{language}/reject", new { kind = "SUMMARY" }, admin), HttpStatusCode.UnprocessableEntity, "BLOCKED");
        }
        // Tiếng Việt là bản gốc: chỉ có tóm tắt để duyệt.
        await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/{id}/translations/vi/approve", new { kind = "TRANSLATION" }, admin), HttpStatusCode.BadRequest, "INVALID");
        // Đối tượng và ngôn ngữ phải hợp lệ.
        await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/{id}/translations/en/approve", new { }, admin), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/{id}/translations/fr/approve", new { kind = "BOTH" }, admin), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/{id}/translations/zh-TW/approve", new { kind = "BOTH" }, admin), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/999999/translations/en/approve", new { kind = "BOTH" }, admin), HttpStatusCode.NotFound, "BLOCKED");

        // Duyệt hai lần: lần hai báo đã duyệt; chọn cả hai mà không còn gì chờ thì báo không có bản nào chờ.
        await _k.ApproveAsync(admin, id, "en", "TRANSLATION");
        var twice = await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/{id}/translations/en/approve", new { kind = "TRANSLATION" }, admin),
            HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("đã được duyệt", twice.Message);
        await _k.ApproveAsync(admin, id, "en", "BOTH");   // còn tóm tắt chờ: duyệt phần chờ, bỏ qua phần đã duyệt
        var none = await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/{id}/translations/en/approve", new { kind = "BOTH" }, admin),
            HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("Không có bản nào đang chờ duyệt", none.Message);

        // Chưa có bản dịch thì 404.
        var bare = await Expect(Http.PostAsync<HeritageDetailDto>($"{AdminRoot}/heritages", new { name = $"Chưa dịch {Unique()}", shortDescription = "Mô tả." }, admin), HttpStatusCode.Created);
        await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/{bare.Id}/translations/en/approve", new { kind = "TRANSLATION" }, admin), HttpStatusCode.NotFound, "BLOCKED");
        await Fail(Http.PostAsync<object>($"{AdminRoot}/heritages/{bare.Id}/translations/en/approve", new { kind = "BOTH" }, admin), HttpStatusCode.UnprocessableEntity, "BLOCKED");
    }

    [Fact]
    public async Task Approving_english_translates_korean_chinese_and_japanese_again_from_the_approved_english_text()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        await _k.GenerateAsync(admin, id);
        var generationsBefore = (await _k.GenerationsAsync(id)).Count;
        var koreanBefore = await factory.WithDbAsync(db => db.HeritageTranslations.AsNoTracking().SingleAsync(t => t.HeritageId == id && t.LanguageCode == LanguageCode.ko));
        Assert.Equal($"[ko] Hoàng thành {seed.Tag}", koreanBefore.Name);   // lần đầu dịch từ tiếng Việt vì chưa có bản tiếng Anh đã duyệt

        // Quản trị sửa bản tiếng Anh rồi duyệt: bản máy được làm lại từ chính bản đã duyệt.
        await Expect(Http.PutAsync<TranslationDetailDto>($"{AdminRoot}/heritages/{id}/translations/en/translation",
            new { name = $"Imperial Citadel {seed.Tag}", shortDescription = "Centre of power." }, admin));
        factory.HeritageAi.TranslateRequests.Clear();
        var result = await _k.ApproveAsync(admin, id, "en", "TRANSLATION");

        Assert.True(result.MachineTranslationsRefreshed);
        Assert.Null(result.Warning);
        var requests = factory.HeritageAi.TranslateRequests.ToList();
        Assert.Equal(["ja", "ko", "zh-Hans"], requests.Select(r => r.TargetLanguage).Order());
        Assert.All(requests, r =>
        {
            Assert.Equal(("en", null), (r.ContentLanguage, r.SummaryLanguage));
            Assert.Null(r.Summary);
            Assert.Equal($"Imperial Citadel {seed.Tag}", r.Content.Name);
            Assert.Equal("Centre of power.", r.Content.ShortDescription);
            Assert.StartsWith("[en] ", r.Content.Content);   // phần chưa sửa vẫn là bản tiếng Anh đã dịch, không lùi về tiếng Việt
        });
        var korean = await factory.WithDbAsync(db => db.HeritageTranslations.AsNoTracking().SingleAsync(t => t.HeritageId == id && t.LanguageCode == LanguageCode.ko));
        Assert.Equal(($"[ko] Imperial Citadel {seed.Tag}", TranslationStatus.MACHINE, koreanBefore.Id), (korean.Name, korean.Status, korean.Id));
        Assert.NotEqual(koreanBefore.AiGenerationId, korean.AiGenerationId);
        // Ba lần dịch lại có nhật ký riêng và không tính hạn mức của trường.
        var rows = (await _k.GenerationsAsync(id)).Skip(generationsBefore).ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, g => Assert.Equal((AiTaskType.HERITAGE_TRANSLATION, AiTaskStatus.SUCCEEDED, (long?)null), (g.TaskType, g.Status, g.LessonId)));

        // Duyệt tóm tắt thì không dịch lại gì.
        factory.HeritageAi.Reset();
        await _k.ApproveAsync(admin, id, "en", "SUMMARY");
        Assert.Equal(0, factory.HeritageAi.TranslateCalls);

        // Dịch lại cả di sản khi đã có bản tiếng Anh đã duyệt: ba ngôn ngữ còn lại dịch từ bản tiếng Anh, tiếng Anh dịch từ tiếng Việt.
        factory.HeritageAi.Reset();
        await _k.GenerateAsync(admin, id);
        var fresh = factory.HeritageAi.TranslateRequests.ToDictionary(r => r.TargetLanguage);
        Assert.Equal(("vi", $"Hoàng thành {seed.Tag}"), (fresh["en"].ContentLanguage, fresh["en"].Content.Name));
        foreach (var language in new[] { "ko", "zh-Hans", "ja" })
        {
            Assert.Equal(("en", $"Imperial Citadel {seed.Tag}"), (fresh[language].ContentLanguage, fresh[language].Content.Name));
            Assert.Equal("vi", fresh[language].SummaryLanguage);   // tóm tắt các ngôn ngữ dịch từ tóm tắt tiếng Việt
        }
    }

    [Fact]
    public async Task A_failure_while_refreshing_the_machine_translations_does_not_undo_the_approval()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        await _k.GenerateAsync(admin, id);
        var before = await factory.WithDbAsync(db => db.HeritageTranslations.AsNoTracking().SingleAsync(t => t.HeritageId == id && t.LanguageCode == LanguageCode.zh));
        factory.HeritageAi.TranslateBehavior = (_, _, _) => throw new AiServiceException("Dịch vụ AI lỗi.", retryable: false);

        var result = await _k.ApproveAsync(admin, id, "en", "TRANSLATION");

        Assert.False(result.MachineTranslationsRefreshed);
        Assert.Equal(TranslationMessages.MachineRefreshFailed, result.Warning);
        Assert.Equal(TranslationStatus.APPROVED, Translation(result.Overview, "en").Status);
        var after = await factory.WithDbAsync(db => db.HeritageTranslations.AsNoTracking().SingleAsync(t => t.HeritageId == id && t.LanguageCode == LanguageCode.zh));
        Assert.Equal((before.Name, before.AiGenerationId), (after.Name, after.AiGenerationId));   // bản máy cũ giữ nguyên
        Assert.Equal(3, (await _k.GenerationsAsync(id)).Count(g => g.Status == AiTaskStatus.FAILED));
    }

    // ---- Dịch lại thay bản cũ ----

    [Fact]
    public async Task Translating_again_replaces_the_old_rows_in_place_and_hides_the_approved_english_until_the_new_one_is_approved()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        await _k.GenerateAsync(admin, id);
        await _k.ApproveAllAsync(admin, id);
        var ids = await factory.WithDbAsync(async db => (
            await db.HeritageTranslations.Where(t => t.HeritageId == id).OrderBy(t => t.Id).Select(t => t.Id).ToListAsync(),
            await db.HeritageSummaries.Where(s => s.HeritageId == id).OrderBy(s => s.Id).Select(s => s.Id).ToListAsync(),
            await db.TimelineTranslations.CountAsync(t => t.Timeline!.HeritageId == id)));

        var again = await _k.GenerateAsync(admin, id);

        // Cùng các dòng (mỗi di sản và ngôn ngữ một dòng), trạng thái về chờ duyệt, người duyệt cũ bị xóa.
        var idsAfter = await factory.WithDbAsync(async db => (
            await db.HeritageTranslations.Where(t => t.HeritageId == id).OrderBy(t => t.Id).Select(t => t.Id).ToListAsync(),
            await db.HeritageSummaries.Where(s => s.HeritageId == id).OrderBy(s => s.Id).Select(s => s.Id).ToListAsync(),
            await db.TimelineTranslations.CountAsync(t => t.Timeline!.HeritageId == id)));
        Assert.Equal(ids.Item1, idsAfter.Item1);
        Assert.Equal(ids.Item2, idsAfter.Item2);
        Assert.Equal(ids.Item3, idsAfter.Item3);
        Assert.Equal(TranslationStatus.PENDING, Translation(again, "en").Status);
        Assert.Equal(TranslationStatus.PENDING, Summary(again, "vi").Status);
        Assert.All(again.Translations.Concat(again.Summaries), c => Assert.Null(c.ReviewedAt));
        Assert.Equal(13, (await _k.GenerationsAsync(id)).Count);   // 5 lần dịch đầu, 3 lần dịch máy lại khi duyệt tiếng Anh, 5 lần dịch lại

        // Trong lúc chờ: tiếng Anh và tóm tắt tiếng Việt ngừng hiện; bản máy hiện ngay bản mới.
        var detail = await _k.DetailAsync(seed.Heritage.Slug!, "en");
        Assert.Equal((true, seed.Heritage.Name), (detail.Localization!.IsFallback, detail.Name));
        Assert.False(detail.HasSummary);
        var korean = await _k.DetailAsync(seed.Heritage.Slug!, "ko");
        Assert.Equal(($"[ko] [en] Hoàng thành {seed.Tag}", true), (korean.Name, korean.Localization!.IsMachineTranslated));   // dịch từ bản tiếng Anh đã duyệt lúc bấm

        await _k.ApproveAllAsync(admin, id);
        Assert.False((await _k.DetailAsync(seed.Heritage.Slug!, "en")).Localization!.IsFallback);
    }

    [Fact]
    public async Task Translations_of_a_deleted_timeline_event_or_image_are_removed_with_it()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        await _k.GenerateAsync(admin, id);

        await Expect(Http.DeleteAsync<object>($"{AdminRoot}/media/{seed.EventMediaId}", admin));
        Assert.Equal(0, await factory.WithDbAsync(db => db.MediaTranslations.CountAsync(m => m.MediaId == seed.EventMediaId)));
        Assert.Equal(4, await factory.WithDbAsync(db => db.MediaTranslations.CountAsync(m => m.MediaId == seed.HeritageMediaId)));

        await Expect(Http.DeleteAsync<object>($"{AdminRoot}/events/{seed.Event.Id}", admin));
        Assert.Equal(0, await factory.WithDbAsync(db => db.EventTranslations.CountAsync(e => e.EventId == seed.Event.Id)));

        await Expect(Http.DeleteAsync<object>($"{AdminRoot}/timelines/{seed.Phase.Id}", admin));
        Assert.Equal(0, await factory.WithDbAsync(db => db.TimelineTranslations.CountAsync(t => t.TimelineId == seed.Phase.Id)));
        // Bản dịch của di sản vẫn còn.
        Assert.Equal(4, await factory.WithDbAsync(db => db.HeritageTranslations.CountAsync(t => t.HeritageId == id)));
    }

    // ---- Cần dịch lại ----

    [Fact]
    public async Task Editing_the_vietnamese_text_marks_every_version_as_needing_a_new_translation_while_old_ones_stay_visible()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        await _k.GenerateAsync(admin, id);
        await _k.ApproveAllAsync(admin, id);

        async Task<TranslationOverviewDto> Expected(int outdated)
        {
            var o = await _k.OverviewAsync(admin, id);
            Assert.Equal(outdated, o.OutdatedCount);
            return o;
        }

        // Sửa thứ không phải chữ cần dịch (đổi năm công nhận) thì không đánh dấu.
        var h = seed.Heritage;
        object Body(string? name = null, string? shortDescription = null, string? address = null, string? content = null) => new
        {
            name = name ?? h.Name, slug = h.Slug, shortDescription = shortDescription ?? h.ShortDescription, content = content ?? h.Content,
            heritageType = h.HeritageType, recognitionLevel = h.RecognitionLevel, recognizedYear = 2001, provinceId = h.Province!.Id,
            address = address ?? h.Address, latitude = h.Latitude, longitude = h.Longitude,
        };
        await Expect(Http.PutAsync<HeritageDetailDto>($"{AdminRoot}/heritages/{id}", Body(), admin));
        await Expected(0);

        // Sửa mô tả ngắn: 4 bản dịch + 5 tóm tắt cần dịch lại.
        await Expect(Http.PutAsync<HeritageDetailDto>($"{AdminRoot}/heritages/{id}", Body(shortDescription: "Mô tả ngắn đã sửa."), admin));
        var overview = await Expected(9);
        Assert.Equal(TranslationMessages.Outdated(9), overview.OutdatedNotice);
        Assert.Contains("9 bản dịch và tóm tắt cần dịch lại", overview.OutdatedNotice);
        var cell = Translation(overview, "en");
        Assert.Equal((TranslationStatus.APPROVED, true, "Cần dịch lại"), (cell.Status!.Value, cell.IsOutdated, cell.Label));
        Assert.Equal("Cần dịch lại", Summary(overview, "ja").Label);

        // Bản cũ vẫn hiện cho khách cho tới khi dịch lại.
        var en = await _k.DetailAsync(h.Slug!, "en");
        Assert.False(en.Localization!.IsFallback);
        Assert.Equal($"[en] Hoàng thành {seed.Tag}", en.Name);
        Assert.True((await _k.DetailAsync(h.Slug!, "ko")).Localization!.IsMachineTranslated);
        Assert.Equal(h.Slug, (await _k.SummaryAsync(h.Slug!, "en")).Slug);

        // Dịch lại thì hết cờ.
        await _k.GenerateAsync(admin, id);
        await Expected(0);

        // Sửa tên giai đoạn, mô tả sự kiện, chú thích ảnh cũng đánh dấu; sửa thứ tự thì không.
        await Expect(Http.PutAsync<TimelineDto>($"{AdminRoot}/timelines/{seed.Phase.Id}", new { name = "Thời Lý (sửa)", description = seed.Phase.Description, startYear = 1010, endYear = 1225 }, admin));
        await Expected(9);
        await _k.GenerateAsync(admin, id);
        await Expect(Http.PutAsync<EventDto>($"{AdminRoot}/events/{seed.Event.Id}", new
            { name = seed.Event.Name, description = "Mô tả sự kiện đã sửa", startYear = 1010, startMonth = 7, startDay = 21 }, admin));
        await Expected(9);
        await _k.GenerateAsync(admin, id);
        await Expect(Http.PutAsync<MediaDto>($"{AdminRoot}/media/{seed.EventMediaId}", new
            { mediaType = "IMAGE", url = $"https://upload.wikimedia.org/{seed.Tag}-su-kien.jpg", caption = "Chú thích đã sửa", author = "Nguyễn Văn An", license = "CC BY-SA 4.0", source = "Wikimedia Commons" }, admin));
        await Expected(9);
        await _k.GenerateAsync(admin, id);
        await Expect(Http.PutAsync<TimelineDto>($"{AdminRoot}/timelines/{seed.Phase.Id}", new { name = "Thời Lý (sửa)", description = seed.Phase.Description, startYear = 1010, endYear = 1226 }, admin));
        await Expected(0);   // chỉ đổi năm, không đổi chữ

        // Thêm giai đoạn mới (chữ mới chưa có bản dịch) cũng nhắc dịch lại.
        await _k.Discovery.AddPhaseAsync(admin, id, "Thời Trần mới", 1226, 1400);
        await Expected(9);

        // Cờ không lan sang di sản khác.
        var other = await _k.SeedAsync(admin);
        await _k.GenerateAsync(admin, other.Heritage.Id);
        await Expect(Http.PutAsync<HeritageDetailDto>($"{AdminRoot}/heritages/{id}", Body(content: h.Content + " Thêm một câu mới."), admin));
        Assert.Equal(0, (await _k.OverviewAsync(admin, other.Heritage.Id)).OutdatedCount);
    }

    // ---- Xem và sửa ----

    [Fact]
    public async Task The_review_screen_shows_the_original_beside_the_translation_for_every_part()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        await _k.GenerateAsync(admin, id);

        var detail = await Expect(Http.GetAsync<TranslationDetailDto>($"{AdminRoot}/heritages/{id}/translations/en/translation", admin));
        Assert.Equal((id, "en", "Tiếng Anh", TranslationStatus.PENDING), (detail.HeritageId, detail.Language, detail.LanguageLabel, detail.Status));
        Assert.Equal(seed.Heritage.Name, detail.Original.Name);
        Assert.Equal($"[en] {seed.Heritage.Name}", detail.Translated.Name);
        Assert.Equal(seed.Heritage.Address, detail.Original.Address);
        Assert.Null(detail.Translated.Address);
        Assert.Equal(seed.Phase.Name, Assert.Single(detail.Original.Timelines).Name);
        Assert.Equal($"[en] {seed.Phase.Name}", Assert.Single(detail.Translated.Timelines).Name);
        Assert.Equal(seed.Event.Id, Assert.Single(detail.Translated.Events).Id);
        Assert.Equal(2, detail.Original.Media.Count);
        Assert.Equal(detail.Original.Media.Select(m => (m.Id, m.EventId)), detail.Translated.Media.Select(m => (m.Id, m.EventId)));
        Assert.All(detail.Translated.Media, m => Assert.StartsWith("[en] ", m.Caption));
        Assert.Null(detail.ReviewedAt);

        // Các ngôn ngữ khác cũng xem được; mã zh viết ngắn nhận được; vi không có bản dịch nội dung.
        Assert.Equal("zh-Hans", (await Expect(Http.GetAsync<TranslationDetailDto>($"{AdminRoot}/heritages/{id}/translations/zh/translation", admin))).Language);
        Assert.Equal(TranslationStatus.MACHINE, (await Expect(Http.GetAsync<TranslationDetailDto>($"{AdminRoot}/heritages/{id}/translations/ja/translation", admin))).Status);
        await Fail(Http.GetAsync<object>($"{AdminRoot}/heritages/{id}/translations/vi/translation", admin), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.GetAsync<object>($"{AdminRoot}/heritages/{id}/translations/fr/translation", admin), HttpStatusCode.BadRequest, "INVALID");

        // Tóm tắt: bản ngắn, bản chi tiết, kèm nội dung tiếng Việt để đối chiếu.
        var summary = await Expect(Http.GetAsync<SummaryDetailDto>($"{AdminRoot}/heritages/{id}/translations/en/summary", admin));
        Assert.StartsWith("[en] ", summary.Summary.ShortText);
        Assert.NotNull(summary.ViSummary);
        Assert.Equal(seed.Heritage.Content, summary.Original.Content);
        var vi = await Expect(Http.GetAsync<SummaryDetailDto>($"{AdminRoot}/heritages/{id}/translations/vi/summary", admin));
        Assert.Null(vi.ViSummary);
        Assert.StartsWith($"Hoàng thành {seed.Tag}.", vi.Summary.ShortText);

        var bare = await Expect(Http.PostAsync<HeritageDetailDto>($"{AdminRoot}/heritages", new { name = $"Chưa dịch {Unique()}", shortDescription = "Mô tả." }, admin), HttpStatusCode.Created);
        await Fail(Http.GetAsync<object>($"{AdminRoot}/heritages/{bare.Id}/translations/en/translation", admin), HttpStatusCode.NotFound, "BLOCKED");
        await Fail(Http.GetAsync<object>($"{AdminRoot}/heritages/{bare.Id}/translations/en/summary", admin), HttpStatusCode.NotFound, "BLOCKED");
    }

    [Fact]
    public async Task Editing_a_translation_in_place_sends_an_approved_or_rejected_version_back_to_pending()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        await _k.GenerateAsync(admin, id);
        await _k.ApproveAsync(admin, id, "en", "TRANSLATION");
        var url = $"{AdminRoot}/heritages/{id}/translations/en/translation";

        var edited = await Expect(Http.PutAsync<TranslationDetailDto>(url, new
        {
            name = "  Imperial Citadel of Thang Long  ",
            shortDescription = "",
            timelines = new[] { new { id = seed.Phase.Id, name = "Ly Dynasty", description = (string?)null } },
            events = new[] { new { id = seed.Event.Id, name = "Capital moved", description = "In 1010." } },
            media = new[] { new { id = seed.EventMediaId, caption = "Edict on moving the capital" }, new { id = seed.HeritageMediaId, caption = "" } },
        }, admin));

        Assert.Equal((TranslationStatus.PENDING, null, "Imperial Citadel of Thang Long"), (edited.Status, edited.ReviewedAt, edited.Translated.Name));
        Assert.Null(edited.Translated.ShortDescription);                       // chuỗi rỗng xóa phần đó: công khai lùi về tiếng Việt cho phần này
        Assert.StartsWith("[en] ", edited.Translated.Content);                 // phần không gửi thì giữ nguyên
        Assert.Equal(("Ly Dynasty", "[en] " + seed.Phase.Description), (edited.Translated.Timelines[0].Name, edited.Translated.Timelines[0].Description));
        Assert.Equal(("Capital moved", "In 1010."), (edited.Translated.Events[0].Name, edited.Translated.Events[0].Description));
        Assert.Equal("Edict on moving the capital", edited.Translated.Media.Single(m => m.Id == seed.EventMediaId).Caption);
        Assert.Null(edited.Translated.Media.Single(m => m.Id == seed.HeritageMediaId).Caption);                 // xóa bản dịch chú thích

        // Bản bị từ chối được sửa cũng về chờ duyệt.
        await _k.RejectAsync(admin, id, "en", "TRANSLATION", "Sai");
        Assert.Equal(TranslationStatus.PENDING, (await Expect(Http.PutAsync<TranslationDetailDto>(url, new { content = "Nội dung sửa." }, admin))).Status);

        // Kiểm tra dữ liệu vào.
        await Fail(Http.PutAsync<object>(url, new { name = "   " }, admin), HttpStatusCode.BadRequest, "REQUIRED");
        await Fail(Http.PutAsync<object>(url, new { name = new string('a', 256) }, admin), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.PutAsync<object>(url, new { shortDescription = new string('a', 1001) }, admin), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.PutAsync<object>(url, new { media = new[] { new { id = seed.EventMediaId, caption = new string('a', 501) } } }, admin), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.PutAsync<object>(url, new { timelines = new[] { new { id = 987654L, name = "Lạ" } } }, admin), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.PutAsync<object>(url, new { events = new[] { new { id = 987654L, name = "Lạ" } } }, admin), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.PutAsync<object>(url, new { media = new[] { new { id = 987654L, caption = "Lạ" } } }, admin), HttpStatusCode.BadRequest, "INVALID");
        // Bản máy không sửa tay; tiếng Việt không có bản dịch.
        await Fail(Http.PutAsync<object>($"{AdminRoot}/heritages/{id}/translations/ko/translation", new { name = "x" }, admin), HttpStatusCode.UnprocessableEntity, "BLOCKED");
        await Fail(Http.PutAsync<object>($"{AdminRoot}/heritages/{id}/translations/vi/translation", new { name = "x" }, admin), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.PutAsync<object>($"{AdminRoot}/heritages/999999/translations/en/translation", new { name = "x" }, admin), HttpStatusCode.NotFound, "BLOCKED");
    }

    [Fact]
    public async Task Editing_a_summary_keeps_both_versions_and_returns_it_to_pending()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        await _k.GenerateAsync(admin, id);
        await _k.ApproveAsync(admin, id, "vi", "SUMMARY");
        var url = $"{AdminRoot}/heritages/{id}/translations/vi/summary";

        var edited = await Expect(Http.PutAsync<SummaryDetailDto>(url, new { shortText = "  Bản ngắn đã sửa.  " }, admin));
        Assert.Equal((TranslationStatus.PENDING, "Bản ngắn đã sửa."), (edited.Status, edited.Summary.ShortText));
        Assert.StartsWith($"Hoàng thành {seed.Tag}.", edited.Summary.DetailedText);   // bản chi tiết không gửi thì giữ nguyên
        Assert.Null(edited.ReviewedAt);

        var detailed = await Expect(Http.PutAsync<SummaryDetailDto>(url, new { detailedText = "Bản chi tiết đã sửa." }, admin));
        Assert.Equal(("Bản ngắn đã sửa.", "Bản chi tiết đã sửa."), (detailed.Summary.ShortText, detailed.Summary.DetailedText));

        await Fail(Http.PutAsync<object>(url, new { shortText = " " }, admin), HttpStatusCode.BadRequest, "REQUIRED");
        await Fail(Http.PutAsync<object>(url, new { detailedText = "" }, admin), HttpStatusCode.BadRequest, "REQUIRED");
        await Fail(Http.PutAsync<object>(url, new { shortText = new string('a', 2001) }, admin), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.PutAsync<object>($"{AdminRoot}/heritages/{id}/translations/ja/summary", new { shortText = "x" }, admin), HttpStatusCode.UnprocessableEntity, "BLOCKED");
        await Fail(Http.PutAsync<object>($"{AdminRoot}/heritages/{id}/translations/xx/summary", new { shortText = "x" }, admin), HttpStatusCode.BadRequest, "INVALID");
    }

    // ---- Hàng chờ ----

    [Fact]
    public async Task The_review_queue_lists_by_tab_with_filters_and_counts()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var other = await _k.SeedAsync(admin);
        await _k.GenerateAsync(admin, seed.Heritage.Id);
        await _k.GenerateAsync(admin, other.Heritage.Id);
        await _k.ApproveAsync(admin, other.Heritage.Id, "en", "BOTH");
        await _k.RejectAsync(admin, other.Heritage.Id, "vi", "SUMMARY");
        var queue = $"{AdminRoot}/translations";

        Task<PagedResult<TranslationQueueItemDto>> List(string query) =>
            Expect(Http.GetAsync<PagedResult<TranslationQueueItemDto>>($"{queue}?{query}", admin));

        // Chờ duyệt (mặc định): di sản thứ nhất đủ ba mục (tóm tắt tiếng Việt, bản dịch và tóm tắt tiếng Anh), di sản thứ hai còn không mục nào.
        var pending = await List($"heritageId={seed.Heritage.Id}");
        Assert.Equal(3, pending.TotalItems);
        Assert.Equal(new[] { (TranslationKind.SUMMARY, "vi"), (TranslationKind.SUMMARY, "en"), (TranslationKind.TRANSLATION, "en") }.Order(),
            pending.Items.Select(i => (i.Kind, i.Language)).Order());
        Assert.All(pending.Items, i =>
        {
            Assert.Equal((seed.Heritage.Name, seed.Heritage.Slug, TranslationStatus.PENDING), (i.HeritageName, i.Slug, i.Status));
            Assert.NotNull(i.RequestedAt);
            Assert.Null(i.ReviewedAt);
        });
        Assert.Equal(0, (await List($"heritageId={other.Heritage.Id}")).TotalItems);
        Assert.Equal(pending.Items.Select(i => i.LanguageLabel).Order(), pending.Items.Select(i => i.Language == "vi" ? "Tiếng Việt" : "Tiếng Anh").Order());

        // Lọc theo loại và ngôn ngữ (cả mã zh viết ngắn).
        Assert.Equal(1, (await List($"heritageId={seed.Heritage.Id}&kind=TRANSLATION")).TotalItems);
        Assert.Equal(2, (await List($"heritageId={seed.Heritage.Id}&kind=SUMMARY")).TotalItems);
        Assert.Equal(2, (await List($"heritageId={seed.Heritage.Id}&language=en")).TotalItems);
        Assert.Equal(1, (await List($"heritageId={seed.Heritage.Id}&language=vi")).TotalItems);
        Assert.Equal(0, (await List($"heritageId={seed.Heritage.Id}&language=ko")).TotalItems);

        // Đã duyệt, bản dịch máy, bị từ chối.
        var approved = await List($"tab=APPROVED&heritageId={other.Heritage.Id}");
        Assert.Equal(2, approved.TotalItems);
        Assert.All(approved.Items, i => Assert.NotNull(i.ReviewedAt));
        var machine = await List($"tab=MACHINE&heritageId={seed.Heritage.Id}");
        Assert.Equal(["ja", "ko", "zh-Hans"], machine.Items.Where(i => i.Kind == TranslationKind.TRANSLATION).Select(i => i.Language).Order());
        Assert.Equal(6, machine.TotalItems);
        Assert.Equal(1, (await List($"tab=MACHINE&heritageId={seed.Heritage.Id}&language=zh&kind=SUMMARY")).TotalItems);
        var rejected = Assert.Single((await List($"tab=REJECTED&heritageId={other.Heritage.Id}")).Items);
        Assert.Equal((TranslationKind.SUMMARY, "vi"), (rejected.Kind, rejected.Language));

        // Phân trang.
        var first = await List($"heritageId={seed.Heritage.Id}&tab=MACHINE&pageSize=4");
        Assert.Equal((4, 6, 2), (first.Items.Count, first.TotalItems, first.TotalPages));
        var second = await List($"heritageId={seed.Heritage.Id}&tab=MACHINE&pageSize=4&page=2");
        Assert.Equal(2, second.Items.Count);
        Assert.Empty(first.Items.Select(i => (i.Kind, i.Language)).Intersect(second.Items.Select(i => (i.Kind, i.Language))));

        // Kiểm tra bộ lọc.
        await Fail(Http.GetAsync<object>($"{queue}?language=fr", admin), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.GetAsync<object>($"{queue}?page=0", admin), HttpStatusCode.BadRequest, "INVALID");
        await Fail(Http.GetAsync<object>($"{queue}?pageSize=101", admin), HttpStatusCode.BadRequest, "INVALID");

        // Số đếm của các tab (cả hệ thống nên chỉ kiểm quan hệ với dữ liệu vừa tạo).
        var counts = await Expect(Http.GetAsync<TranslationCountsDto>($"{queue}/counts", admin));
        Assert.True(counts.Pending >= 3 && counts.Approved >= 2 && counts.Machine >= 12);
        var total = await Expect(Http.GetAsync<PagedResult<TranslationQueueItemDto>>($"{queue}?pageSize=1", admin));
        Assert.Equal(counts.Pending, total.TotalItems);
        Assert.Equal(counts.Machine, (await List("tab=MACHINE&pageSize=1")).TotalItems);
        Assert.Equal(counts.Approved, (await List("tab=APPROVED&pageSize=1")).TotalItems);

        // Di sản đã xóa không còn trong hàng chờ.
        var gone = await Expect(Http.PostAsync<HeritageDetailDto>($"{AdminRoot}/heritages", new { name = $"Sẽ xóa {Unique()}", shortDescription = "Mô tả." }, admin), HttpStatusCode.Created);
        await _k.GenerateAsync(admin, gone.Id);
        Assert.Equal(3, (await List($"heritageId={gone.Id}")).TotalItems);
        await Expect(Http.DeleteAsync<object>($"{AdminRoot}/heritages/{gone.Id}", admin));
        Assert.Equal(0, (await List($"heritageId={gone.Id}")).TotalItems);
    }

    [Fact]
    public async Task The_heritage_list_shows_and_filters_the_translation_state()
    {
        var admin = await _k.AdminAsync();
        var none = await _k.SeedAsync(admin);
        var pending = await _k.SeedAsync(admin);
        var approved = await _k.SeedAsync(admin);
        var outdated = await _k.SeedAsync(admin);
        await _k.GenerateAsync(admin, pending.Heritage.Id);
        await _k.GenerateAsync(admin, approved.Heritage.Id);
        await _k.ApproveAllAsync(admin, approved.Heritage.Id);
        await _k.GenerateAsync(admin, outdated.Heritage.Id);
        await _k.ApproveAllAsync(admin, outdated.Heritage.Id);
        await Expect(Http.PutAsync<TimelineDto>($"{AdminRoot}/timelines/{outdated.Phase.Id}",
            new { name = "Tên giai đoạn mới", description = "x", startYear = 1010, endYear = 1225 }, admin));

        async Task<List<(long Id, TranslationState State)>> List(string? state)
        {
            var result = await Expect(Http.GetAsync<PagedResult<HeritageSummaryDto>>(
                $"{AdminRoot}/heritages?pageSize=100{(state is null ? "" : "&translationState=" + state)}", admin));
            return result.Items.Select(i => (i.Id, i.TranslationState)).ToList();
        }
        var all = await List(null);
        Assert.Equal(TranslationState.NONE, all.Single(x => x.Id == none.Heritage.Id).State);
        Assert.Equal(TranslationState.PENDING, all.Single(x => x.Id == pending.Heritage.Id).State);
        Assert.Equal(TranslationState.APPROVED, all.Single(x => x.Id == approved.Heritage.Id).State);
        Assert.Equal(TranslationState.OUTDATED, all.Single(x => x.Id == outdated.Heritage.Id).State);

        // Màn hình Biên tập di sản cũng biết tình trạng để nhắc "Dịch và tóm tắt bằng AI" khi chưa dịch.
        foreach (var (seeded, expectedState) in new[]
                 {
                     (none, TranslationState.NONE), (pending, TranslationState.PENDING), (approved, TranslationState.APPROVED), (outdated, TranslationState.OUTDATED),
                 })
            Assert.Equal(expectedState, (await Expect(Http.GetAsync<HeritageDetailDto>($"{AdminRoot}/heritages/{seeded.Heritage.Id}", admin))).TranslationState);

        foreach (var (state, expected) in new[]
                 {
                     ("NONE", none.Heritage.Id), ("PENDING", pending.Heritage.Id), ("APPROVED", approved.Heritage.Id), ("OUTDATED", outdated.Heritage.Id),
                 })
        {
            var filtered = await List(state);
            Assert.Contains(expected, filtered.Select(x => x.Id));
            Assert.All(filtered, x => Assert.Equal(Enum.Parse<TranslationState>(state), x.State));
            Assert.Equal(1, filtered.Count(x => new[] { none, pending, approved, outdated }.Any(s => s.Heritage.Id == x.Id)));
        }
        await Fail(Http.GetAsync<object>($"{AdminRoot}/heritages?translationState=KHAC", admin), HttpStatusCode.BadRequest, "INVALID");
    }
}
