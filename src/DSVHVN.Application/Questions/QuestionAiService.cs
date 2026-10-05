using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Questions;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DSVHVN.Application.Questions;

/// <summary>
/// AI sinh câu hỏi theo dạng và cấp của lớp từ một bài học (màn hình Sinh và duyệt câu hỏi bằng AI).
/// <list type="bullet">
/// <item>Chỉ giáo viên gọi, từ bài Nháp hoặc Đã xuất bản của chính mình có ít nhất một di sản đang xuất bản; bài Lưu trữ thì không. Không đòi bài đã xuất bản.</item>
/// <item>1-20 câu mỗi lần; dạng câu chọn trong các dạng dùng được ở cấp của lớp (để trống thì lấy mọi dạng của cấp); độ khó chọn được; di sản chọn trong
/// các di sản của bài (để trống thì AI dùng cả bài).</item>
/// <item>Trường phải có gói hiệu lực và còn lượt AI trong kỳ; kiểm và thêm dòng "đang xử lý" trong một giao dịch có khóa dòng trường. Mỗi lần gọi,
/// kể cả "Tạo lại", là một lượt, không phụ thuộc số câu sinh ra.</item>
/// <item>Mỗi câu AI trả về phải đạt quy tắc Cấu trúc câu hỏi và Dạng câu hỏi và cấp phù hợp (chấm thử đáp án chuẩn đúng đủ), gắn một di sản của bài và cấp
/// của lớp, ảnh chỉ chọn trong ảnh có sẵn của di sản; câu không đạt bị bỏ và kết quả báo số câu nhận được.</item>
/// <item>Câu vào ngân hàng ở trạng thái chờ duyệt, người tạo là giáo viên yêu cầu, gắn lần gọi AI. AI không tự duyệt.</item>
/// <item>Chờ tối đa 45 giây mỗi lần, thử lại tối đa 2 lần trong cùng dòng nhật ký. Hết lượt thử thì dòng chuyển FAILED, không tính lượt, không có câu nào được thêm.</item>
/// </list>
/// </summary>
public sealed class QuestionAiService(
    IAppDbContext db,
    PlanGuard plans,
    LessonMaterialService materialService,
    AiHeritageInputBuilder inputBuilder,
    QuestionPresenter presenter,
    IQuestionAiClient client,
    IOptions<AiOptions> options,
    TimeProvider clock,
    ILogger<QuestionAiService> logger)
{
    private const string Action = "sinh câu hỏi bằng AI";
    public const int DefaultCount = 5;

    public async Task<QuestionBatchDto> GenerateAsync(Actor actor, long lessonId, GenerateQuestionsRequest request, string correlationId,
        CancellationToken ct)
    {
        if (actor.Role != RoleCode.TEACHER) throw AppException.Forbidden(Messages.Forbidden);

        var count = request.Count ?? DefaultCount;
        if (count is < QuestionRules.AiMinCount or > QuestionRules.AiMaxCount)
            throw AppException.Validation("count", Messages.Invalid("Số câu hỏi", $"từ {QuestionRules.AiMinCount} đến {QuestionRules.AiMaxCount}"));
        var difficulty = request.DifficultyLevel ?? DifficultyLevel.MEDIUM;
        if (!Enum.IsDefined(difficulty)) throw AppException.Validation("difficultyLevel", Messages.Invalid("Độ khó", "chọn dễ, vừa hoặc khó"));

        var lesson = await db.FindLessonAsync(actor, lessonId, Action, tracking: false, ct);
        if (lesson.Status == ContentStatus.ARCHIVED)
            throw AppException.BusinessRule(Messages.Blocked(Action, "Bài học đã lưu trữ."));

        var level = SchoolClass.SchoolLevelOf(lesson.Class!.Grade);
        var types = ResolveTypes(request.Types, level);

        var lessonHeritageIds = await db.LessonHeritages.AsNoTracking()
            .Where(lh => lh.LessonId == lessonId && lh.Heritage!.DeletedAt == null && lh.Heritage.Status == ContentStatus.PUBLISHED)
            .OrderBy(lh => lh.HeritageId).Select(lh => lh.HeritageId).ToListAsync(ct);
        if (lessonHeritageIds.Count == 0)
            throw AppException.BusinessRule(Messages.Blocked(Action, "Bài học cần ít nhất một di sản đã xuất bản."));
        var heritageIds = lessonHeritageIds;
        if (request.HeritageId is { } chosen)
        {
            if (!lessonHeritageIds.Contains(chosen))
                throw AppException.Validation("heritageId", Messages.Invalid("Di sản", "phải là một trong các di sản đã xuất bản của bài học"));
            heritageIds = [chosen];
        }

        var orgId = actor.RequireOrganizationId();
        var materials = await materialService.LoadAsync(heritageIds, ct);
        var inputs = await inputBuilder.BuildAsync(heritageIds, materials, ct);

        // Giữ chỗ lượt AI: kiểm gói, hạn mức và thêm dòng "đang xử lý" trong một giao dịch có khóa dòng trường.
        var generation = new AiGeneration
        {
            TaskType = AiTaskType.QUIZ_GENERATION,
            LessonId = lessonId,
            RequestedBy = actor.UserId,
            InputSnapshot = LessonJson.Serialize(new { gradeLevel = level, count, difficulty, types, heritages = inputs }),
            Params = LessonJson.Serialize(new { gradeLevel = level, count, difficulty, types, heritageIds }),
            Status = AiTaskStatus.PROCESSING,
        };
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.LockOrganizationAsync(orgId, ct);
            await plans.EnsureAiAvailableAsync(orgId, Action, ct);
            db.AiGenerations.Add(generation);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var aiRequest = new AiQuestionRequest(generation.Id, lessonId, level, count, difficulty, types, inputs);
        (AiQuestionResult Raw, List<Question> Accepted, int Dropped)? outcome = null;
        var error = "Dịch vụ AI không phản hồi.";
        var attempts = 1 + Math.Max(0, options.Value.MaxRetries);
        try
        {
            for (var attempt = 1; attempt <= attempts && outcome is null; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(options.Value.AttemptTimeout);
                try
                {
                    var raw = await client.GenerateQuestionsAsync(aiRequest, correlationId, timeout.Token);
                    var (accepted, dropped) = Normalize(raw, aiRequest, inputs);
                    outcome = (raw, accepted, dropped);
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
            Close(generation, AiTaskStatus.FAILED, "Yêu cầu bị hủy trước khi dịch vụ AI trả lời.", null, null);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        if (outcome is not { } done)
        {
            Close(generation, AiTaskStatus.FAILED, error, null, null);
            await db.SaveChangesAsync(ct);
            throw new AppException(503, Messages.AiUnavailable);
        }

        // Câu vào ngân hàng chờ duyệt cùng lúc với việc đóng dòng nhật ký: hoặc có cả hai, hoặc không có gì.
        foreach (var q in done.Accepted)
        {
            q.CreatedBy = actor.UserId;
            q.AiGenerationId = generation.Id;
            q.Source = QuestionSource.AI;
            q.ReviewStatus = ReviewStatus.PENDING;
            db.Questions.Add(q);
        }
        Close(generation, AiTaskStatus.SUCCEEDED, null, done.Raw.Model, LessonJson.Serialize(done.Raw));
        await db.SaveChangesAsync(ct);

        var saved = await db.Questions.AsNoTracking()
            .Include(q => q.Answers).Include(q => q.Heritage).Include(q => q.Reviewer).Include(q => q.Creator!).ThenInclude(u => u.Organization)
            .Where(q => q.AiGenerationId == generation.Id).OrderBy(q => q.Id).ToListAsync(ct);
        var details = new List<QuestionDetailDto>();
        foreach (var q in saved) details.Add(await presenter.DetailAsync(q, QuestionAccessRules.ForTeacher(q, actor.UserId, inQuiz: false), 0, ct));

        var quota = await plans.GetAiQuotaAsync(orgId, ct);
        return new QuestionBatchDto(
            generation.Id, level, SchoolLevels.NameOf(level), count, details.Count, done.Dropped,
            details, quota, done.Raw.Model, Messages.AiDraftReady("câu hỏi", quota.Remaining ?? 0).Text);
    }

    /// <summary>Hạn mức lượt AI của trường trong kỳ gói hiện tại (hiện trên nút Sinh câu hỏi).</summary>
    public Task<AiQuotaDto> GetQuotaAsync(Actor actor, CancellationToken ct)
    {
        if (actor.Role != RoleCode.TEACHER) throw AppException.Forbidden(Messages.Forbidden);
        return plans.GetAiQuotaAsync(actor.RequireOrganizationId(), ct);
    }

    // ---- Kiểm đầu vào ----

    /// <summary>Dạng câu được chọn phải dùng được ở cấp của lớp; để trống thì lấy mọi dạng của cấp.</summary>
    private static List<QuestionType> ResolveTypes(List<QuestionType>? requested, int level)
    {
        var allowed = QuestionTypes.ForLevel(level);
        if (requested is null || requested.Count == 0) return allowed.ToList();
        var types = requested.Distinct().ToList();
        foreach (var type in types)
        {
            if (!Enum.IsDefined(type) || !allowed.Contains(type))
                throw AppException.Validation("types", Messages.Invalid("Dạng câu hỏi",
                    $"dạng \"{QuestionTypes.NameOf(type)}\" không dùng được ở lớp cấp {level}; các dạng của cấp {level}: {string.Join(", ", allowed.Select(t => QuestionTypes.NameOf(t)))}"));
        }
        return types;
    }

    // ---- Kiểm kết quả ----

    /// <summary>
    /// Kiểm từng câu AI trả về: dạng nằm trong các dạng được chọn, gắn một di sản đã gửi, ảnh chỉ trong ảnh có sẵn của di sản đó, đạt quy tắc cấu trúc
    /// (cấp phù hợp là cấp của lớp, độ khó là độ khó đã chọn), không trùng câu đã nhận. Câu không đạt bị bỏ; nhận tối đa số câu yêu cầu.
    /// Không còn câu nào là lỗi đầu ra (thử lại như lỗi mạng).
    /// </summary>
    private static (List<Question> Accepted, int Dropped) Normalize(AiQuestionResult raw, AiQuestionRequest request, IReadOnlyList<AiHeritageInput> inputs)
    {
        var imagesOf = inputs.ToDictionary(h => h.HeritageId, h => (IReadOnlySet<string>)h.Images.Select(i => i.Url).ToHashSet(StringComparer.Ordinal));
        var accepted = new List<Question>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dropped = 0;
        foreach (var generated in raw.Questions)
        {
            if (accepted.Count >= request.Count) { dropped++; continue; }
            if (!request.Types.Contains(generated.QuestionType) || !imagesOf.TryGetValue(generated.HeritageId, out var library)) { dropped++; continue; }

            var built = QuestionBuilder.Build(generated.ToSaveRequest(request.GradeLevel, request.Difficulty), library);
            if (!built.IsValid || !seen.Add(Signature(built.Question!))) { dropped++; continue; }
            accepted.Add(built.Question!);
        }
        if (accepted.Count == 0)
            throw new AiServiceException("Dịch vụ AI không trả câu hỏi nào đạt quy tắc.", retryable: true);
        return (accepted, dropped);
    }

    /// <summary>Dấu vân của câu (dạng, di sản, lời câu, hình, tư liệu và toàn bộ đáp án) để nhận mỗi câu một lần.</summary>
    private static string Signature(Question q) => string.Join('|', q.QuestionType, q.HeritageId, q.Content, q.ImageUrl, q.Passage,
        string.Join('~', q.Answers.OrderBy(a => a.SortOrder).Select(a => $"{a.Content}/{a.ImageUrl}/{a.MatchContent}/{a.IsCorrect}/{a.BlankIndex}")));

    private void Close(AiGeneration generation, AiTaskStatus status, string? error, string? model, string? rawOutput)
    {
        generation.Status = status;
        generation.Model = model;
        generation.RawOutput = rawOutput;
        generation.ErrorMessage = error is null ? null : error.Length > 1000 ? error[..1000] : error;
        generation.CompletedAt = clock.GetUtcNow().UtcDateTime;
    }
}
