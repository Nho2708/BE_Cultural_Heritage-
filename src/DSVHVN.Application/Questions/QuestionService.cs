using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Questions;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Questions;

/// <summary>
/// Ngân hàng câu hỏi của giáo viên (màn hình Ngân hàng câu hỏi, Biểu mẫu câu hỏi theo dạng, duyệt câu AI).
/// <list type="bullet">
/// <item>Ngân hàng dùng chung mọi trường: câu đã duyệt hiện cho mọi giáo viên kèm tên người tạo và trường; câu chờ duyệt, bị loại chỉ người tạo thấy
/// (người khác nhận 404 như không tồn tại).</item>
/// <item>Chỉ người tạo sửa, xóa mềm, duyệt, loại câu của mình, và chỉ khi câu chưa nằm trong quiz nào; câu đã nằm trong quiz hoặc của người khác
/// chỉ đọc, muốn sửa thì nhân bản. Câu bị quản trị hệ thống ẩn chỉ đọc và không nhân bản được.</item>
/// <item>Câu tự tạo duyệt ngay; câu AI chờ duyệt. Mọi lần lưu kiểm lại cấu trúc theo dạng, cấp phù hợp, ảnh thuộc thư viện của di sản.</item>
/// </list>
/// </summary>
public sealed class QuestionService(IAppDbContext db, QuestionPresenter presenter, TimeProvider clock)
{
    private const int MaxApproveBatch = 100;

    // ---- Danh sách và chi tiết ----

    /// <summary>Danh sách câu theo ba tab: Dùng chung (mặc định), Của tôi, Chờ duyệt; lọc theo dạng, cấp, độ khó, di sản, trạng thái, người tạo, từ khóa.</summary>
    public async Task<QuestionPageDto> ListAsync(Actor actor, QuestionListQuery query, CancellationToken ct)
    {
        EnsureTeacher(actor);
        if (query.Level is { } level && level is < 1 or > 3)
            throw AppException.Validation("level", Messages.Invalid("Cấp", "từ 1 đến 3"));

        var questions = db.Questions.AsNoTracking().Where(q => q.DeletedAt == null);
        switch (query.Scope ?? QuestionScope.SHARED)
        {
            case QuestionScope.SHARED:
                questions = questions.Where(q => q.ReviewStatus == ReviewStatus.APPROVED);
                if (query.CreatedBy is { } creator) questions = questions.Where(q => q.CreatedBy == creator);
                break;
            case QuestionScope.MINE:
                questions = questions.Where(q => q.CreatedBy == actor.UserId);
                if (query.Status is { } status) questions = questions.Where(q => q.ReviewStatus == status);
                break;
            case QuestionScope.PENDING:
                questions = questions.Where(q => q.CreatedBy == actor.UserId && q.ReviewStatus == ReviewStatus.PENDING);
                break;
        }
        questions = ApplyFilters(questions, query);

        var rows = questions
            .OrderByDescending(q => q.CreatedAt).ThenByDescending(q => q.Id)
            .Select(q => new
            {
                Question = q,
                Creator = q.Creator!.FullName,
                Organization = q.Creator.Organization!.Name,
                Heritage = q.Heritage!.Name,
                Hidden = q.ReviewStatus == ReviewStatus.REJECTED && q.Reviewer!.RoleId == Roles.AdminId,
                Quizzes = db.QuizQuestions.Count(x => x.QuestionId == q.Id && x.Quiz!.DeletedAt == null),
            });
        var page = await rows.ToPagedAsync(query.Page, query.PageSize, ct);
        var items = page.Items.Select(r => QuestionPresenter.Row(
            r.Question, r.Creator, r.Organization, r.Heritage, r.Question.CreatedBy == actor.UserId, r.Hidden, r.Quizzes)).ToList();
        return new QuestionPageDto(items, page.Page, page.PageSize, page.TotalItems, page.TotalPages);
    }

    internal static IQueryable<Question> ApplyFilters(IQueryable<Question> questions, QuestionListQuery query)
    {
        if (query.QuestionType is { } type) questions = questions.Where(q => q.QuestionType == type);
        if (query.Level is { } level)
        {
            var l = (byte)level;
            questions = questions.Where(q => q.LevelMin <= l && q.LevelMax >= l);
        }
        if (query.DifficultyLevel is { } difficulty) questions = questions.Where(q => q.DifficultyLevel == difficulty);
        if (query.HeritageId is { } heritageId) questions = questions.Where(q => q.HeritageId == heritageId);
        if (query.Source is { } source) questions = questions.Where(q => q.Source == source);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            questions = questions.Where(q => q.Content!.Contains(keyword));
        }
        return questions;
    }

    /// <summary>Số đếm các tab: câu dùng chung, của tôi, chờ duyệt, bị loại.</summary>
    public async Task<QuestionCountsDto> CountsAsync(Actor actor, CancellationToken ct)
    {
        EnsureTeacher(actor);
        var live = db.Questions.AsNoTracking().Where(q => q.DeletedAt == null);
        var shared = await live.CountAsync(q => q.ReviewStatus == ReviewStatus.APPROVED, ct);
        var mine = live.Where(q => q.CreatedBy == actor.UserId);
        return new QuestionCountsDto(
            shared,
            await mine.CountAsync(ct),
            await mine.CountAsync(q => q.ReviewStatus == ReviewStatus.PENDING, ct),
            await mine.CountAsync(q => q.ReviewStatus == ReviewStatus.REJECTED, ct));
    }

    /// <summary>Bảng dạng câu theo cấp, để trình soạn gợi ý cấp và chỉ cho chọn dạng của cấp.</summary>
    public static QuestionTypesDto Types() => new(
        QuestionTypes.All.Select(t => new QuestionTypeDto(t.Type, t.Name, t.MinLevel, t.MaxLevel, t.MultiPart)).ToList(),
        Enumerable.Range(1, 3).ToDictionary(l => l, l => QuestionTypes.ForLevel(l)));

    public async Task<QuestionDetailDto> GetAsync(Actor actor, long id, CancellationToken ct)
    {
        EnsureTeacher(actor);
        var q = await FindVisibleAsync(actor, id, "xem câu hỏi", tracking: false, ct);
        return await DetailAsync(actor, q, ct);
    }

    // ---- Tạo, sửa, xóa ----

    /// <summary>Tạo câu thủ công: duyệt ngay (người duyệt là người tạo), gắn một di sản đã xuất bản và cấp phù hợp.</summary>
    public async Task<QuestionDetailDto> CreateAsync(Actor actor, SaveQuestionRequest request, CancellationToken ct)
    {
        EnsureTeacher(actor);
        var q = (await BuildAsync(request, ct)).Question!;
        var now = Now();
        q.CreatedBy = actor.UserId;
        q.Source = QuestionSource.MANUAL;
        q.ReviewStatus = ReviewStatus.APPROVED;
        q.ReviewedBy = actor.UserId;
        q.ReviewedAt = now;
        db.Questions.Add(q);
        await db.SaveChangesAsync(ct);
        return await GetAsync(actor, q.Id, ct);
    }

    /// <summary>
    /// Sửa câu của mình chưa nằm trong quiz nào. Trạng thái giữ nguyên (câu chờ duyệt vẫn chờ duyệt). Không đổi dạng của câu do AI sinh.
    /// </summary>
    public async Task<QuestionDetailDto> UpdateAsync(Actor actor, long id, SaveQuestionRequest request, CancellationToken ct)
    {
        EnsureTeacher(actor);
        var q = await FindVisibleAsync(actor, id, "sửa câu hỏi", tracking: true, ct);
        await EnsureEditableAsync(actor, q, "sửa câu hỏi", ct);
        if (q.Source == QuestionSource.AI && request.QuestionType != q.QuestionType)
            throw AppException.BusinessRule(Messages.Blocked("sửa câu hỏi", "Không đổi được dạng của câu do AI sinh; muốn dạng khác hãy tạo câu mới."));

        var built = (await BuildAsync(request, ct)).Question!;
        db.Answers.RemoveRange(q.Answers.ToList());
        q.QuestionType = built.QuestionType;
        q.HeritageId = built.HeritageId;
        q.Content = built.Content;
        q.Explanation = built.Explanation;
        q.DifficultyLevel = built.DifficultyLevel;
        q.LevelMin = built.LevelMin;
        q.LevelMax = built.LevelMax;
        q.ImageUrl = built.ImageUrl;
        q.Passage = built.Passage;
        q.PassageSource = built.PassageSource;
        foreach (var row in built.Answers)
        {
            row.QuestionId = q.Id;
            db.Answers.Add(row);
        }
        await db.SaveChangesAsync(ct);
        return await GetAsync(actor, id, ct);
    }

    public async Task DeleteAsync(Actor actor, long id, CancellationToken ct)
    {
        EnsureTeacher(actor);
        var q = await FindVisibleAsync(actor, id, "xóa câu hỏi", tracking: true, ct);
        await EnsureEditableAsync(actor, q, "xóa câu hỏi", ct);
        q.DeletedAt = Now();
        await db.SaveChangesAsync(ct);
    }

    // ---- Duyệt, loại, nhân bản ----

    /// <summary>
    /// Duyệt câu chờ duyệt hoặc câu mình đã loại (duyệt lại): kiểm lại cấu trúc theo quy tắc hiện hành, ghi người và thời điểm duyệt.
    /// Câu đã duyệt thì giữ nguyên.
    /// </summary>
    public async Task<QuestionDetailDto> ApproveAsync(Actor actor, long id, CancellationToken ct)
    {
        EnsureTeacher(actor);
        var q = await FindVisibleAsync(actor, id, "duyệt câu hỏi", tracking: true, ct);
        await ApproveCoreAsync(actor, q, ct);
        await db.SaveChangesAsync(ct);
        return await GetAsync(actor, id, ct);
    }

    /// <summary>Duyệt nhiều câu một lần (tối đa 100); câu không duyệt được bị bỏ qua kèm lý do, các câu còn lại vẫn được duyệt.</summary>
    public async Task<ApproveQuestionsResultDto> ApproveManyAsync(Actor actor, ApproveQuestionsRequest request, CancellationToken ct)
    {
        EnsureTeacher(actor);
        var ids = (request.QuestionIds ?? []).Distinct().ToList();
        if (ids.Count == 0) throw AppException.Validation("questionIds", Messages.Required("danh sách câu hỏi cần duyệt"));
        if (ids.Count > MaxApproveBatch)
            throw AppException.Validation("questionIds", Messages.Invalid("Danh sách câu hỏi", $"tối đa {MaxApproveBatch} câu mỗi lần"));

        var approved = new List<long>();
        var skipped = new List<SkippedQuestionDto>();
        foreach (var id in ids)
        {
            try
            {
                var q = await FindVisibleAsync(actor, id, "duyệt câu hỏi", tracking: true, ct);
                await ApproveCoreAsync(actor, q, ct);
                approved.Add(id);
            }
            catch (AppException ex)
            {
                skipped.Add(new SkippedQuestionDto(id, ex.Message));
            }
        }
        if (approved.Count > 0) await db.SaveChangesAsync(ct);
        return new ApproveQuestionsResultDto(approved, skipped,
            skipped.Count == 0 ? Messages.Success($"Duyệt {approved.Count} câu hỏi").Text : $"Đã duyệt {approved.Count} câu, bỏ qua {skipped.Count} câu.");
    }

    /// <summary>Loại câu AI chờ duyệt của mình (REJECTED, ghi người và thời điểm). Câu đã loại sửa được và duyệt lại.</summary>
    public async Task<QuestionDetailDto> RejectAsync(Actor actor, long id, CancellationToken ct)
    {
        EnsureTeacher(actor);
        var q = await FindVisibleAsync(actor, id, "loại câu hỏi", tracking: true, ct);
        EnsureOwner(actor, q);
        if (q.ReviewStatus != ReviewStatus.PENDING)
            throw AppException.BusinessRule(Messages.Blocked("loại câu hỏi", "Chỉ loại được câu đang chờ duyệt."));
        q.ReviewStatus = ReviewStatus.REJECTED;
        q.ReviewedBy = actor.UserId;
        q.ReviewedAt = Now();
        await db.SaveChangesAsync(ct);
        return await GetAsync(actor, id, ct);
    }

    /// <summary>
    /// Nhân bản câu đã duyệt (của mình hoặc dùng chung) thành câu mới của người nhân bản: tự tạo, duyệt ngay, không gắn lần gọi AI,
    /// chép dạng, đáp án, giải thích, độ khó, di sản, cấp phù hợp. Không nhân bản được câu bị quản trị hệ thống ẩn.
    /// </summary>
    public async Task<QuestionDetailDto> DuplicateAsync(Actor actor, long id, CancellationToken ct)
    {
        EnsureTeacher(actor);
        var source = await FindVisibleAsync(actor, id, "nhân bản câu hỏi", tracking: false, ct);
        if (QuestionAccessRules.IsHiddenByAdmin(source))
            throw AppException.BusinessRule(Messages.Blocked("nhân bản câu hỏi", "Câu bị quản trị hệ thống ẩn không nhân bản được; bạn có thể tạo câu mới."));
        if (source.ReviewStatus != ReviewStatus.APPROVED)
            throw AppException.BusinessRule(Messages.Blocked("nhân bản câu hỏi", "Chỉ nhân bản được câu đã duyệt."));

        var copy = (await BuildAsync(QuestionShape.ToRequest(source), ct)).Question!;
        copy.CreatedBy = actor.UserId;
        copy.Source = QuestionSource.MANUAL;
        copy.ReviewStatus = ReviewStatus.APPROVED;
        copy.ReviewedBy = actor.UserId;
        copy.ReviewedAt = Now();
        db.Questions.Add(copy);
        await db.SaveChangesAsync(ct);
        return await GetAsync(actor, copy.Id, ct);
    }

    // ---- Xem trước, chấm thử, kiểm hợp lệ ----

    /// <summary>Câu như học sinh của một cấp thấy (mặc định cấp thấp nhất của câu); cấp phải nằm trong cấp phù hợp của câu.</summary>
    public async Task<QuestionPreviewDto> PreviewAsync(Actor actor, long id, int? level, CancellationToken ct)
    {
        EnsureTeacher(actor);
        var q = await FindVisibleAsync(actor, id, "xem trước câu hỏi", tracking: false, ct);
        return QuestionPreviewBuilder.Build(q, PreviewLevel(q, level));
    }

    /// <summary>Chấm thử một cách làm với câu đã lưu, theo đúng phép chấm của bài kiểm tra (không ghi gì).</summary>
    public async Task<GradeResultDto> TryGradeAsync(Actor actor, long id, QuestionSubmission? submission, CancellationToken ct)
    {
        EnsureTeacher(actor);
        var q = await FindVisibleAsync(actor, id, "chấm thử câu hỏi", tracking: false, ct);
        return QuestionGrader.Grade(q.QuestionType!.Value, q.Answers, submission).ToDto();
    }

    /// <summary>
    /// Kiểm hợp lệ nội dung câu đang soạn mà không lưu: trả lỗi theo trường, số phần chấm, cách học sinh thấy và (nếu gửi kèm cách làm) kết quả chấm thử.
    /// Không trả 400 khi câu chưa hợp lệ để trình soạn hiện lỗi ngay khi gõ.
    /// </summary>
    public async Task<ValidateQuestionResultDto> ValidateAsync(Actor actor, ValidateQuestionRequest request, CancellationToken ct)
    {
        EnsureTeacher(actor);
        if (request.Question is null) throw AppException.Validation("question", Messages.Required("nội dung câu hỏi"));
        var result = await TryBuildAsync(request.Question, ct);
        if (!result.IsValid) return new ValidateQuestionResultDto(false, result.ErrorTexts, 0, null, null);

        var q = result.Question!;
        var type = q.QuestionType!.Value;
        var preview = QuestionPreviewBuilder.Build(q, PreviewLevel(q, request.PreviewLevel));
        var trial = request.Submission is null ? null : QuestionGrader.Grade(type, q.Answers, request.Submission).ToDto();
        return new ValidateQuestionResultDto(true, new Dictionary<string, string>(), QuestionGrader.PartsTotal(type, q.Answers), preview, trial);
    }

    // ---- Nội bộ ----

    private async Task ApproveCoreAsync(Actor actor, Question q, CancellationToken ct)
    {
        EnsureOwner(actor, q);
        if (QuestionAccessRules.IsHiddenByAdmin(q))
            throw AppException.BusinessRule(Messages.Blocked("duyệt câu hỏi", "Câu đã bị quản trị hệ thống ẩn."));
        if (q.ReviewStatus == ReviewStatus.APPROVED) return;

        // Kiểm lại theo quy tắc hiện hành: ảnh còn trong thư viện, di sản còn xuất bản, cấu trúc đúng dạng.
        var check = await TryBuildAsync(QuestionShape.ToRequest(q), ct);
        if (!check.IsValid)
            throw new AppException(422, Messages.Blocked("duyệt câu hỏi", $"Câu hỏi chưa đạt: {check.Errors[0].Value.Text}"), check.ErrorTexts);
        q.ReviewStatus = ReviewStatus.APPROVED;
        q.ReviewedBy = actor.UserId;
        q.ReviewedAt = Now();
    }

    private async Task<QuestionBuildResult> BuildAsync(SaveQuestionRequest request, CancellationToken ct)
    {
        var result = await TryBuildAsync(request, ct);
        return result.IsValid ? result : throw result.ToException();
    }

    private async Task<QuestionBuildResult> TryBuildAsync(SaveQuestionRequest request, CancellationToken ct)
    {
        IReadOnlySet<string> library = new HashSet<string>();
        if (request.HeritageId is > 0 and var heritageId)
        {
            var published = await db.Heritages.AsNoTracking()
                .AnyAsync(h => h.Id == heritageId && h.DeletedAt == null && h.Status == ContentStatus.PUBLISHED, ct);
            if (!published)
                return new QuestionBuildResult
                {
                    Errors = [new("heritageId", Messages.Invalid("Di sản", "phải là di sản đã xuất bản"))],
                };
            library = await presenter.LibraryUrlsAsync(heritageId, ct);
        }
        return QuestionBuilder.Build(request, library);
    }

    private static int PreviewLevel(Question q, int? level)
    {
        var l = level ?? q.LevelMin;
        if (l < q.LevelMin || l > q.LevelMax)
            throw AppException.Validation("level", Messages.Invalid("Cấp", $"câu này dùng được ở cấp {q.LevelMin}-{q.LevelMax}"));
        return l;
    }

    /// <summary>Câu người gọi được thấy: câu đã duyệt chưa xóa của mọi trường, hoặc câu của chính mình. Còn lại như không tồn tại.</summary>
    private async Task<Question> FindVisibleAsync(Actor actor, long id, string action, bool tracking, CancellationToken ct)
    {
        var query = db.Questions.Include(q => q.Answers).Include(q => q.Heritage).Include(q => q.Reviewer)
            .Include(q => q.Creator!).ThenInclude(u => u.Organization).AsQueryable();
        if (!tracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(q => q.Id == id && q.DeletedAt == null
                                                     && (q.ReviewStatus == ReviewStatus.APPROVED || q.CreatedBy == actor.UserId), ct)
               ?? throw AppException.NotFound(Messages.NotFound(action, "câu hỏi"));
    }

    private async Task<QuestionDetailDto> DetailAsync(Actor actor, Question q, CancellationToken ct)
    {
        var quizCount = await presenter.QuizCountAsync(q.Id, ct);
        return await presenter.DetailAsync(q, QuestionAccessRules.ForTeacher(q, actor.UserId, quizCount > 0), quizCount, ct);
    }

    private static void EnsureOwner(Actor actor, Question q)
    {
        if (q.CreatedBy != actor.UserId) throw AppException.Forbidden(Messages.Forbidden);
    }

    /// <summary>Người tạo, câu chưa bị ẩn, chưa nằm trong quiz nào (mọi trạng thái, mọi trường).</summary>
    private async Task EnsureEditableAsync(Actor actor, Question q, string action, CancellationToken ct)
    {
        EnsureOwner(actor, q);
        if (QuestionAccessRules.IsHiddenByAdmin(q))
            throw AppException.BusinessRule(Messages.Blocked(action, "Câu hỏi đã bị quản trị hệ thống ẩn."));
        if (await presenter.QuizCountAsync(q.Id, ct) > 0)
            throw AppException.BusinessRule(Messages.Blocked(action, "Câu hỏi đã nằm trong bài kiểm tra. Bấm \"Nhân bản\" để tạo bản của bạn rồi sửa."));
    }

    private static void EnsureTeacher(Actor actor)
    {
        if (actor.Role != RoleCode.TEACHER) throw AppException.Forbidden(Messages.Forbidden);
    }

    private DateTime Now()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }
}
