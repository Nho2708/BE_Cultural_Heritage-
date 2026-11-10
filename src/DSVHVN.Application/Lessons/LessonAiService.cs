using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DSVHVN.Application.Lessons;

/// <summary>
/// AI soạn nội dung bài học đúng cấp của lớp (màn hình Soạn bài học, nút "Soạn nội dung bằng AI" và "Tạo lại").
/// <list type="bullet">
/// <item>Chỉ giáo viên gọi, từ bài Nháp hoặc Đã xuất bản của chính mình có ít nhất một di sản đang xuất bản; bài Lưu trữ thì không.</item>
/// <item>Trường phải có gói hiệu lực và còn lượt AI trong kỳ. Việc kiểm và thêm dòng "đang xử lý" nằm trong một giao dịch có khóa dòng trường
/// nên hai giáo viên bấm cùng lúc không vượt hạn mức; mỗi lần gọi, kể cả "Tạo lại", là một lượt.</item>
/// <item>Dữ liệu gửi sang dịch vụ AI chỉ gồm nội dung các di sản của bài, cấp học của lớp, ảnh có sẵn và nội dung hiện có của bài, lưu nguyên ở
/// <c>input_snapshot</c>; không có họ tên, mã học sinh, tên lớp, email.</item>
/// <item>Mỗi lần gọi chờ tối đa 45 giây, thử lại tối đa 2 lần trong cùng dòng nhật ký. Hết lượt thử thì dòng chuyển FAILED kèm lỗi, không tính
/// lượt, nội dung bài giữ nguyên.</item>
/// <item>Kết quả luôn là bản nháp trả cho giáo viên xem và sửa; AI không tự lưu, không tự xuất bản.</item>
/// <item>Giáo viên có thể ghi thêm yêu cầu cho AI (<c>instructions</c>, gửi sang Dịch vụ AI thành <c>teacherNote</c>) và viết lại riêng MỘT phần của bài
/// (<see cref="RewriteSectionAsync"/>): cùng gói, hạn mức, thử lại và nhật ký như soạn bài, mỗi lần là một lượt AI.</item>
/// </list>
/// </summary>
public sealed class LessonAiService(
    IAppDbContext db,
    RequestValidator validator,
    PlanGuard plans,
    LessonMaterialService materialService,
    AiHeritageInputBuilder inputBuilder,
    ILessonAiClient client,
    IOptions<AiOptions> options,
    TimeProvider clock,
    ILogger<LessonAiService> logger)
{
    private const string Action = "soạn nội dung bằng AI";

    /// <summary>Lượt AI đã dùng, tối đa và còn lại của trường trong kỳ gói hiện tại (hiện trên nút gọi AI).</summary>
    public Task<AiQuotaDto> GetQuotaAsync(Actor actor, CancellationToken ct)
    {
        if (actor.Role != RoleCode.TEACHER) throw AppException.Forbidden(Messages.Forbidden);
        return plans.GetAiQuotaAsync(actor.RequireOrganizationId(), ct);
    }

    public async Task<LessonDraftDto> ComposeAsync(Actor actor, long lessonId, ComposeLessonRequest request, string correlationId,
        CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var lesson = await db.FindLessonAsync(actor, lessonId, Action, tracking: false, ct);
        if (lesson.Status == ContentStatus.ARCHIVED)
            throw AppException.BusinessRule(Messages.Blocked(Action, "Bài học đã lưu trữ."));

        var orgId = actor.RequireOrganizationId();
        var level = SchoolClass.SchoolLevelOf(lesson.Class!.Grade);
        var frame = LessonFrames.For(level);
        var length = request.Length ?? LessonLength.MEDIUM;

        var heritageIds = await db.LessonHeritages.AsNoTracking()
            .Where(lh => lh.LessonId == lessonId && lh.Heritage!.DeletedAt == null && lh.Heritage.Status == ContentStatus.PUBLISHED)
            .OrderBy(lh => lh.HeritageId).Select(lh => lh.HeritageId).ToListAsync(ct);
        if (heritageIds.Count == 0)
            throw AppException.BusinessRule(Messages.Blocked(Action, "Bài học cần ít nhất một di sản đã xuất bản."));

        var materials = await materialService.LoadAsync(heritageIds, ct);
        var inputs = await inputBuilder.BuildAsync(heritageIds, materials, ct);
        var currentContent = lesson.Summary ?? string.Empty;
        var instructions = ValidationRules.TrimToNull(request.Instructions);

        // Giữ chỗ lượt AI: kiểm gói, hạn mức và thêm dòng "đang xử lý" trong một giao dịch có khóa dòng trường.
        var generation = new AiGeneration
        {
            TaskType = AiTaskType.LESSON_SUMMARY,
            LessonId = lessonId,
            RequestedBy = actor.UserId,
            InputSnapshot = LessonJson.Serialize(new { gradeLevel = level, length, instructions, heritages = inputs, currentContent }),
            Params = LessonJson.Serialize(new { gradeLevel = level, length, instructions }),
            Status = AiTaskStatus.PROCESSING,
        };
        await ReserveAsync(generation, orgId, Action, ct);

        var aiRequest = new AiComposeRequest(generation.Id, lessonId, level, length, inputs, currentContent, instructions ?? string.Empty);
        var result = await CallAsync(generation, lessonId, token => client.ComposeLessonAsync(aiRequest, correlationId, token),
            raw => Normalize(raw, aiRequest, frame), ct);

        await CloseAsync(generation, AiTaskStatus.SUCCEEDED, null, result.Model, LessonJson.Serialize(result), ct);

        var sections = ToSections(result, level, materials);
        var views = sections.Select(LessonMapper.ToView).ToList();
        var check = LessonChecks.Evaluate(level, new LessonContent(lesson.Title, heritageIds.Count, result.Content, result.KeyPoints, views));
        var warnings = check.Warnings.ToList();
        if (!check.IsReady) warnings.Add($"Để xuất bản, bài còn thiếu: {string.Join(", ", check.Missing)}.");

        var quota = await plans.GetAiQuotaAsync(orgId, ct);
        return new LessonDraftDto(
            generation.Id, level, length, LessonFrames.LabelOf(length), result.Content, result.KeyPoints,
            sections.Select(s => LessonMapper.ToDto(s, materials)).ToList(),
            LessonChecks.MainWordCount(result.Content, views.Where(v => frame.Allows(v.Type))), warnings, result.Model, quota,
            Messages.AiDraftReady("nội dung bài học", quota.Remaining ?? 0).Text);
    }

    // ---- Viết lại một phần ----

    /// <summary>
    /// Viết lại MỘT phần của bài bằng AI (nút "Viết lại bằng AI" trên một phần ở Soạn bài học): nhận chữ đang sửa trong biểu mẫu (chưa cần lưu), trả bản mới để giáo viên
    /// xem cạnh bản cũ, không lưu vào bài. Loại phần phải thuộc khung của cấp lớp và không phải đoạn trích tư liệu gốc (phải nguyên văn); năm chỉ với mốc thời gian.
    /// Cùng điều kiện với soạn bài (bài của giáo viên, Nháp hoặc Đã xuất bản, có di sản đang xuất bản, gói hiệu lực, còn lượt AI); mỗi lần là một lượt AI (dòng nhật ký
    /// <c>LESSON_SECTION_REWRITE</c> gắn bài học nên tự tính vào hạn mức); chờ tối đa 45 giây, thử lại tối đa 2 lần, lỗi thì dòng FAILED, không tính lượt, 503.
    /// Backend kiểm lại chữ trả về (không rỗng; vượt giới hạn của loại phần chỉ thành cảnh báo).
    /// </summary>
    public async Task<LessonSectionRewriteDto> RewriteSectionAsync(Actor actor, long lessonId, RewriteSectionRequest request, string correlationId,
        CancellationToken ct)
    {
        const string action = "viết lại phần bài học bằng AI";
        await validator.EnsureValidAsync(request, ct);
        var lesson = await db.FindLessonAsync(actor, lessonId, action, tracking: false, ct);
        if (lesson.Status == ContentStatus.ARCHIVED)
            throw AppException.BusinessRule(Messages.Blocked(action, "Bài học đã lưu trữ."));

        var orgId = actor.RequireOrganizationId();
        var level = SchoolClass.SchoolLevelOf(lesson.Class!.Grade);
        var type = request.SectionType!.Value;
        if (type == LessonSectionType.SOURCE_EXCERPT)
            throw AppException.Validation("sectionType",
                Messages.Invalid("Loại phần", "đoạn trích tư liệu gốc phải nguyên văn nên không viết lại bằng AI"));
        if (!SectionRewriteRules.CanRewrite(level, type))
            throw AppException.Validation("sectionType",
                Messages.Invalid("Loại phần", $"\"{LessonFrames.LabelOf(type)}\" không dùng được ở bài của lớp cấp {level}"));
        if (request.Year is { } year)
        {
            if (type != LessonSectionType.MILESTONE)
                throw AppException.Validation("year", Messages.Invalid("Năm", "chỉ dùng với mốc thời gian"));
            if (year < LessonRules.MinMilestoneYear || year > DateTime.UtcNow.Year + 1)
                throw AppException.Validation("year", Messages.Invalid("Năm của mốc", $"phải từ {LessonRules.MinMilestoneYear} (năm âm là trước Công nguyên) đến năm hiện tại"));
        }

        var heritageIds = await db.LessonHeritages.AsNoTracking()
            .Where(lh => lh.LessonId == lessonId && lh.Heritage!.DeletedAt == null && lh.Heritage.Status == ContentStatus.PUBLISHED)
            .OrderBy(lh => lh.HeritageId).Select(lh => lh.HeritageId).ToListAsync(ct);
        if (heritageIds.Count == 0)
            throw AppException.BusinessRule(Messages.Blocked(action, "Bài học cần ít nhất một di sản đã xuất bản."));

        var materials = await materialService.LoadAsync(heritageIds, ct);
        var inputs = await inputBuilder.BuildAsync(heritageIds, materials, ct);
        var currentText = request.Text!.Trim();
        var title = ValidationRules.TrimToNull(request.Title);
        var instructions = ValidationRules.TrimToNull(request.Instructions);
        var summary = lesson.Summary ?? string.Empty;
        var lessonContent = summary.Length > SectionRewriteRules.LessonContentMaxLength ? summary[..SectionRewriteRules.LessonContentMaxLength] : summary;

        var generation = new AiGeneration
        {
            TaskType = AiTaskType.LESSON_SECTION_REWRITE,
            LessonId = lessonId,
            RequestedBy = actor.UserId,
            InputSnapshot = LessonJson.Serialize(new
            {
                gradeLevel = level, kind = type, currentText, title, year = request.Year, instructions, lessonContent, heritages = inputs,
            }),
            Params = LessonJson.Serialize(new { gradeLevel = level, kind = type, instructions }),
            Status = AiTaskStatus.PROCESSING,
        };
        await ReserveAsync(generation, orgId, action, ct);

        var aiRequest = new AiRewriteSectionRequest(generation.Id, lessonId, level, type, currentText, title, request.Year,
            instructions ?? string.Empty, lessonContent, inputs);
        var result = await CallAsync(generation, lessonId, token => client.RewriteSectionAsync(aiRequest, correlationId, token),
            NormalizeRewrite, ct);

        await CloseAsync(generation, AiTaskStatus.SUCCEEDED, null, result.Model, LessonJson.Serialize(result), ct);

        var warnings = SectionRewriteRules.Check(level, type, result.Text);
        var quota = await plans.GetAiQuotaAsync(orgId, ct);
        return new LessonSectionRewriteDto(generation.Id, type, result.Text, result.Title, warnings, result.Model, quota,
            Messages.AiDraftReady("đoạn viết lại", quota.Remaining ?? 0).Text);
    }

    /// <summary>Chữ rỗng là lỗi đầu ra (thử lại như lỗi mạng); tiêu đề rỗng nghĩa là giữ tiêu đề cũ, tiêu đề dài quá cột thì cắt.</summary>
    private static AiRewriteSectionResult NormalizeRewrite(AiRewriteSectionResult raw)
    {
        if (string.IsNullOrWhiteSpace(raw.Text))
            throw new AiServiceException("Dịch vụ AI trả chữ rỗng.", retryable: true);
        var title = ValidationRules.TrimToNull(raw.Title);
        if (title is { Length: > LessonRules.SectionTitleMaxLength }) title = title[..LessonRules.SectionTitleMaxLength];
        return raw with { Text = raw.Text.Trim(), Title = title };
    }

    // ---- Giữ chỗ lượt AI, gọi dịch vụ AI, đóng dòng nhật ký ----

    /// <summary>Giữ chỗ lượt AI: kiểm gói, hạn mức và thêm dòng "đang xử lý" trong một giao dịch có khóa dòng trường (hai giáo viên bấm cùng lúc không vượt hạn mức).</summary>
    private async Task ReserveAsync(AiGeneration generation, long orgId, string action, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockOrganizationAsync(orgId, ct);
        await plans.EnsureAiAvailableAsync(orgId, action, ct);
        db.AiGenerations.Add(generation);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Gọi Dịch vụ AI chờ tối đa <c>AttemptTimeout</c> mỗi lần, thử lại tối đa <c>MaxRetries</c> lần trong cùng dòng nhật ký, kiểm kết quả bằng <paramref name="normalize"/>.
    /// Hết lượt thử thì đóng dòng FAILED (không tính lượt) và ném 503; người gọi ngắt kết nối giữa chừng thì dòng cũng đóng FAILED.
    /// </summary>
    private async Task<TResult> CallAsync<TRaw, TResult>(AiGeneration generation, long lessonId, Func<CancellationToken, Task<TRaw>> call,
        Func<TRaw, TResult> normalize, CancellationToken ct) where TResult : class
    {
        TResult? result = null;
        string error = "Dịch vụ AI không phản hồi.";
        var attempts = 1 + Math.Max(0, options.Value.MaxRetries);
        try
        {
            for (var attempt = 1; attempt <= attempts && result is null; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(options.Value.AttemptTimeout);
                try
                {
                    result = normalize(await call(timeout.Token));
                }
                catch (AiServiceException ex)
                {
                    error = ex.Message;
                    logger.LogWarning(ex, "Dịch vụ AI lỗi ở lần {Attempt}/{Max} (bài {LessonId})", attempt, attempts, lessonId);
                    if (!ex.Retryable) break;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    error = $"Quá thời gian chờ {options.Value.AttemptTimeout.TotalSeconds:0.##} giây.";
                    logger.LogWarning("Dịch vụ AI quá thời gian chờ ở lần {Attempt}/{Max} (bài {LessonId})", attempt, attempts, lessonId);
                }
                catch (HttpRequestException ex)
                {
                    error = "Không kết nối được dịch vụ AI.";
                    logger.LogWarning(ex, "Dịch vụ AI không kết nối được ở lần {Attempt}/{Max} (bài {LessonId})", attempt, attempts, lessonId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Người gọi ngắt kết nối giữa chừng: dòng nhật ký không được kẹt ở "đang xử lý".
            await CloseAsync(generation, AiTaskStatus.FAILED, "Yêu cầu bị hủy trước khi dịch vụ AI trả lời.", null, null, CancellationToken.None);
            throw;
        }

        if (result is null)
        {
            await CloseAsync(generation, AiTaskStatus.FAILED, error, null, null, ct);
            throw new AppException(503, Messages.AiUnavailable);
        }
        return result;
    }

    // ---- Kiểm kết quả ----

    /// <summary>
    /// Kiểm kết quả theo hợp đồng: nội dung không rỗng, có ý chính, các phần đúng khung của cấp. Ý chính cắt về tối đa 7 ý, mỗi ý 300 ký tự;
    /// hình phải nằm trong danh sách ảnh đã gửi; đoạn trích phải là chuỗi con nguyên văn của nội dung di sản đã gửi và có nguồn. Phần không đạt
    /// bị bỏ; kết quả không rỗng chữ hoặc không có ý chính nào là lỗi đầu ra (thử lại như lỗi mạng).
    /// </summary>
    private static AiComposeResult Normalize(AiComposeResult raw, AiComposeRequest request, LevelFrame frame)
    {
        if (string.IsNullOrWhiteSpace(raw.Content))
            throw new AiServiceException("Dịch vụ AI trả nội dung rỗng.", retryable: true);

        var keyPoints = raw.KeyPoints.Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim().Length > LessonRules.KeyPointMaxLength ? k.Trim()[..LessonRules.KeyPointMaxLength] : k.Trim())
            .Take(LessonRules.AiMaxKeyPoints).ToList();
        if (keyPoints.Count == 0)
            throw new AiServiceException("Dịch vụ AI không trả ý chính nào.", retryable: true);

        var offeredMedia = request.Heritages.SelectMany(h => h.Images).Select(i => i.MediaId).ToHashSet();
        var offeredEvents = request.Heritages.SelectMany(h => h.Timeline).Select(t => t.EventId).ToHashSet();

        var steps = new List<AiComposedStep>();
        foreach (var s in raw.Steps)
        {
            if (!frame.Allows(s.Kind)) continue;
            switch (s.Kind)
            {
                case LessonSectionType.STORY:
                    if (string.IsNullOrWhiteSpace(s.Text)) continue;
                    steps.Add(s with { MediaId = s.MediaId is { } m && offeredMedia.Contains(m) ? m : null });
                    break;
                case LessonSectionType.IMAGE:
                    if (s.MediaId is not { } image || !offeredMedia.Contains(image)) continue;
                    steps.Add(s);
                    break;
                case LessonSectionType.MILESTONE:
                    if (s.Year is null || string.IsNullOrWhiteSpace(s.Text)) continue;
                    steps.Add(s with { EventId = s.EventId is { } e && offeredEvents.Contains(e) ? e : null });
                    break;
                case LessonSectionType.REFLECTION:
                    if (string.IsNullOrWhiteSpace(s.Text)) continue;
                    steps.Add(s);
                    break;
            }
        }

        var quotes = new List<AiSourceQuote>();
        if (frame.Allows(LessonSectionType.SOURCE_EXCERPT))
        {
            var contents = request.Heritages.ToDictionary(h => h.HeritageId, h => h.Content ?? string.Empty);
            foreach (var q in raw.SourceQuotes)
            {
                if (string.IsNullOrWhiteSpace(q.Quote) || string.IsNullOrWhiteSpace(q.Source)) continue;
                if (!contents.TryGetValue(q.HeritageId, out var content) || !content.Contains(q.Quote.Trim(), StringComparison.Ordinal)) continue;
                quotes.Add(q with { Quote = q.Quote.Trim(), Source = q.Source.Trim() });
            }
        }

        return raw with { Content = raw.Content.Trim(), KeyPoints = keyPoints, Steps = steps, SourceQuotes = quotes };
    }

    /// <summary>
    /// Bản nháp thành các phần theo luồng của cấp (chưa lưu): cấp 1 bước câu chuyện rồi hình; cấp 2 hình rồi các mốc theo thứ tự thời gian;
    /// cấp 3 đoạn trích tư liệu rồi câu hỏi gợi suy nghĩ.
    /// </summary>
    private static List<LessonSection> ToSections(AiComposeResult result, int level, LessonMaterials materials)
    {
        string? UrlOf(long? mediaId) => mediaId is { } id ? materials.Images.FirstOrDefault(i => i.MediaId == id)?.Url : null;

        var sections = new List<LessonSection>();
        void Add(LessonSectionType type, string? title, string? body, string? imageUrl = null, int? year = null, long? eventId = null, string? source = null) =>
            sections.Add(new LessonSection
            {
                SectionType = type, Title = title, Body = body, ImageUrl = imageUrl, MilestoneYear = year, EventId = eventId,
                SourceCitation = source, SortOrder = sections.Count + 1,
            });

        IEnumerable<AiComposedStep> Of(LessonSectionType kind) => result.Steps.Where(s => s.Kind == kind);

        switch (level)
        {
            case 1:
                foreach (var s in Of(LessonSectionType.STORY)) Add(LessonSectionType.STORY, s.Title, s.Text!.Trim(), UrlOf(s.MediaId));
                foreach (var s in Of(LessonSectionType.IMAGE)) Add(LessonSectionType.IMAGE, s.Title, s.Text?.Trim(), UrlOf(s.MediaId));
                break;
            case 2:
                foreach (var s in Of(LessonSectionType.IMAGE)) Add(LessonSectionType.IMAGE, s.Title, s.Text?.Trim(), UrlOf(s.MediaId));
                foreach (var s in Of(LessonSectionType.MILESTONE).OrderBy(s => s.Year))
                    Add(LessonSectionType.MILESTONE, s.Title, s.Text!.Trim(), year: s.Year, eventId: s.EventId);
                break;
            default:
                foreach (var q in result.SourceQuotes) Add(LessonSectionType.SOURCE_EXCERPT, null, q.Quote, source: q.Source);
                foreach (var s in Of(LessonSectionType.MILESTONE).OrderBy(s => s.Year))
                    Add(LessonSectionType.MILESTONE, s.Title, s.Text!.Trim(), year: s.Year, eventId: s.EventId);
                foreach (var s in Of(LessonSectionType.REFLECTION)) Add(LessonSectionType.REFLECTION, s.Title, s.Text!.Trim());
                break;
        }
        return sections;
    }

    private async Task CloseAsync(AiGeneration generation, AiTaskStatus status, string? error, string? model, string? rawOutput, CancellationToken ct)
    {
        generation.Status = status;
        generation.Model = model;
        generation.RawOutput = rawOutput;
        generation.ErrorMessage = error is null ? null : error.Length > 1000 ? error[..1000] : error;
        generation.CompletedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }
}
