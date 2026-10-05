using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Questions;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Questions;

/// <summary>
/// Quản trị hệ thống xem ngân hàng câu hỏi chỉ đọc và ẩn câu kém chất lượng. Chỉ thấy câu đã duyệt và câu mình đã ẩn; câu chờ duyệt hay bị người
/// tạo loại là việc riêng của giáo viên. Ẩn là chuyển câu APPROVED sang REJECTED với người duyệt là quản trị hệ thống và ghi nhật ký thao tác:
/// câu bị ẩn không chọn được vào quiz mới, không nhân bản được; quiz đã phát hành và kết quả cũ giữ nguyên.
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
                Quizzes = db.QuizQuestions.Count(x => x.QuestionId == q.Id),
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
