using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Questions;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Questions;

/// <summary>
/// Quản trị hệ thống xem ngân hàng câu hỏi chỉ đọc và ẩn câu kém chất lượng. Chỉ thấy câu đã duyệt và câu mình đã ẩn; câu chờ duyệt hay bị người
/// tạo loại là việc riêng của giáo viên. Ẩn là chuyển câu APPROVED sang REJECTED với người duyệt là quản trị hệ thống và ghi nhật ký thao tác:
/// câu bị ẩn không chọn được vào quiz mới, không nhân bản được; quiz đã phát hành và kết quả cũ giữ nguyên. Quản trị hệ thống cũng hiện lại được
/// câu đã ẩn (câu về lại trạng thái đã duyệt của người tạo). Màn kiểm duyệt kèm chất lượng của từng câu tính từ kết quả làm bài.
/// </summary>
public sealed class AdminQuestionService(IAppDbContext db, QuestionPresenter presenter, IAuditLogger audit, TimeProvider clock)
{
    public async Task<QuestionPageDto> ListAsync(Actor actor, QuestionListQuery query, CancellationToken ct)
    {
        EnsureAdmin(actor);
        if (query.Level is { } level && level is < 1 or > 3)
            throw AppException.Validation("level", Messages.Invalid("Cấp", "từ 1 đến 3"));

        var questions = db.Questions.AsNoTracking().Where(q => q.DeletedAt == null
            && (q.ReviewStatus == ReviewStatus.APPROVED || (q.ReviewStatus == ReviewStatus.REJECTED && q.Reviewer!.RoleId == Roles.AdminId)));
        if (query.Status is ReviewStatus.APPROVED or ReviewStatus.REJECTED)
            questions = questions.Where(q => q.ReviewStatus == query.Status);
        if (query.CreatedBy is { } creator) questions = questions.Where(q => q.CreatedBy == creator);
        questions = QuestionService.ApplyFilters(questions, query);

        var rows = questions
            .OrderByDescending(q => q.CreatedAt).ThenByDescending(q => q.Id)
            .Select(q => new
            {
                Question = q,
                Creator = q.Creator!.FullName,
                Organization = q.Creator.Organization!.Name,
                Heritage = q.Heritage!.Name,
                Hidden = q.ReviewStatus == ReviewStatus.REJECTED,
                Quizzes = db.QuizQuestions.Count(x => x.QuestionId == q.Id && x.Quiz!.DeletedAt == null),
            });
        var page = await rows.ToPagedAsync(query.Page, query.PageSize, ct);
        var items = page.Items.Select(r => QuestionPresenter.Row(r.Question, r.Creator, r.Organization, r.Heritage, false, r.Hidden, r.Quizzes)).ToList();
        return new QuestionPageDto(items, page.Page, page.PageSize, page.TotalItems, page.TotalPages);
    }

    public async Task<QuestionDetailDto> GetAsync(Actor actor, long id, CancellationToken ct)
    {
        EnsureAdmin(actor);
        var q = await FindAsync(id, "xem câu hỏi", tracking: false, ct);
        return await presenter.DetailAsync(q, QuestionAccessRules.ForAdmin(q), await presenter.QuizCountAsync(id, ct), ct);
    }

    /// <summary>Ẩn câu đã duyệt kém chất lượng. Câu đã ẩn hoặc chưa duyệt thì không ẩn lại được (422).</summary>
    public async Task<QuestionDetailDto> HideAsync(Actor actor, long id, AdminHideQuestionRequest request, CancellationToken ct)
    {
        EnsureAdmin(actor);
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is { Length: > 500 })
            throw AppException.Validation("reason", Messages.Invalid("Lý do ẩn", "tối đa 500 ký tự"));

        var q = await FindAsync(id, "ẩn câu hỏi", tracking: true, ct);
        if (q.ReviewStatus != ReviewStatus.APPROVED)
            throw AppException.BusinessRule(Messages.Blocked("ẩn câu hỏi", "Chỉ ẩn được câu đã duyệt."));

        var before = new { reviewStatus = q.ReviewStatus, reviewedBy = q.ReviewedBy };
        q.ReviewStatus = ReviewStatus.REJECTED;
        q.ReviewedBy = actor.UserId;
        q.ReviewedAt = Now();
        audit.Record(AuditAction.QUESTION_HIDDEN, AuditTargets.Questions, q.Id, actor, before,
            new { reviewStatus = ReviewStatus.REJECTED, reviewedBy = actor.UserId, reason });
        await db.SaveChangesAsync(ct);
        return await GetAsync(actor, id, ct);
    }

    /// <summary>
    /// Danh sách kiểm duyệt: câu đã duyệt và câu đã bị ẩn kèm số lượt trả lời, số lượt đúng và dấu hiệu "cần xem lại" (đủ lượt mà tỉ lệ đúng thấp).
    /// Chất lượng tính từ câu trả lời của mọi lượt đã nộp ở mọi trường, khi hiển thị; ngân hàng không có chức năng báo cáo câu hỏi.
    /// </summary>
    public async Task<QuestionQualityPageDto> ListQualityAsync(Actor actor, QuestionQualityQuery query, CancellationToken ct)
    {
        EnsureAdmin(actor);
        if (query.Level is { } level && level is < 1 or > 3)
            throw AppException.Validation("level", Messages.Invalid("Cấp", "từ 1 đến 3"));
        if (query.Status is ReviewStatus.PENDING)
            throw AppException.Validation("status", Messages.Invalid("Trạng thái", "chỉ nhận APPROVED (đang trong ngân hàng) hoặc REJECTED (đã bị ẩn)"));

        var visible = Visible();
        var questions = visible;
        if (query.Status is { } status) questions = questions.Where(q => q.ReviewStatus == status);
        if (query.OrganizationId is { } organizationId) questions = questions.Where(q => q.Creator!.OrganizationId == organizationId);
        if (query.CreatedBy is { } creator) questions = questions.Where(q => q.CreatedBy == creator);
        questions = QuestionService.ApplyFilters(questions, new QuestionListQuery(
            QuestionType: query.QuestionType, Level: query.Level, DifficultyLevel: query.DifficultyLevel, HeritageId: query.HeritageId, Keyword: query.Keyword));

        var min = QuestionQualityRules.MinAnswers;
        var low = QuestionQualityRules.LowCorrectPercent;
        var rows = questions.Select(q => new
        {
            Question = q,
            Creator = q.Creator!.FullName,
            Organization = q.Creator.Organization!.Name,
            Heritage = q.Heritage!.Name,
            Hidden = q.ReviewStatus == ReviewStatus.REJECTED,
            Quizzes = db.QuizQuestions.Count(x => x.QuestionId == q.Id && x.Quiz!.DeletedAt == null),
            Answered = db.AttemptAnswers.Count(a => a.QuestionId == q.Id && a.Attempt!.Status != AttemptStatus.IN_PROGRESS),
            Correct = db.AttemptAnswers.Count(a => a.QuestionId == q.Id && a.Attempt!.Status != AttemptStatus.IN_PROGRESS && a.IsCorrect),
        });
        if (query.LowQualityOnly) rows = rows.Where(r => r.Answered >= min && r.Correct * 100 < low * r.Answered);

        var ordered = query.Sort == QuestionQualitySort.LOWEST_RATE
            // Số nguyên cả phép chia để mọi hệ cơ sở dữ liệu xếp giống nhau; câu chưa ai làm xếp cuối.
            ? rows.OrderBy(r => r.Answered == 0 ? 1 : 0).ThenBy(r => r.Correct * 10000 / (r.Answered == 0 ? 1 : r.Answered))
                .ThenByDescending(r => r.Answered).ThenBy(r => r.Question.Id)
            : rows.OrderByDescending(r => r.Question.CreatedAt).ThenByDescending(r => r.Question.Id);
        var page = await ordered.ToPagedAsync(query.Page, query.PageSize, ct);
        var items = page.Items.Select(r => new QuestionQualityRowDto(
            QuestionPresenter.Row(r.Question, r.Creator, r.Organization, r.Heritage, false, r.Hidden, r.Quizzes),
            r.Answered, r.Correct, QuestionQualityRules.CorrectRatePercent(r.Answered, r.Correct),
            !r.Hidden && QuestionQualityRules.IsLowQuality(r.Answered, r.Correct),
            r.Hidden ? r.Question.ReviewedAt : null)).ToList();

        var approved = visible.Where(q => q.ReviewStatus == ReviewStatus.APPROVED);
        var approvedCount = await approved.CountAsync(ct);
        var hiddenCount = await visible.CountAsync(q => q.ReviewStatus == ReviewStatus.REJECTED, ct);
        var lowCount = await approved.CountAsync(q =>
            db.AttemptAnswers.Count(a => a.QuestionId == q.Id && a.Attempt!.Status != AttemptStatus.IN_PROGRESS) >= min
            && db.AttemptAnswers.Count(a => a.QuestionId == q.Id && a.Attempt!.Status != AttemptStatus.IN_PROGRESS && a.IsCorrect) * 100
               < low * db.AttemptAnswers.Count(a => a.QuestionId == q.Id && a.Attempt!.Status != AttemptStatus.IN_PROGRESS), ct);
        return new QuestionQualityPageDto(items, page.Page, page.PageSize, page.TotalItems, page.TotalPages,
            approvedCount, hiddenCount, lowCount, min, low);
    }

    /// <summary>
    /// Hiện lại câu đã bị quản trị hệ thống ẩn: câu về trạng thái đã duyệt với người duyệt là người tạo (như lúc duyệt ban đầu), chọn lại được vào quiz mới
    /// và hiện lại cho mọi giáo viên. Ghi nhật ký thao tác kèm lý do (tùy chọn). Câu chưa bị ẩn thì không hiện lại được (422).
    /// </summary>
    public async Task<QuestionDetailDto> RestoreAsync(Actor actor, long id, AdminHideQuestionRequest request, CancellationToken ct)
    {
        EnsureAdmin(actor);
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is { Length: > 500 })
            throw AppException.Validation("reason", Messages.Invalid("Lý do hiện lại", "tối đa 500 ký tự"));

        var q = await FindAsync(id, "hiện lại câu hỏi", tracking: true, ct);
        if (q.ReviewStatus != ReviewStatus.REJECTED)
            throw AppException.BusinessRule(Messages.Blocked("hiện lại câu hỏi", "Chỉ hiện lại được câu đã bị ẩn."));

        var before = new { reviewStatus = q.ReviewStatus, reviewedBy = q.ReviewedBy };
        q.ReviewStatus = ReviewStatus.APPROVED;
        q.ReviewedBy = q.CreatedBy;
        q.ReviewedAt = Now();
        audit.Record(AuditAction.QUESTION_RESTORED, AuditTargets.Questions, q.Id, actor, before,
            new { reviewStatus = ReviewStatus.APPROVED, reviewedBy = q.CreatedBy, reason });
        await db.SaveChangesAsync(ct);
        return await GetAsync(actor, id, ct);
    }

    /// <summary>Câu quản trị hệ thống thấy: câu đã duyệt (chưa xóa) và câu đã bị chính quản trị hệ thống ẩn.</summary>
    private IQueryable<Question> Visible() => db.Questions.AsNoTracking().Where(q => q.DeletedAt == null
        && (q.ReviewStatus == ReviewStatus.APPROVED || (q.ReviewStatus == ReviewStatus.REJECTED && q.Reviewer!.RoleId == Roles.AdminId)));

    private async Task<Question> FindAsync(long id, string action, bool tracking, CancellationToken ct)
    {
        var query = db.Questions.Include(q => q.Answers).Include(q => q.Heritage).Include(q => q.Reviewer)
            .Include(q => q.Creator!).ThenInclude(u => u.Organization).AsQueryable();
        if (!tracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(q => q.Id == id && q.DeletedAt == null
                   && (q.ReviewStatus == ReviewStatus.APPROVED || (q.ReviewStatus == ReviewStatus.REJECTED && q.Reviewer!.RoleId == Roles.AdminId)), ct)
               ?? throw AppException.NotFound(Messages.NotFound(action, "câu hỏi"));
    }

    private static void EnsureAdmin(Actor actor)
    {
        if (actor.Role != RoleCode.ADMIN) throw AppException.Forbidden(Messages.Forbidden);
    }

    private DateTime Now()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }
}
