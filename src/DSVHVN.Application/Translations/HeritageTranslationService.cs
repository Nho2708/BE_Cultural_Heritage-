using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DSVHVN.Application.Translations;

/// <summary>
/// Dịch và tóm tắt di sản bằng AI, xem và duyệt bản dịch (quản trị hệ thống; màn hình Biên tập di sản tab Bản dịch và tóm tắt, màn hình Duyệt bản dịch và tóm tắt).
/// <list type="bullet">
/// <item>Dịch và tóm tắt: chỉ ở di sản đã có nội dung tiếng Việt (Nháp hoặc Đã xuất bản, chưa xóa). Gọi Dịch vụ AI một lần để tóm tắt tiếng Việt rồi bốn lần (song song) để dịch sang
/// tiếng Anh, Hàn, Trung giản thể, Nhật, mỗi lần kèm bản tóm tắt tiếng Việt cần dịch. Mỗi lần gọi một dòng <c>ai_generations</c>; không tính hạn mức lượt AI của trường.
/// Kết quả lưu sẵn, không gọi AI khi khách xem. Một lần lỗi thì không lưu gì và bản cũ giữ nguyên.</item>
/// <item>Trạng thái: bản dịch tiếng Anh và tóm tắt tiếng Việt, tiếng Anh vào <c>PENDING</c>; bản Hàn, Trung, Nhật vào <c>MACHINE</c> (hiện ngay, luôn kèm nhãn "bản dịch máy", không có bước duyệt).
/// Dịch lại thay chính dòng cũ (mỗi di sản và ngôn ngữ một dòng): bản cũ ngừng hiện cho tới khi bản mới được duyệt.</item>
/// <item>Duyệt hoặc từ chối theo (di sản, ngôn ngữ) cho bản dịch hoặc cho tóm tắt, ghi người duyệt, thời điểm và nhật ký thao tác; lý do từ chối chỉ ghi vào nhật ký.
/// Duyệt bản dịch tiếng Anh thì bản Hàn, Trung, Nhật được dịch lại từ bản tiếng Anh đã duyệt.</item>
/// </list>
/// </summary>
public sealed class HeritageTranslationService(
    IAppDbContext db,
    HeritageSourceLoader loader,
    HeritageAiRunner ai,
    IAuditLogger audit,
    TimeProvider clock,
    ILogger<HeritageTranslationService> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ---- Tổng quan của một di sản ----

    public async Task<TranslationOverviewDto> GetOverviewAsync(long heritageId, CancellationToken ct) =>
        await BuildOverviewAsync(heritageId, ct);

    private async Task<TranslationOverviewDto> BuildOverviewAsync(long heritageId, CancellationToken ct)
    {
        var heritage = await db.Heritages.AsNoTracking().Where(h => h.Id == heritageId && h.DeletedAt == null)
                           .Select(h => new { h.Name, h.Status, h.ShortDescription, h.Content })
                           .SingleOrDefaultAsync(ct)
                       ?? throw AppException.NotFound(Messages.NotFound("xem bản dịch", "di sản"));
        var translations = await db.HeritageTranslations.AsNoTracking().Where(t => t.HeritageId == heritageId).ToListAsync(ct);
        var summaries = await db.HeritageSummaries.AsNoTracking().Where(t => t.HeritageId == heritageId).ToListAsync(ct);
        var reviewerIds = translations.Select(t => t.ReviewedBy).Concat(summaries.Select(s => s.ReviewedBy)).Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
        var reviewers = reviewerIds.Count == 0
            ? new Dictionary<long, string?>()
            : await db.Users.AsNoTracking().Where(u => reviewerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        var translationCells = Languages.TranslationTargets.Select(lang =>
        {
            var row = translations.SingleOrDefault(t => t.LanguageCode == lang);
            return Cell(TranslationKind.TRANSLATION, lang, row?.Status, row?.IsOutdated ?? false, row?.CreatedAt, row?.UpdatedAt, row?.ReviewedAt, row?.ReviewedBy, reviewers);
        }).ToList();
        var summaryCells = Languages.All.Select(info =>
        {
            var row = summaries.SingleOrDefault(s => s.LanguageCode == info.Code);
            return Cell(TranslationKind.SUMMARY, info.Code, row?.Status, row?.IsOutdated ?? false, row?.CreatedAt, row?.UpdatedAt, row?.ReviewedAt, row?.ReviewedBy, reviewers);
        }).ToList();

        var outdated = translations.Count(t => t.IsOutdated) + summaries.Count(s => s.IsOutdated);
        string? reason = null;
        if (heritage.Status == ContentStatus.ARCHIVED) reason = "Di sản đang bị ẩn. Hãy hiện lại di sản trước khi dịch.";
        else if (string.IsNullOrWhiteSpace(heritage.Name)
                 || (string.IsNullOrWhiteSpace(heritage.Content) && string.IsNullOrWhiteSpace(heritage.ShortDescription)))
            reason = "Di sản chưa có nội dung tiếng Việt để dịch. Hãy nhập mô tả ngắn hoặc nội dung trước.";
        else if (await IsBusyAsync(heritageId, ct)) reason = TranslationMessages.Busy.Text;

        return new TranslationOverviewDto(
            heritageId, heritage.Name ?? string.Empty, reason is null, reason, outdated,
            outdated > 0 ? TranslationMessages.Outdated(outdated) : null, translationCells, summaryCells);
    }

    private static TranslationCellDto Cell(TranslationKind kind, LanguageCode language, TranslationStatus? status, bool outdated, DateTime? created,
        DateTime? updated, DateTime? reviewedAt, long? reviewedBy, IReadOnlyDictionary<long, string?> reviewers) =>
        new(kind, Languages.Tag(language), Languages.ViLabel(language), status, outdated && status is not null, StateLabel(status, outdated),
            created, updated, reviewedAt, reviewedBy, reviewedBy is { } id ? reviewers.GetValueOrDefault(id) : null);

    /// <summary>Nhãn của ô: Chưa có, Bản dịch máy, Chờ duyệt, Đã duyệt, Bị từ chối; bản đã có mà nội dung tiếng Việt sửa sau đó thì "Cần dịch lại".</summary>
    public static string StateLabel(TranslationStatus? status, bool outdated) => status switch
    {
        null => "Chưa có",
        _ when outdated => "Cần dịch lại",
        TranslationStatus.MACHINE => "Bản dịch máy",
        TranslationStatus.PENDING => "Chờ duyệt",
        TranslationStatus.APPROVED => "Đã duyệt",
        TranslationStatus.REJECTED => "Bị từ chối",
        _ => status.ToString()!,
    };

    private async Task<bool> IsBusyAsync(long heritageId, CancellationToken ct)
    {
        var staleBefore = Now - TranslationRules.StaleProcessing;
        return await db.AiGenerations.AsNoTracking().AnyAsync(g => g.HeritageId == heritageId
            && g.Status == AiTaskStatus.PROCESSING && g.CreatedAt > staleBefore, ct);
    }

    // ---- Dịch và tóm tắt bằng AI ----

    /// <summary>Nút "Dịch và tóm tắt bằng AI". Trả tổng quan mới của di sản.</summary>
    public async Task<TranslationOverviewDto> GenerateAsync(Actor actor, long heritageId, string correlationId, CancellationToken ct)
    {
        if (actor.Role != RoleCode.ADMIN) throw AppException.Forbidden(Messages.Forbidden);
        const string action = "dịch di sản";
        var source = await loader.LoadAsync(heritageId, action, ct);
        if (source.Heritage.Status == ContentStatus.ARCHIVED)
            throw AppException.BusinessRule(Messages.Blocked(action, "Di sản đang bị ẩn. Hãy hiện lại di sản trước khi dịch."));
        if (!source.HasContent)
            throw AppException.BusinessRule(Messages.Blocked(action, "Di sản chưa có nội dung tiếng Việt để dịch. Hãy nhập mô tả ngắn hoặc nội dung trước."));
        if (await IsBusyAsync(heritageId, ct)) throw new AppException(409, TranslationMessages.Busy);

        var vietnamese = source.ToAiText();

        // Tiếng Hàn, Trung, Nhật dịch từ bản tiếng Anh đã duyệt nếu có (quy tắc Bản dịch nội dung di sản), không thì từ tiếng Việt.
        var approvedEnglish = await loader.LoadRowsAsync(heritageId, LanguageCode.en, ct);
        if (approvedEnglish is not { Heritage.Status: TranslationStatus.APPROVED }) approvedEnglish = null;
        var englishText = approvedEnglish?.ToAiText(source);

        // 1. Tóm tắt tiếng Việt từ chính nội dung đã có.
        var summaryJob = await StartAsync(actor, heritageId, AiTaskType.HERITAGE_SUMMARY,
            new { language = Languages.Tag(LanguageCode.vi), source = vietnamese }, new { language = Languages.Tag(LanguageCode.vi) }, ct);
        AiAttempt<AiSummarizeResult> summarized;
        try
        {
            summarized = await ai.SummarizeAsync(new AiSummarizeRequest(summaryJob.Id, heritageId, vietnamese), correlationId, ct);
        }
        catch (OperationCanceledException)
        {
            await CloseAsync(summaryJob, AiTaskStatus.FAILED, "Yêu cầu bị hủy trước khi dịch vụ AI trả lời.", null, null, CancellationToken.None);
            throw;
        }
        if (!summarized.Succeeded)
        {
            await CloseAsync(summaryJob, AiTaskStatus.FAILED, summarized.Error, null, null, ct);
            throw new AppException(503, TranslationMessages.AiUnavailable);
        }
        await CloseAsync(summaryJob, AiTaskStatus.SUCCEEDED, null, ai.ModelName, LessonJson.Serialize(summarized.Value!), ct);
        var summaryVi = new AiSummaryText(summarized.Value!.ShortText, summarized.Value.DetailedText);

        // 2. Dịch nội dung và tóm tắt sang bốn ngôn ngữ, song song (mỗi lần gọi một dòng nhật ký riêng).
        var jobs = new List<(LanguageCode Language, AiGeneration Generation, AiTranslateRequest Request)>();
        foreach (var language in Languages.TranslationTargets)
        {
            var fromEnglish = language != LanguageCode.en && englishText is not null;
            var content = fromEnglish ? englishText! : vietnamese;
            var contentLanguage = Languages.Tag(fromEnglish ? LanguageCode.en : LanguageCode.vi);
            var generation = await StartAsync(actor, heritageId, AiTaskType.HERITAGE_TRANSLATION,
                new { target = Languages.Tag(language), contentLanguage, content, summary = summaryVi },
                new { language = Languages.Tag(language), contentLanguage, includesSummary = true }, ct);
            jobs.Add((language, generation, new AiTranslateRequest(generation.Id, heritageId, Languages.Tag(language), contentLanguage, content,
                Languages.Tag(LanguageCode.vi), summaryVi)));
        }

        AiAttempt<AiTranslateResult>[] results;
        try
        {
            results = await Task.WhenAll(jobs.Select(j => ai.TranslateAsync(j.Request, correlationId, ct)));
        }
        catch (OperationCanceledException)
        {
            foreach (var job in jobs)
                await CloseAsync(job.Generation, AiTaskStatus.FAILED, "Yêu cầu bị hủy trước khi dịch vụ AI trả lời.", null, null, CancellationToken.None);
            throw;
        }

        string? failure = null;
        for (var i = 0; i < jobs.Count; i++)
        {
            if (results[i].Succeeded)
            {
                await CloseAsync(jobs[i].Generation, AiTaskStatus.SUCCEEDED, null, ai.ModelName, LessonJson.Serialize(results[i].Value!), ct);
            }
            else
            {
                failure ??= results[i].Error;
                await CloseAsync(jobs[i].Generation, AiTaskStatus.FAILED, results[i].Error, null, null, ct);
            }
        }
        if (failure is not null) throw new AppException(503, TranslationMessages.AiUnavailable);

        // 3. Lưu một lần: bản dịch thay bản cũ, tóm tắt thay bản cũ.
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await UpsertSummaryAsync(heritageId, LanguageCode.vi, summaryVi, summaryJob.Id, ct);
            for (var i = 0; i < jobs.Count; i++)
            {
                var (language, generation, _) = jobs[i];
                var result = results[i].Value!;
                await UpsertTranslationAsync(source, language, result.Content, generation.Id, Languages.NewTranslationStatus(language), outdated: false, ct);
                await UpsertSummaryAsync(heritageId, language, result.Summary!, generation.Id, ct);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Hai yêu cầu dịch cùng một di sản chạm nhau ở ràng buộc không trùng (di sản, ngôn ngữ): yêu cầu thua báo đang bận.
            logger.LogWarning(ex, "Hai yêu cầu dịch cùng lúc cho di sản {HeritageId}", heritageId);
            throw new AppException(409, TranslationMessages.Busy);
        }

        return await BuildOverviewAsync(heritageId, ct);
    }

    private async Task<AiGeneration> StartAsync(Actor actor, long heritageId, AiTaskType type, object input, object parameters, CancellationToken ct)
    {
        var generation = new AiGeneration
        {
            TaskType = type,
            HeritageId = heritageId,
            RequestedBy = actor.UserId,
            InputSnapshot = LessonJson.Serialize(input),
            Params = LessonJson.Serialize(parameters),
            Status = AiTaskStatus.PROCESSING,
        };
        db.AiGenerations.Add(generation);
        await db.SaveChangesAsync(ct);
        return generation;
    }

    private async Task CloseAsync(AiGeneration generation, AiTaskStatus status, string? error, string? model, string? rawOutput, CancellationToken ct)
    {
        generation.Status = status;
        generation.Model = model;
        generation.RawOutput = rawOutput;
        generation.ErrorMessage = error is null ? null : error.Length > 1000 ? error[..1000] : error;
        generation.CompletedAt = Now;
        await db.SaveChangesAsync(ct);
    }

    // ---- Ghi bản dịch, tóm tắt ----

    /// <summary>
    /// Thay (hoặc thêm) bản dịch của một ngôn ngữ: dòng chính, giai đoạn, sự kiện, chú thích ảnh. Dòng có sẵn được cập nhật tại chỗ (mỗi di sản và ngôn ngữ một dòng),
    /// phần con không còn trong kết quả thì xóa. Không lưu; người gọi lưu trong giao dịch của mình.
    /// </summary>
    private async Task UpsertTranslationAsync(HeritageSource source, LanguageCode language, AiHeritageText text, long generationId,
        TranslationStatus status, bool outdated, CancellationToken ct)
    {
        var id = source.Heritage.Id;
        var row = await db.HeritageTranslations.SingleOrDefaultAsync(t => t.HeritageId == id && t.LanguageCode == language, ct);
        if (row is null)
        {
            row = new HeritageTranslation { HeritageId = id, LanguageCode = language };
            db.HeritageTranslations.Add(row);
        }
        row.Name = text.Name;
        row.ShortDescription = text.ShortDescription;
        row.Content = text.Content;
        row.Status = status;
        row.IsOutdated = outdated;
        row.AiGenerationId = generationId;
        row.ReviewedBy = null;
        row.ReviewedAt = null;

        var timelines = await db.TimelineTranslations.Where(t => t.LanguageCode == language && t.Timeline!.HeritageId == id).ToListAsync(ct);
        Sync(db.TimelineTranslations, timelines, text.Timelines, x => x.TimelineId, x => x.Id,
            item => new TimelineTranslation { TimelineId = item.Id, LanguageCode = language, Name = item.Name, Description = item.Description },
            (x, item) => { x.Name = item.Name; x.Description = item.Description; });

        var events = await db.EventTranslations.Where(t => t.LanguageCode == language && t.Event!.Timeline!.HeritageId == id).ToListAsync(ct);
        Sync(db.EventTranslations, events, text.Events, x => x.EventId, x => x.Id,
            item => new EventTranslation { EventId = item.Id, LanguageCode = language, Name = item.Name, Description = item.Description },
            (x, item) => { x.Name = item.Name; x.Description = item.Description; });

        var media = await db.MediaTranslations.Where(t => t.LanguageCode == language
                && (t.Media!.HeritageId == id || (t.Media.EventId != null && t.Media.Event!.Timeline!.HeritageId == id))).ToListAsync(ct);
        Sync(db.MediaTranslations, media, text.Media, x => x.MediaId, x => x.Id,
            item => new MediaTranslation { MediaId = item.Id, LanguageCode = language, Caption = item.Caption },
            (x, item) => x.Caption = item.Caption);
    }

    private static void Sync<TRow, TItem>(Microsoft.EntityFrameworkCore.DbSet<TRow> set, List<TRow> existing, IEnumerable<TItem> items,
        Func<TRow, long> rowKey, Func<TItem, long> itemKey, Func<TItem, TRow> create, Action<TRow, TItem> update) where TRow : class
    {
        var byKey = existing.ToDictionary(rowKey);
        var seen = new HashSet<long>();
        foreach (var item in items)
        {
            var key = itemKey(item);
            if (!seen.Add(key)) continue;
            if (byKey.TryGetValue(key, out var row)) update(row, item);
            else set.Add(create(item));
        }
        set.RemoveRange(existing.Where(r => !seen.Contains(rowKey(r))));
    }

    private async Task UpsertSummaryAsync(long heritageId, LanguageCode language, AiSummaryText text, long generationId, CancellationToken ct)
    {
        var row = await db.HeritageSummaries.SingleOrDefaultAsync(s => s.HeritageId == heritageId && s.LanguageCode == language, ct);
        if (row is null)
        {
            row = new HeritageSummary { HeritageId = heritageId, LanguageCode = language };
            db.HeritageSummaries.Add(row);
        }
        row.ShortText = text.ShortText;
        row.DetailedText = text.DetailedText;
        row.Status = Languages.NewSummaryStatus(language);
        row.IsOutdated = false;
        row.AiGenerationId = generationId;
        row.ReviewedBy = null;
        row.ReviewedAt = null;
    }

    // ---- Hàng chờ duyệt ----

    public async Task<TranslationCountsDto> GetCountsAsync(CancellationToken ct)
    {
        var translations = db.HeritageTranslations.AsNoTracking().Where(t => t.Heritage!.DeletedAt == null);
        var summaries = db.HeritageSummaries.AsNoTracking().Where(t => t.Heritage!.DeletedAt == null);
        return new TranslationCountsDto(
            await translations.CountAsync(t => t.Status == TranslationStatus.PENDING, ct) + await summaries.CountAsync(t => t.Status == TranslationStatus.PENDING, ct),
            await translations.CountAsync(t => t.Status == TranslationStatus.APPROVED, ct) + await summaries.CountAsync(t => t.Status == TranslationStatus.APPROVED, ct),
            await translations.CountAsync(t => t.Status == TranslationStatus.MACHINE, ct) + await summaries.CountAsync(t => t.Status == TranslationStatus.MACHINE, ct),
            await translations.CountAsync(t => t.IsOutdated, ct) + await summaries.CountAsync(t => t.IsOutdated, ct));
    }

    /// <summary>
    /// Danh sách các bản dịch và tóm tắt theo tab (mặc định Chờ duyệt), mới yêu cầu trước; lọc theo di sản, ngôn ngữ, loại. Chỉ di sản chưa xóa.
    /// </summary>
    public async Task<PagedResult<TranslationQueueItemDto>> ListAsync(TranslationQueueQuery query, CancellationToken ct)
    {
        Paging.Normalize(query.Page, query.PageSize);
        LanguageCode? language = null;
        if (!string.IsNullOrWhiteSpace(query.Language))
            language = Languages.TryParse(query.Language)
                       ?? throw AppException.Validation("language", Messages.Invalid("Ngôn ngữ", "chỉ nhận vi, en, ko, zh-Hans hoặc ja"));
        var status = (query.Tab ?? QueueTab.PENDING) switch
        {
            QueueTab.APPROVED => TranslationStatus.APPROVED,
            QueueTab.MACHINE => TranslationStatus.MACHINE,
            QueueTab.REJECTED => TranslationStatus.REJECTED,
            _ => TranslationStatus.PENDING,
        };

        var translations = db.HeritageTranslations.AsNoTracking().Where(t => t.Heritage!.DeletedAt == null && t.Status == status);
        var summaries = db.HeritageSummaries.AsNoTracking().Where(t => t.Heritage!.DeletedAt == null && t.Status == status);
        if (query.HeritageId is { } heritageId)
        {
            translations = translations.Where(t => t.HeritageId == heritageId);
            summaries = summaries.Where(t => t.HeritageId == heritageId);
        }
        if (language is { } lang)
        {
            translations = translations.Where(t => t.LanguageCode == lang);
            summaries = summaries.Where(t => t.LanguageCode == lang);
        }

        // Hai bảng cùng hình dạng dòng để gộp trong CSDL rồi phân trang một lần.
        var rows = query.Kind switch
        {
            TranslationKind.TRANSLATION => ProjectTranslations(translations),
            TranslationKind.SUMMARY => ProjectSummaries(summaries),
            _ => ProjectTranslations(translations).Concat(ProjectSummaries(summaries)),
        };
        var page = await rows.OrderByDescending(r => r.RequestedAt).ThenBy(r => r.HeritageId).ThenBy(r => r.Kind).ThenBy(r => r.Language)
            .ToPagedAsync(query.Page, query.PageSize, ct);

        var items = page.Items.Select(r => new TranslationQueueItemDto(
            r.HeritageId, r.HeritageName ?? string.Empty, r.Slug ?? string.Empty, r.Kind == 0 ? TranslationKind.TRANSLATION : TranslationKind.SUMMARY,
            Languages.Tag(r.Language), Languages.ViLabel(r.Language), r.Status, r.IsOutdated, r.RequestedAt, r.ReviewedAt)).ToList();
        return new PagedResult<TranslationQueueItemDto>(items, page.Page, page.PageSize, page.TotalItems, page.TotalPages);
    }

    private static IQueryable<QueueRow> ProjectTranslations(IQueryable<HeritageTranslation> q) => q.Select(t => new QueueRow
    {
        HeritageId = t.HeritageId, HeritageName = t.Heritage!.Name, Slug = t.Heritage.Slug, Kind = 0, Language = t.LanguageCode,
        Status = t.Status, IsOutdated = t.IsOutdated, RequestedAt = t.UpdatedAt ?? t.CreatedAt, ReviewedAt = t.ReviewedAt,
    });

    private static IQueryable<QueueRow> ProjectSummaries(IQueryable<HeritageSummary> q) => q.Select(t => new QueueRow
    {
        HeritageId = t.HeritageId, HeritageName = t.Heritage!.Name, Slug = t.Heritage.Slug, Kind = 1, Language = t.LanguageCode,
        Status = t.Status, IsOutdated = t.IsOutdated, RequestedAt = t.UpdatedAt ?? t.CreatedAt, ReviewedAt = t.ReviewedAt,
    });

    private sealed class QueueRow
    {
        public long HeritageId { get; set; }
        public string? HeritageName { get; set; }
        public string? Slug { get; set; }
        public int Kind { get; set; }
        public LanguageCode Language { get; set; }
        public TranslationStatus Status { get; set; }
        public bool IsOutdated { get; set; }
        public DateTime? RequestedAt { get; set; }
        public DateTime? ReviewedAt { get; set; }
    }

    // ---- Xem song song bản gốc và bản dịch ----

    /// <summary>Bản gốc tiếng Việt và bản dịch của một ngôn ngữ (en, ko, zh-Hans, ja) cạnh nhau.</summary>
    public async Task<TranslationDetailDto> GetTranslationAsync(long heritageId, string? language, CancellationToken ct)
    {
        var lang = ParseTarget(language, "xem bản dịch");
        var source = await loader.LoadAsync(heritageId, "xem bản dịch", ct);
        var rows = await loader.LoadRowsAsync(heritageId, lang, ct)
                   ?? throw AppException.NotFound(Messages.NotFound("xem bản dịch", $"bản dịch {Languages.InlineLabel(lang)}"));
        var reviewer = await ReviewerNameAsync(rows.Heritage.ReviewedBy, ct);
        return new TranslationDetailDto(
            heritageId, source.Heritage.Name ?? string.Empty, source.Heritage.Slug ?? string.Empty, Languages.Tag(lang), Languages.ViLabel(lang),
            rows.Heritage.Status, rows.Heritage.IsOutdated, rows.Heritage.ReviewedAt, reviewer, source.ToTextDto(), rows.ToTextDto(source));
    }

    /// <summary>Bản tóm tắt của một ngôn ngữ (vi, en, ko, zh-Hans, ja) kèm nội dung tiếng Việt của di sản và tóm tắt tiếng Việt để đối chiếu.</summary>
    public async Task<SummaryDetailDto> GetSummaryAsync(long heritageId, string? language, CancellationToken ct)
    {
        var lang = ParseAny(language, "xem tóm tắt");
        var source = await loader.LoadAsync(heritageId, "xem tóm tắt", ct);
        var summaries = await db.HeritageSummaries.AsNoTracking()
            .Where(s => s.HeritageId == heritageId && (s.LanguageCode == lang || s.LanguageCode == LanguageCode.vi)).ToListAsync(ct);
        var row = summaries.SingleOrDefault(s => s.LanguageCode == lang)
                  ?? throw AppException.NotFound(Messages.NotFound("xem tóm tắt", $"bản tóm tắt {Languages.InlineLabel(lang)}"));
        var vi = lang == LanguageCode.vi ? null : summaries.SingleOrDefault(s => s.LanguageCode == LanguageCode.vi);
        var reviewer = await ReviewerNameAsync(row.ReviewedBy, ct);
        return new SummaryDetailDto(
            heritageId, source.Heritage.Name ?? string.Empty, source.Heritage.Slug ?? string.Empty, Languages.Tag(lang), Languages.ViLabel(lang),
            row.Status, row.IsOutdated, row.ReviewedAt, reviewer, source.ToTextDto(),
            vi is null ? null : new SummaryTextDto(vi.ShortText, vi.DetailedText), new SummaryTextDto(row.ShortText, row.DetailedText));
    }

    private async Task<string?> ReviewerNameAsync(long? userId, CancellationToken ct) =>
        userId is null ? null : await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.FullName).SingleOrDefaultAsync(ct);

    // ---- Sửa chữ tại màn hình duyệt ----

    /// <summary>
    /// Sửa chữ của bản dịch tiếng Anh. Phần bỏ trống (null) giữ nguyên, chuỗi rỗng xóa phần đó (nội dung công khai lùi về tiếng Việt cho phần ấy). Bản đã duyệt
    /// hoặc đã bị từ chối được sửa thì quay về chờ duyệt; bản Hàn, Trung, Nhật là bản dịch máy nên không sửa tay ở đây.
    /// </summary>
    public async Task<TranslationDetailDto> UpdateTranslationAsync(long heritageId, string? language, UpdateTranslationRequest request, CancellationToken ct)
    {
        const string action = "sửa bản dịch";
        var lang = ParseTarget(language, action);
        if (!Languages.TranslationNeedsReview(lang))
            throw AppException.BusinessRule(Messages.Blocked(action, "Bản tiếng Hàn, Trung, Nhật là bản dịch máy, không sửa tay; hãy dịch lại bằng AI."));
        var source = await loader.LoadAsync(heritageId, action, ct);
        var row = await db.HeritageTranslations.SingleOrDefaultAsync(t => t.HeritageId == heritageId && t.LanguageCode == lang, ct)
                  ?? throw AppException.NotFound(Messages.NotFound(action, "bản dịch tiếng Anh"));
        Validate(request);

        if (request.Name is not null) row.Name = request.Name.Trim();
        if (request.ShortDescription is not null) row.ShortDescription = Trimmed(request.ShortDescription);
        if (request.Content is not null) row.Content = Trimmed(request.Content);
        if (request.Address is not null) row.Address = Trimmed(request.Address);

        if (request.Timelines is { Count: > 0 })
        {
            var existing = await db.TimelineTranslations.Where(t => t.LanguageCode == lang && t.Timeline!.HeritageId == heritageId).ToListAsync(ct);
            foreach (var item in request.Timelines)
            {
                RequireOwned(source.Timelines.Any(t => t.Id == item.Id), "timelines");
                var current = existing.SingleOrDefault(x => x.TimelineId == item.Id);
                if (item.Name is null && item.Description is null) continue;
                if (item.Name is { } name && string.IsNullOrWhiteSpace(name)) { if (current is not null) db.TimelineTranslations.Remove(current); continue; }
                if (current is null)
                {
                    if (string.IsNullOrWhiteSpace(item.Name))
                        throw AppException.Validation("timelines", Messages.Required("tên giai đoạn đã dịch"));
                    db.TimelineTranslations.Add(new TimelineTranslation
                    {
                        TimelineId = item.Id, LanguageCode = lang, Name = item.Name.Trim(), Description = Trimmed(item.Description),
                    });
                }
                else
                {
                    if (item.Name is not null) current.Name = item.Name.Trim();
                    if (item.Description is not null) current.Description = Trimmed(item.Description);
                }
            }
        }

        if (request.Events is { Count: > 0 })
        {
            var existing = await db.EventTranslations.Where(t => t.LanguageCode == lang && t.Event!.Timeline!.HeritageId == heritageId).ToListAsync(ct);
            foreach (var item in request.Events)
            {
                RequireOwned(source.Events.Any(e => e.Id == item.Id), "events");
                var current = existing.SingleOrDefault(x => x.EventId == item.Id);
                if (item.Name is null && item.Description is null) continue;
                if (item.Name is { } name && string.IsNullOrWhiteSpace(name)) { if (current is not null) db.EventTranslations.Remove(current); continue; }
                if (current is null)
                {
                    if (string.IsNullOrWhiteSpace(item.Name))
                        throw AppException.Validation("events", Messages.Required("tên sự kiện đã dịch"));
                    db.EventTranslations.Add(new EventTranslation
                    {
                        EventId = item.Id, LanguageCode = lang, Name = item.Name.Trim(), Description = Trimmed(item.Description),
                    });
                }
                else
                {
                    if (item.Name is not null) current.Name = item.Name.Trim();
                    if (item.Description is not null) current.Description = Trimmed(item.Description);
                }
            }
        }

        if (request.Media is { Count: > 0 })
        {
            var existing = await db.MediaTranslations.Where(t => t.LanguageCode == lang
                && (t.Media!.HeritageId == heritageId || (t.Media.EventId != null && t.Media.Event!.Timeline!.HeritageId == heritageId))).ToListAsync(ct);
            foreach (var item in request.Media)
            {
                RequireOwned(source.Media.Any(m => m.Id == item.Id), "media");
                var current = existing.SingleOrDefault(x => x.MediaId == item.Id);
                if (item.Caption is null) continue;
                if (string.IsNullOrWhiteSpace(item.Caption)) { if (current is not null) db.MediaTranslations.Remove(current); continue; }
                if (current is null) db.MediaTranslations.Add(new MediaTranslation { MediaId = item.Id, LanguageCode = lang, Caption = item.Caption.Trim() });
                else current.Caption = item.Caption.Trim();
            }
        }

        BackToPending(row);
        row.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return await GetTranslationAsync(heritageId, language, ct);
    }

    /// <summary>Sửa chữ của bản tóm tắt tiếng Việt hoặc tiếng Anh; bản đã duyệt hoặc bị từ chối được sửa thì quay về chờ duyệt.</summary>
    public async Task<SummaryDetailDto> UpdateSummaryAsync(long heritageId, string? language, UpdateSummaryRequest request, CancellationToken ct)
    {
        const string action = "sửa tóm tắt";
        var lang = ParseAny(language, action);
        if (!Languages.SummaryNeedsReview(lang))
            throw AppException.BusinessRule(Messages.Blocked(action, "Bản tiếng Hàn, Trung, Nhật là bản dịch máy, không sửa tay; hãy dịch lại bằng AI."));
        await loader.LoadAsync(heritageId, action, ct);
        var row = await db.HeritageSummaries.SingleOrDefaultAsync(s => s.HeritageId == heritageId && s.LanguageCode == lang, ct)
                  ?? throw AppException.NotFound(Messages.NotFound(action, $"bản tóm tắt {Languages.InlineLabel(lang)}"));

        if (request.ShortText is not null)
        {
            if (string.IsNullOrWhiteSpace(request.ShortText))
                throw AppException.Validation("shortText", Messages.Required("bản tóm tắt ngắn"));
            if (request.ShortText.Trim().Length > TranslationRules.SummaryShortMaxLength)
                throw AppException.Validation("shortText", Messages.Invalid("Bản tóm tắt ngắn", $"tối đa {TranslationRules.SummaryShortMaxLength} ký tự"));
            row.ShortText = request.ShortText.Trim();
        }
        if (request.DetailedText is not null)
        {
            if (string.IsNullOrWhiteSpace(request.DetailedText))
                throw AppException.Validation("detailedText", Messages.Required("bản tóm tắt chi tiết"));
            row.DetailedText = request.DetailedText.Trim();
        }

        BackToPending(row);
        row.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return await GetSummaryAsync(heritageId, language, ct);
    }

    private static void BackToPending(HeritageTranslation row)
    {
        if (row.Status is TranslationStatus.APPROVED or TranslationStatus.REJECTED)
        {
            row.Status = TranslationStatus.PENDING;
            row.ReviewedBy = null;
            row.ReviewedAt = null;
        }
    }

    private static void BackToPending(HeritageSummary row)
    {
        if (row.Status is TranslationStatus.APPROVED or TranslationStatus.REJECTED)
        {
            row.Status = TranslationStatus.PENDING;
            row.ReviewedBy = null;
            row.ReviewedAt = null;
        }
    }

    private static void Validate(UpdateTranslationRequest r)
    {
        if (r.Name is not null && string.IsNullOrWhiteSpace(r.Name)) throw AppException.Validation("name", Messages.Required("tên di sản đã dịch"));
        Max(r.Name, TranslationRules.NameMaxLength, "name", "Tên di sản đã dịch");
        Max(r.ShortDescription, TranslationRules.ShortDescriptionMaxLength, "shortDescription", "Mô tả ngắn đã dịch");
        Max(r.Address, TranslationRules.AddressMaxLength, "address", "Địa chỉ đã dịch");
        foreach (var t in r.Timelines ?? []) Max(t.Name, TranslationRules.NameMaxLength, "timelines", "Tên giai đoạn đã dịch");
        foreach (var e in r.Events ?? []) Max(e.Name, TranslationRules.NameMaxLength, "events", "Tên sự kiện đã dịch");
        foreach (var m in r.Media ?? []) Max(m.Caption, TranslationRules.CaptionMaxLength, "media", "Chú thích ảnh đã dịch");
    }

    private static void Max(string? value, int max, string field, string label)
    {
        if (value is not null && value.Trim().Length > max)
            throw AppException.Validation(field, Messages.Invalid(label, $"tối đa {max} ký tự"));
    }

    private static void RequireOwned(bool owned, string field)
    {
        if (!owned) throw AppException.Validation(field, Messages.Invalid("Mã phần chữ", "không thuộc di sản này"));
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---- Duyệt, từ chối ----

    /// <summary>Duyệt bản dịch, tóm tắt hoặc cả hai của một ngôn ngữ: hiện công khai, ghi người duyệt, thời điểm và nhật ký TRANSLATION_APPROVED.</summary>
    public Task<ReviewResultDto> ApproveAsync(Actor actor, long heritageId, string? language, ApproveTranslationRequest request, string correlationId,
        CancellationToken ct) => ReviewAsync(actor, heritageId, language, request.Kind, approve: true, reason: null, correlationId, ct);

    /// <summary>Từ chối bản dịch, tóm tắt hoặc cả hai (yêu cầu dịch lại): ngừng hiện, ghi nhật ký TRANSLATION_REJECTED kèm lý do nếu có.</summary>
    public Task<ReviewResultDto> RejectAsync(Actor actor, long heritageId, string? language, RejectTranslationRequest request, string correlationId,
        CancellationToken ct) => ReviewAsync(actor, heritageId, language, request.Kind, approve: false, request.Reason, correlationId, ct);

    private async Task<ReviewResultDto> ReviewAsync(Actor actor, long heritageId, string? language, ReviewScope? scope, bool approve, string? reason,
        string correlationId, CancellationToken ct)
    {
        if (actor.Role != RoleCode.ADMIN) throw AppException.Forbidden(Messages.Forbidden);
        var action = approve ? "duyệt bản dịch" : "từ chối bản dịch";
        var lang = ParseAny(language, action);
        if (lang != LanguageCode.vi && !Languages.TranslationNeedsReview(lang))
            throw AppException.BusinessRule(Messages.Blocked(action, "Bản tiếng Hàn, Trung, Nhật là bản dịch máy, luôn kèm nhãn và không có bước duyệt."));
        if (scope is null || !Enum.IsDefined(scope.Value))
            throw AppException.Validation("kind", Messages.Invalid("Đối tượng", "chỉ nhận TRANSLATION, SUMMARY hoặc BOTH"));
        if (scope == ReviewScope.TRANSLATION && lang == LanguageCode.vi)
            throw AppException.Validation("kind", Messages.Invalid("Đối tượng", "tiếng Việt là bản gốc, chỉ có bản tóm tắt để duyệt"));
        var trimmedReason = Trimmed(reason);
        if (trimmedReason is { Length: > TranslationRules.RejectReasonMaxLength })
            throw AppException.Validation("reason", Messages.Invalid("Lý do", $"tối đa {TranslationRules.RejectReasonMaxLength} ký tự"));

        var heritage = await db.Heritages.AsNoTracking().Where(h => h.Id == heritageId && h.DeletedAt == null).Select(h => new { h.Id, h.Name }).SingleOrDefaultAsync(ct)
                       ?? throw AppException.NotFound(Messages.NotFound(action, "di sản"));

        var wantsTranslation = scope != ReviewScope.SUMMARY && lang != LanguageCode.vi;
        var wantsSummary = scope != ReviewScope.TRANSLATION;
        var translation = wantsTranslation
            ? await db.HeritageTranslations.SingleOrDefaultAsync(t => t.HeritageId == heritageId && t.LanguageCode == lang, ct) : null;
        var summary = wantsSummary
            ? await db.HeritageSummaries.SingleOrDefaultAsync(s => s.HeritageId == heritageId && s.LanguageCode == lang, ct) : null;

        // Từng đối tượng được chọn phải có và duyệt được (chờ duyệt) hoặc từ chối được (chờ duyệt, hoặc đã duyệt để gỡ khỏi trang công khai);
        // chọn cả hai thì bỏ qua đối tượng không có hoặc không ở trạng thái đó.
        var both = scope == ReviewScope.BOTH;
        if (!both)
        {
            if (wantsTranslation && translation is null)
                throw AppException.NotFound(Messages.NotFound(action, $"bản dịch {Languages.InlineLabel(lang)}"));
            if (wantsSummary && !wantsTranslation && summary is null)
                throw AppException.NotFound(Messages.NotFound(action, $"bản tóm tắt {Languages.InlineLabel(lang)}"));
        }
        if (translation is not null && !Reviewable(translation.Status, approve))
        {
            if (!both) throw NotReviewable(action, "Bản dịch", translation.Status);
            translation = null;
        }
        if (summary is not null && !Reviewable(summary.Status, approve))
        {
            if (!both) throw NotReviewable(action, "Bản tóm tắt", summary.Status);
            summary = null;
        }
        if (translation is null && summary is null)
            throw AppException.BusinessRule(Messages.Blocked(action, approve
                ? "Không có bản nào đang chờ duyệt ở ngôn ngữ này."
                : "Không có bản nào chờ duyệt hoặc đã duyệt để từ chối ở ngôn ngữ này."));

        var now = Now;
        var labels = new List<string>();
        if (translation is not null)
        {
            Decide(translation, actor, approve, now);
            Record(approve, AuditTargets.HeritageTranslations, translation.Id, actor, heritageId, heritage.Name, lang, "TRANSLATION", trimmedReason);
            labels.Add("bản dịch");
        }
        if (summary is not null)
        {
            Decide(summary, actor, approve, now);
            Record(approve, AuditTargets.HeritageSummaries, summary.Id, actor, heritageId, heritage.Name, lang, "SUMMARY", trimmedReason);
            labels.Add("tóm tắt");
        }
        await db.SaveChangesAsync(ct);

        // Duyệt bản dịch tiếng Anh: dịch lại tiếng Hàn, Trung, Nhật từ bản đã duyệt. Lỗi ở bước này không làm mất lần duyệt.
        var refreshed = false;
        string? warning = null;
        if (approve && translation is not null && lang == LanguageCode.en)
        {
            var outcome = await RefreshMachineTranslationsAsync(actor, heritageId, correlationId, ct);
            refreshed = outcome == true;
            if (outcome == false) warning = TranslationMessages.MachineRefreshFailed;
        }

        return new ReviewResultDto(await BuildOverviewAsync(heritageId, ct), refreshed, warning);
    }

    /// <summary>Tên đối tượng vừa duyệt hoặc từ chối cho thông điệp trả về, vd "bản dịch và tóm tắt tiếng Anh".</summary>
    public static string ReviewLabel(ReviewScope scope, LanguageCode language) => (scope switch
    {
        ReviewScope.TRANSLATION => "bản dịch",
        ReviewScope.SUMMARY => "tóm tắt",
        _ => "bản dịch và tóm tắt",
    }) + " " + Languages.InlineLabel(language);

    /// <summary>Duyệt chỉ áp cho bản đang chờ duyệt; từ chối áp cho bản chờ duyệt và cả bản đã duyệt (gỡ một bản đã hiện mà còn sai khỏi trang công khai).</summary>
    private static bool Reviewable(TranslationStatus status, bool approve) =>
        status == TranslationStatus.PENDING || (!approve && status == TranslationStatus.APPROVED);

    private static AppException NotReviewable(string action, string what, TranslationStatus status) =>
        AppException.BusinessRule(Messages.Blocked(action, status switch
        {
            TranslationStatus.APPROVED => $"{what} này đã được duyệt.",
            TranslationStatus.REJECTED => $"{what} này đã bị từ chối. Hãy dịch lại bằng AI hoặc sửa nội dung để đưa về chờ duyệt.",
            _ => $"{what} này không có bước duyệt.",
        }));

    private static void Decide(HeritageTranslation row, Actor actor, bool approve, DateTime now)
    {
        row.Status = approve ? TranslationStatus.APPROVED : TranslationStatus.REJECTED;
        row.ReviewedBy = actor.UserId;
        row.ReviewedAt = now;
    }

    private static void Decide(HeritageSummary row, Actor actor, bool approve, DateTime now)
    {
        row.Status = approve ? TranslationStatus.APPROVED : TranslationStatus.REJECTED;
        row.ReviewedBy = actor.UserId;
        row.ReviewedAt = now;
    }

    private void Record(bool approve, string target, long rowId, Actor actor, long heritageId, string? heritageName, LanguageCode language, string kind, string? reason) =>
        audit.Record(approve ? AuditAction.TRANSLATION_APPROVED : AuditAction.TRANSLATION_REJECTED, target, rowId, actor,
            newValue: new { heritageId, heritage = heritageName, language = Languages.Tag(language), kind, reason });

    /// <summary>
    /// Dịch lại bản Hàn, Trung, Nhật đã có của di sản từ bản tiếng Anh vừa duyệt (chỉ phần bản dịch nội dung; tóm tắt các ngôn ngữ này dịch từ tóm tắt tiếng Việt khi bấm dịch).
    /// Trả null khi không có bản nào để làm mới, true khi làm mới xong, false khi Dịch vụ AI lỗi (bản cũ giữ nguyên).
    /// </summary>
    private async Task<bool?> RefreshMachineTranslationsAsync(Actor actor, long heritageId, string correlationId, CancellationToken ct)
    {
        var existing = await db.HeritageTranslations.AsNoTracking()
            .Where(t => t.HeritageId == heritageId && (t.LanguageCode == LanguageCode.ko || t.LanguageCode == LanguageCode.zh || t.LanguageCode == LanguageCode.ja))
            .Select(t => t.LanguageCode).ToListAsync(ct);
        if (existing.Count == 0) return null;

        var source = await loader.LoadAsync(heritageId, "dịch lại bản dịch máy", ct);
        var english = await loader.LoadRowsAsync(heritageId, LanguageCode.en, ct);
        if (english is null || english.Heritage.Status != TranslationStatus.APPROVED) return null;
        var englishText = english.ToAiText(source);

        var jobs = new List<(LanguageCode Language, AiGeneration Generation, AiTranslateRequest Request)>();
        foreach (var language in existing.OrderBy(l => l))
        {
            var generation = await StartAsync(actor, heritageId, AiTaskType.HERITAGE_TRANSLATION,
                new { target = Languages.Tag(language), contentLanguage = Languages.Tag(LanguageCode.en), content = englishText },
                new { language = Languages.Tag(language), contentLanguage = Languages.Tag(LanguageCode.en), includesSummary = false, refresh = true }, ct);
            jobs.Add((language, generation, new AiTranslateRequest(generation.Id, heritageId, Languages.Tag(language), Languages.Tag(LanguageCode.en),
                englishText, null, null)));
        }

        AiAttempt<AiTranslateResult>[] results;
        try
        {
            results = await Task.WhenAll(jobs.Select(j => ai.TranslateAsync(j.Request, correlationId, ct)));
        }
        catch (OperationCanceledException)
        {
            foreach (var job in jobs)
                await CloseAsync(job.Generation, AiTaskStatus.FAILED, "Yêu cầu bị hủy trước khi dịch vụ AI trả lời.", null, null, CancellationToken.None);
            throw;
        }

        var failed = false;
        for (var i = 0; i < jobs.Count; i++)
        {
            if (results[i].Succeeded)
            {
                await CloseAsync(jobs[i].Generation, AiTaskStatus.SUCCEEDED, null, ai.ModelName, LessonJson.Serialize(results[i].Value!), ct);
            }
            else
            {
                failed = true;
                await CloseAsync(jobs[i].Generation, AiTaskStatus.FAILED, results[i].Error, null, null, ct);
            }
        }
        if (failed) return false;

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            for (var i = 0; i < jobs.Count; i++)
                await UpsertTranslationAsync(source, jobs[i].Language, results[i].Value!.Content, jobs[i].Generation.Id, TranslationStatus.MACHINE,
                    english.Heritage.IsOutdated, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Không ghi được bản dịch máy làm mới của di sản {HeritageId}", heritageId);
            return false;
        }
        return true;
    }

    // ---- Mã ngôn ngữ ----

    /// <summary>Mã ngôn ngữ đích của bản dịch nội dung: en, ko, zh-Hans, ja (không có vi, tiếng Việt là bản gốc).</summary>
    private static LanguageCode ParseTarget(string? language, string action)
    {
        var lang = Languages.TryParse(language);
        if (lang is null || lang == LanguageCode.vi)
            throw AppException.Validation("language", Messages.Invalid("Ngôn ngữ", $"không thể {action} cho \"{language}\"; chỉ nhận en, ko, zh-Hans hoặc ja"));
        return lang.Value;
    }

    /// <summary>Mã ngôn ngữ của tóm tắt: vi, en, ko, zh-Hans, ja.</summary>
    private static LanguageCode ParseAny(string? language, string action)
    {
        var lang = Languages.TryParse(language);
        if (lang is null)
            throw AppException.Validation("language", Messages.Invalid("Ngôn ngữ", $"không thể {action} cho \"{language}\"; chỉ nhận vi, en, ko, zh-Hans hoặc ja"));
        return lang.Value;
    }
}
