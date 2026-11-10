using DSVHVN.Application.Audit;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Classes;
using DSVHVN.Application.Common;
using DSVHVN.Application.Curriculum;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Lessons;

/// <summary>Đổi dòng bài học sang trạng thái thư viện bài mẫu của DTO và đếm lượt dùng.</summary>
internal static class LessonSampleMapper
{
    public static LessonSampleDto ToDto(Lesson lesson, int useCount) =>
        new(lesson.SampleStatus!.Value, lesson.SampleRequestedAt, lesson.SampleReviewedAt, lesson.SampleNote, useCount);

    /// <summary>
    /// Số bài đã được tạo từ mỗi bài mẫu (<c>source_lesson_id</c>), kể cả bài tạo ra rồi bị xóa vì đó là lượt dùng đã xảy ra. Bài không có lượt dùng nào không có trong kết quả.
    /// </summary>
    public static async Task<Dictionary<long, int>> UseCountsAsync(IAppDbContext db, IReadOnlyCollection<long> lessonIds, CancellationToken ct)
    {
        if (lessonIds.Count == 0) return [];
        var ids = lessonIds.ToList();
        return await db.Lessons.AsNoTracking()
            .Where(l => l.SourceLessonId != null && ids.Contains(l.SourceLessonId.Value))
            .GroupBy(l => l.SourceLessonId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
    }
}

/// <summary>
/// Thư viện bài mẫu dùng chung. Bài hay của một giáo viên, sau khi quản trị hệ thống duyệt, vào thư viện cho mọi trường; giáo viên khác xem trước rồi dùng cho lớp của mình
/// (tạo bài Nháp mới bằng đúng cơ chế nhân bản sẵn có, <see cref="LessonService.CopyLessonAsync"/>).
/// <list type="bullet">
/// <item>Giáo viên chủ bài đề xuất (bài Đã xuất bản và đủ điều kiện xuất bản) và rút; sửa bài đang ở thư viện thì bài tự về chờ duyệt, lưu trữ hoặc xóa bài thì rời thư viện.</item>
/// <item>Quản trị hệ thống duyệt, từ chối (bắt buộc lý do) hoặc gỡ; quản trị trường không dùng thư viện.</item>
/// <item>Thư viện chỉ hiện bài đã duyệt, Đã xuất bản, chưa xóa, lớp chưa xóa, trường chưa ngừng và còn ít nhất một di sản đang xuất bản. Người soạn hiện họ tên và tên trường, không liên hệ.</item>
/// <item>Dùng bài mẫu cần gói hiệu lực, không tốn lượt AI; mọi thao tác đề xuất, rút, duyệt, từ chối, dùng ghi nhật ký thao tác.</item>
/// </list>
/// </summary>
public sealed class LessonSampleService(
    IAppDbContext db,
    RequestValidator validator,
    PlanGuard plans,
    LessonService lessons,
    LessonDetailBuilder detailBuilder,
    StudentLessonService studentView,
    IAuditLogger audit,
    TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ---- Giáo viên: đề xuất và rút ----

    /// <summary>
    /// Đề xuất bài vào thư viện (chỉ giáo viên chủ bài). Bài phải Đã xuất bản và đủ điều kiện xuất bản; chưa ở thư viện hoặc từng bị từ chối thì thành chờ duyệt (xóa lý do cũ).
    /// Bài Nháp, Lưu trữ, đang chờ duyệt hoặc đã trong thư viện thì 422 BLOCKED.
    /// </summary>
    public async Task<LessonSampleResultDto> RequestAsync(Actor actor, long lessonId, CancellationToken ct)
    {
        const string action = "đề xuất bài mẫu";
        var lesson = await db.FindLessonAsync(actor, lessonId, action, tracking: true, ct);
        if (LessonSampleRules.RequestBlockReason(lesson.Status, lesson.SampleStatus) is { } reason)
            throw AppException.BusinessRule(Messages.Blocked(action, reason));
        await EnsureReadyAsync(lesson, action, ct);

        var before = lesson.SampleStatus;
        lesson.SampleStatus = LessonSampleStatus.PENDING;
        lesson.SampleRequestedAt = Now;
        lesson.SampleReviewedAt = null;
        lesson.SampleReviewedBy = null;
        lesson.SampleNote = null;
        audit.Record(AuditAction.LESSON_SAMPLE_REQUESTED, AuditTargets.Lessons, lesson.Id, actor,
            new { sampleStatus = before }, new { sampleStatus = LessonSampleStatus.PENDING });
        await db.SaveChangesAsync(ct);
        return new LessonSampleResultDto(await detailBuilder.BuildAsync(lesson, ct), Messages.LessonSampleRequested.Text);
    }

    /// <summary>Rút bài khỏi thư viện từ chờ duyệt, đã trong thư viện hoặc bị từ chối: trạng thái về NULL, xóa ghi chú, người và lúc duyệt. Bài chưa ở thư viện thì 422 BLOCKED.</summary>
    public async Task<LessonSampleResultDto> WithdrawAsync(Actor actor, long lessonId, CancellationToken ct)
    {
        const string action = "rút bài khỏi thư viện bài mẫu";
        var lesson = await db.FindLessonAsync(actor, lessonId, action, tracking: true, ct);
        if (!LessonSampleRules.CanWithdraw(lesson.SampleStatus))
            throw AppException.BusinessRule(Messages.Blocked(action, "Bài học chưa ở trong thư viện bài mẫu."));

        var before = lesson.SampleStatus;
        LessonService.ClearSample(lesson);
        audit.Record(AuditAction.LESSON_SAMPLE_WITHDRAWN, AuditTargets.Lessons, lesson.Id, actor,
            new { sampleStatus = before }, new { sampleStatus = (LessonSampleStatus?)null });
        await db.SaveChangesAsync(ct);
        return new LessonSampleResultDto(await detailBuilder.BuildAsync(lesson, ct), Messages.LessonSampleWithdrawn.Text);
    }

    // ---- Giáo viên: thư viện ----

    /// <summary>Thư viện bài mẫu cho giáo viên: bài đã duyệt hiện được, lọc theo cấp, khối, di sản, từ khóa trong tiêu đề; xếp mới nhất (mặc định) hoặc dùng nhiều nhất.</summary>
    public async Task<PagedResult<LessonLibraryItemDto>> ListLibraryAsync(Actor actor, LessonLibraryQuery query, CancellationToken ct)
    {
        EnsureTeacher(actor);
        if (query.Level is { } level && level is < 1 or > 3)
            throw AppException.Validation("level", Messages.Invalid("Cấp", "từ 1 đến 3"));
        if (query.Grade is { } grade && grade is < SchoolClass.MinGrade or > SchoolClass.MaxGrade)
            throw AppException.Validation("grade", Messages.Invalid("Khối", $"từ {SchoolClass.MinGrade} đến {SchoolClass.MaxGrade}"));
        if (query.TopicId is < 1)
            throw AppException.Validation("topicId", Messages.Invalid("Chủ đề chương trình", "phải là mã chủ đề hợp lệ"));

        var lessonsQuery = LibraryLessons();
        if (query.Level is { } l) lessonsQuery = WhereLevel(lessonsQuery, l);
        if (query.Grade is { } g) lessonsQuery = lessonsQuery.Where(x => x.Class!.Grade == g);
        if (query.HeritageId is { } heritageId) lessonsQuery = lessonsQuery.Where(x => x.Heritages.Any(h => h.HeritageId == heritageId));
        if (query.TopicId is { } topicId)
        {
            // Chủ đề của bài: chủ đề đang dùng, cùng khối với lớp của bài và gắn với ít nhất một di sản của bài.
            lessonsQuery = lessonsQuery.Where(x =>
                db.CurriculumTopics.Any(t => t.Id == topicId && t.IsActive && t.Grade == x.Class!.Grade)
                && x.Heritages.Any(h => h.Heritage!.DeletedAt == null
                                        && db.HeritageCurriculumTopics.Any(l => l.TopicId == topicId && l.HeritageId == h.HeritageId)));
        }
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            lessonsQuery = lessonsQuery.Where(x => x.Title != null && x.Title.Contains(keyword));
        }

        // Sắp xếp trên bài học rồi mới chiếu sang dòng (EF không dịch được sắp xếp theo thuộc tính của bản ghi dựng bằng hàm khởi tạo).
        var ordered = query.Sort == LessonLibrarySort.POPULAR
            ? lessonsQuery.OrderByDescending(x => db.Lessons.Count(c => c.SourceLessonId == x.Id)).ThenByDescending(x => x.SampleReviewedAt).ThenByDescending(x => x.Id)
            : lessonsQuery.OrderByDescending(x => x.SampleReviewedAt).ThenByDescending(x => x.Id);
        var page = await Project(ordered).ToPagedAsync(query.Page, query.PageSize, ct);

        var items = await ToItemsAsync(page.Items, actor.UserId, publishedHeritagesOnly: true, ct);
        return new PagedResult<LessonLibraryItemDto>(items, page.Page, page.PageSize, page.TotalItems, page.TotalPages);
    }

    /// <summary>Xem trước một bài mẫu: thẻ bài, bài như học sinh thấy (không mã bài học, không tên lớp) và các bài kiểm tra sẽ chép. 404 khi bài không ở thư viện.</summary>
    public async Task<LessonLibraryDetailDto> GetLibraryAsync(Actor actor, long lessonId, CancellationToken ct)
    {
        EnsureTeacher(actor);
        var row = await Project(LibraryLessons().Where(l => l.Id == lessonId)).SingleOrDefaultAsync(ct)
                  ?? throw AppException.NotFound(Messages.NotFound("xem bài mẫu", "bài mẫu trong thư viện"));
        var item = (await ToItemsAsync([row], actor.UserId, publishedHeritagesOnly: true, ct)).Single();
        var (view, quizzes) = await ViewAsync(lessonId, row.Author, ct);
        return new LessonLibraryDetailDto(item, view, quizzes);
    }

    /// <summary>
    /// Dùng bài mẫu cho một lớp của chính giáo viên: cần gói hiệu lực, không tốn lượt AI. Tạo bài Nháp mới bằng đúng logic nhân bản sang lớp khác (chép tiêu đề, tóm tắt,
    /// ý chính, các phần và di sản đang xuất bản; lớp đích khác cấp thì kèm lời nhắc), ghi <c>source_lesson_id</c>; chép kèm bài kiểm tra nếu chọn. Bài gốc không đổi.
    /// Dùng chính bài mẫu của mình thì lớp đích phải khác lớp của bài, như nhân bản.
    /// </summary>
    public async Task<DuplicateLessonResultDto> UseAsync(Actor actor, long lessonId, DuplicateLessonRequest request, CancellationToken ct)
    {
        const string action = "dùng bài mẫu";
        EnsureTeacher(actor);
        var orgId = actor.RequireOrganizationId();
        await validator.EnsureValidAsync(request, ct);

        var source = await LibraryLessons().Include(l => l.Class).Include(l => l.Sections).SingleOrDefaultAsync(l => l.Id == lessonId, ct)
                     ?? throw AppException.NotFound(Messages.NotFound(action, "bài mẫu trong thư viện"));
        var target = await db.FindClassAsync(actor, request.TargetClassId!.Value, action, tracking: false, ct);
        if (source.TeacherId == actor.UserId && target.Id == source.ClassId)
            throw AppException.Validation("targetClassId", Messages.Invalid("Lớp đích", "phải là một lớp khác lớp của bài gốc"));
        if (await plans.GetEffectiveAsync(orgId, ct) is null) throw new AppException(422, Messages.PlanRequired(action));

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var result = await lessons.CopyLessonAsync(actor, source, target, request.IncludeQuizzes, sampleSourceId: source.Id, ct);
        audit.Record(AuditAction.LESSON_SAMPLE_USED, AuditTargets.Lessons, source.Id, actor, null,
            new { newLessonId = result.Lesson.Id, targetClassId = target.Id, includeQuizzes = request.IncludeQuizzes });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return result with
        {
            Message = Messages.LessonSampleUsed(target.Name ?? string.Empty, result.SkippedHeritages, result.CopiedQuizzes, result.SkippedQuestions).Text,
        };
    }

    // ---- Quản trị hệ thống: duyệt ----

    /// <summary>Bài đề xuất theo trạng thái (mặc định chờ duyệt, cũ nhất trước để duyệt lần lượt; đã duyệt và đã từ chối thì mới xử lý nhất trước), lọc theo cấp và từ khóa trong tiêu đề.</summary>
    public async Task<PagedResult<AdminLessonLibraryItemDto>> ListAdminAsync(Actor actor, AdminLessonLibraryQuery query, CancellationToken ct)
    {
        EnsureAdmin(actor);
        if (query.Level is { } level && level is < 1 or > 3)
            throw AppException.Validation("level", Messages.Invalid("Cấp", "từ 1 đến 3"));

        var status = query.Status ?? LessonSampleStatus.PENDING;
        var lessonsQuery = AdminLessons().Where(l => l.SampleStatus == status);
        if (query.Level is { } l) lessonsQuery = WhereLevel(lessonsQuery, l);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            lessonsQuery = lessonsQuery.Where(x => x.Title != null && x.Title.Contains(keyword));
        }

        var ordered = status == LessonSampleStatus.PENDING
            ? lessonsQuery.OrderBy(x => x.SampleRequestedAt).ThenBy(x => x.Id)
            : lessonsQuery.OrderByDescending(x => x.SampleReviewedAt).ThenByDescending(x => x.Id);
        var page = await Project(ordered).ToPagedAsync(query.Page, query.PageSize, ct);
        var items = await ToAdminItemsAsync(page.Items, ct);
        return new PagedResult<AdminLessonLibraryItemDto>(items, page.Page, page.PageSize, page.TotalItems, page.TotalPages);
    }

    /// <summary>Xem bài mẫu để duyệt (chỉ đọc), ở mọi trạng thái thư viện. 404 khi bài không ở thư viện.</summary>
    public async Task<AdminLessonLibraryDetailDto> GetAdminAsync(Actor actor, long lessonId, CancellationToken ct)
    {
        EnsureAdmin(actor);
        var row = await Project(AdminLessons().Where(l => l.Id == lessonId)).SingleOrDefaultAsync(ct)
                  ?? throw AppException.NotFound(Messages.NotFound("xem bài mẫu", "bài mẫu"));
        var item = (await ToAdminItemsAsync([row], ct)).Single();
        var (view, quizzes) = await ViewAsync(lessonId, row.Author, ct);
        return new AdminLessonLibraryDetailDto(item, view, quizzes);
    }

    /// <summary>Duyệt đề xuất đang chờ: bài vẫn phải Đã xuất bản và đủ điều kiện xuất bản. Ghi người và lúc duyệt.</summary>
    public async Task<AdminLessonLibraryResultDto> ApproveAsync(Actor actor, long lessonId, CancellationToken ct)
    {
        const string action = "duyệt bài mẫu";
        EnsureAdmin(actor);
        var lesson = await FindForReviewAsync(lessonId, action, ct);
        if (!LessonSampleRules.CanApprove(lesson.SampleStatus))
            throw AppException.BusinessRule(Messages.Blocked(action, "Chỉ duyệt được đề xuất đang chờ duyệt."));
        if (lesson.Status != ContentStatus.PUBLISHED)
            throw AppException.BusinessRule(Messages.Blocked(action, "Bài học không còn ở trạng thái đã xuất bản."));
        await EnsureReadyAsync(lesson, action, ct);

        lesson.SampleStatus = LessonSampleStatus.APPROVED;
        lesson.SampleReviewedAt = Now;
        lesson.SampleReviewedBy = actor.UserId;
        lesson.SampleNote = null;
        audit.Record(AuditAction.LESSON_SAMPLE_APPROVED, AuditTargets.Lessons, lesson.Id, actor,
            new { sampleStatus = LessonSampleStatus.PENDING }, new { sampleStatus = LessonSampleStatus.APPROVED });
        await db.SaveChangesAsync(ct);
        return new AdminLessonLibraryResultDto(await AdminItemAsync(lessonId, ct), Messages.LessonSampleApproved(lesson.Title).Text);
    }

    /// <summary>Từ chối đề xuất đang chờ, hoặc gỡ bài đang ở trong thư viện. Bắt buộc lý do 1-500 ký tự; giáo viên thấy lý do ở trang Soạn bài học.</summary>
    public async Task<AdminLessonLibraryResultDto> RejectAsync(Actor actor, long lessonId, RejectLessonSampleRequest request, CancellationToken ct)
    {
        const string action = "từ chối bài mẫu";
        EnsureAdmin(actor);
        await validator.EnsureValidAsync(request, ct);
        var lesson = await FindForReviewAsync(lessonId, action, ct);
        if (!LessonSampleRules.CanReject(lesson.SampleStatus))
            throw AppException.BusinessRule(Messages.Blocked(action, "Chỉ từ chối được đề xuất đang chờ duyệt hoặc gỡ bài đang ở trong thư viện."));

        var before = lesson.SampleStatus!.Value;
        var note = request.Note!.Trim();
        lesson.SampleStatus = LessonSampleStatus.REJECTED;
        lesson.SampleReviewedAt = Now;
        lesson.SampleReviewedBy = actor.UserId;
        lesson.SampleNote = note;
        audit.Record(AuditAction.LESSON_SAMPLE_REJECTED, AuditTargets.Lessons, lesson.Id, actor,
            new { sampleStatus = before }, new { sampleStatus = LessonSampleStatus.REJECTED, note });
        await db.SaveChangesAsync(ct);
        return new AdminLessonLibraryResultDto(await AdminItemAsync(lessonId, ct),
            Messages.LessonSampleRejected(removedFromLibrary: before == LessonSampleStatus.APPROVED).Text);
    }

    // ---- Nội bộ ----

    private static void EnsureTeacher(Actor actor)
    {
        if (actor.Role != RoleCode.TEACHER) throw AppException.Forbidden(Messages.Forbidden);
    }

    private static void EnsureAdmin(Actor actor)
    {
        if (actor.Role != RoleCode.ADMIN) throw AppException.Forbidden(Messages.Forbidden);
    }

    /// <summary>
    /// Bài hiện trong thư viện: đã duyệt, Đã xuất bản, chưa xóa, lớp chưa xóa, trường chưa ngừng và còn ít nhất một di sản đang xuất bản.
    /// </summary>
    private IQueryable<Lesson> LibraryLessons() => db.Lessons.AsNoTracking()
        .Where(l => l.SampleStatus == LessonSampleStatus.APPROVED && l.Status == ContentStatus.PUBLISHED && l.DeletedAt == null
                    && l.Class!.DeletedAt == null && l.Class.Organization!.DeletedAt == null
                    && l.Heritages.Any(h => h.Heritage!.DeletedAt == null && h.Heritage.Status == ContentStatus.PUBLISHED));

    /// <summary>Bài có trạng thái thư viện cho quản trị hệ thống duyệt: chưa xóa, lớp chưa xóa.</summary>
    private IQueryable<Lesson> AdminLessons() => db.Lessons.AsNoTracking()
        .Where(l => l.SampleStatus != null && l.DeletedAt == null && l.Class!.DeletedAt == null);

    private static IQueryable<Lesson> WhereLevel(IQueryable<Lesson> lessons, int level) => level switch
    {
        1 => lessons.Where(l => l.Class!.Grade <= 5),
        2 => lessons.Where(l => l.Class!.Grade >= 6 && l.Class.Grade <= 9),
        _ => lessons.Where(l => l.Class!.Grade >= 10),
    };

    private async Task<Lesson> FindForReviewAsync(long lessonId, string action, CancellationToken ct) =>
        await db.Lessons.Include(l => l.Class).Include(l => l.Sections)
            .Where(l => l.SampleStatus != null && l.DeletedAt == null && l.Class!.DeletedAt == null)
            .SingleOrDefaultAsync(l => l.Id == lessonId, ct)
        ?? throw AppException.NotFound(Messages.NotFound(action, "bài mẫu"));

    /// <summary>Bài phải đủ điều kiện xuất bản (như lúc xuất bản) mới đề xuất hoặc duyệt được; thiếu gì thì 422 NOT_READY liệt kê.</summary>
    private async Task EnsureReadyAsync(Lesson lesson, string action, CancellationToken ct)
    {
        var publishedHeritages = await db.LessonHeritages.AsNoTracking()
            .CountAsync(lh => lh.LessonId == lesson.Id && lh.Heritage!.DeletedAt == null && lh.Heritage.Status == ContentStatus.PUBLISHED, ct);
        var check = LessonService.Evaluate(lesson, SchoolClass.SchoolLevelOf(lesson.Class!.Grade), publishedHeritages);
        if (!check.IsReady) throw new AppException(422, Messages.NotReady(action, check.Missing));
    }

    private sealed record LibraryRow(
        long Id, string? Title, byte Grade, string? Summary, string? Author, string? School, long TeacherId,
        DateTime? RequestedAt, DateTime? ReviewedAt, DateTime? ApprovedAt, LessonSampleStatus? Status, string? Note, int Uses, int Quizzes);

    private IQueryable<LibraryRow> Project(IQueryable<Lesson> lessonsQuery) =>
        lessonsQuery.Select(l => new LibraryRow(
            l.Id, l.Title, l.Class!.Grade, l.Summary, l.Teacher!.FullName, l.Class.Organization!.Name, l.TeacherId,
            l.SampleRequestedAt, l.SampleReviewedAt, l.SampleReviewedAt, l.SampleStatus, l.SampleNote,
            db.Lessons.Count(c => c.SourceLessonId == l.Id),
            db.Quizzes.Count(q => q.LessonId == l.Id && q.DeletedAt == null)));

    private async Task<Dictionary<long, List<LibraryHeritageDto>>> HeritagesOfAsync(IReadOnlyCollection<long> lessonIds, bool publishedOnly, CancellationToken ct)
    {
        var ids = lessonIds.ToList();
        var rows = await db.LessonHeritages.AsNoTracking()
            .Where(lh => ids.Contains(lh.LessonId) && lh.Heritage!.DeletedAt == null
                         && (!publishedOnly || lh.Heritage.Status == ContentStatus.PUBLISHED))
            .OrderBy(lh => lh.HeritageId)
            .Select(lh => new { lh.LessonId, Dto = new LibraryHeritageDto(lh.HeritageId, lh.Heritage!.Slug, lh.Heritage.Name, lh.Heritage.ThumbnailUrl) })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.LessonId).ToDictionary(g => g.Key, g => g.Select(r => r.Dto).ToList());
    }

    private async Task<List<LessonLibraryItemDto>> ToItemsAsync(IReadOnlyList<LibraryRow> rows, long viewerId, bool publishedHeritagesOnly, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var heritages = await HeritagesOfAsync(ids, publishedHeritagesOnly, ct);
        var sectionTypes = (await db.LessonSections.AsNoTracking().Where(s => ids.Contains(s.LessonId))
                .Select(s => new { s.LessonId, s.SectionType }).ToListAsync(ct))
            .ToLookup(s => s.LessonId, s => s.SectionType);
        var topics = await CurriculumTopicLookup.ForLessonsAsync(db, rows.Select(r => (r.Id, (int)r.Grade)).ToList(), ct);

        return rows.Select(r =>
        {
            var level = SchoolClass.SchoolLevelOf(r.Grade);
            var frame = LessonFrames.For(level);
            return new LessonLibraryItemDto(
                r.Id, r.Title, level, r.Grade, heritages.GetValueOrDefault(r.Id) ?? [],
                LessonSampleRules.SummaryExcerpt(PlainText.FromMarkdown(r.Summary)),
                sectionTypes[r.Id].Count(frame.Allows), r.Quizzes, r.Author, r.School, r.Uses, r.ApprovedAt, r.TeacherId == viewerId,
                topics.GetValueOrDefault(r.Id) ?? []);
        }).ToList();
    }

    private async Task<List<AdminLessonLibraryItemDto>> ToAdminItemsAsync(IReadOnlyList<LibraryRow> rows, CancellationToken ct)
    {
        var heritages = await HeritagesOfAsync(rows.Select(r => r.Id).ToList(), publishedOnly: false, ct);
        return rows.Select(r => new AdminLessonLibraryItemDto(
            r.Id, r.Title, SchoolClass.SchoolLevelOf(r.Grade), r.Grade, heritages.GetValueOrDefault(r.Id) ?? [], r.Author, r.School,
            r.Status!.Value, r.RequestedAt, r.ReviewedAt, r.Note, r.Uses)).ToList();
    }

    private async Task<AdminLessonLibraryItemDto> AdminItemAsync(long lessonId, CancellationToken ct)
    {
        var row = await Project(AdminLessons().Where(l => l.Id == lessonId)).SingleAsync(ct);
        return (await ToAdminItemsAsync([row], ct)).Single();
    }

    /// <summary>
    /// Bài như học sinh thấy kèm danh sách bài kiểm tra sẽ chép. Bài xem trước không lộ mã bài học, tên lớp hay danh sách bài kiểm tra theo lớp của người soạn
    /// (giờ mở, đóng, trạng thái là của lớp gốc, vô nghĩa với người xem).
    /// </summary>
    private async Task<(StudentLessonDto View, List<LibraryQuizDto> Quizzes)> ViewAsync(long lessonId, string? authorName, CancellationToken ct)
    {
        var lesson = await db.Lessons.AsNoTracking().Include(l => l.Sections).Include(l => l.Class).SingleAsync(l => l.Id == lessonId, ct);
        var view = await studentView.BuildAsync(lesson, lesson.Class!.Grade, className: null, authorName, studentId: null, ct, includeQuizzes: false);
        var quizzes = await db.Quizzes.AsNoTracking().Where(q => q.LessonId == lessonId && q.DeletedAt == null)
            .OrderBy(q => q.Id).Select(q => new LibraryQuizDto(q.Name, q.Questions.Count)).ToListAsync(ct);
        return (view with { AccessCode = string.Empty }, quizzes);
    }
}
