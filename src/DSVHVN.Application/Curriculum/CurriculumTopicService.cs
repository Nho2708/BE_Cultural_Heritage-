using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Application.Discovery;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Curriculum;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Curriculum;

/// <summary>
/// Chủ đề chương trình môn học (bản gọn). Giáo viên chọn di sản theo chủ đề của môn đang dạy: chỉ thấy chủ đề đang dùng và di sản đang xuất bản.
/// Quản trị hệ thống thêm, sửa, ngừng dùng, dùng lại chủ đề và thay toàn bộ danh sách di sản của chủ đề (mọi di sản chưa xóa). Không xóa cứng chủ đề:
/// ngừng dùng thì ẩn với giáo viên nhưng giữ liên kết. (khối, môn, chủ đề lớn, nội dung) không trùng. Mọi thay đổi ghi nhật ký thao tác.
/// </summary>
public sealed class CurriculumTopicService(IAppDbContext db, RequestValidator validator, IAuditLogger audit)
{
    // ---- Giáo viên ----

    /// <summary>Chủ đề đang dùng (lọc theo khối nếu có), theo khối rồi thứ tự trong chương trình; số di sản chỉ đếm di sản đang xuất bản.</summary>
    public async Task<TeacherCurriculumTopicListDto> ListForTeacherAsync(Actor actor, int? grade, CancellationToken ct)
    {
        EnsureTeacher(actor);
        EnsureGrade(grade);
        var topics = await db.CurriculumTopics.AsNoTracking()
            .Where(t => t.IsActive && (grade == null || t.Grade == grade))
            .OrderBy(t => t.Grade).ThenBy(t => t.SortOrder).ThenBy(t => t.Id)
            .ToListAsync(ct);
        var counts = await HeritageCountsAsync(topics.Select(t => t.Id).ToList(), publishedOnly: true, ct);
        return new TeacherCurriculumTopicListDto(topics.Select(t => ToTeacherDto(t, counts.GetValueOrDefault(t.Id))).ToList());
    }

    /// <summary>Di sản đang xuất bản của một chủ đề đang dùng (cấp xếp hạng cao trước, rồi theo tên). 404 khi chủ đề ngừng dùng hoặc không có.</summary>
    public async Task<TeacherCurriculumTopicHeritagesDto> HeritagesForTeacherAsync(Actor actor, int topicId, CancellationToken ct)
    {
        const string action = "xem di sản của chủ đề chương trình";
        EnsureTeacher(actor);
        var topic = await db.CurriculumTopics.AsNoTracking().SingleOrDefaultAsync(t => t.Id == topicId && t.IsActive, ct)
                    ?? throw AppException.NotFound(Messages.CurriculumTopicNotFound(action));
        var cards = await db.HeritageCurriculumTopics.AsNoTracking()
            .Where(l => l.TopicId == topicId && l.Heritage!.DeletedAt == null && l.Heritage.Status == ContentStatus.PUBLISHED)
            .Select(l => l.Heritage!)
            .OrderBy(DiscoveryService.LevelRank).ThenBy(h => h.Name).ThenBy(h => h.Id)
            .Select(DiscoveryService.ToCard)
            .ToListAsync(ct);
        return new TeacherCurriculumTopicHeritagesDto(ToTeacherDto(topic, cards.Count), cards);
    }

    // ---- Quản trị hệ thống: chủ đề ----

    /// <summary>Chủ đề của một khối hoặc mọi khối; chủ đề ngừng dùng chỉ hiện khi <paramref name="includeInactive"/>. Số di sản đếm mọi di sản chưa xóa.</summary>
    public async Task<AdminCurriculumTopicListDto> ListForAdminAsync(Actor actor, int? grade, bool includeInactive, CancellationToken ct)
    {
        EnsureAdmin(actor);
        EnsureGrade(grade);
        var topics = await db.CurriculumTopics.AsNoTracking()
            .Where(t => (includeInactive || t.IsActive) && (grade == null || t.Grade == grade))
            .OrderBy(t => t.Grade).ThenBy(t => t.SortOrder).ThenBy(t => t.Id)
            .ToListAsync(ct);
        var counts = await HeritageCountsAsync(topics.Select(t => t.Id).ToList(), publishedOnly: false, ct);
        return new AdminCurriculumTopicListDto(topics.Select(t => ToAdminDto(t, counts.GetValueOrDefault(t.Id))).ToList());
    }

    /// <summary>Thêm chủ đề (đang dùng, chưa có di sản). Trùng (khối, môn, chủ đề lớn, nội dung) thì 409.</summary>
    public async Task<AdminCurriculumTopicDto> CreateAsync(Actor actor, CurriculumTopicRequest request, CancellationToken ct)
    {
        EnsureAdmin(actor);
        await validator.EnsureValidAsync(request, ct);
        await EnsureNotDuplicateAsync(request, exceptId: null, ct);

        var topic = new CurriculumTopic { IsActive = true };
        Apply(topic, request);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.CurriculumTopics.Add(topic);
        await db.SaveChangesAsync(ct);
        audit.Record(AuditAction.CURRICULUM_TOPIC_CREATED, AuditTargets.CurriculumTopics, topic.Id, actor, newValue: Snapshot(topic));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToAdminDto(topic, 0);
    }

    /// <summary>Sửa chủ đề. Trùng với chủ đề khác thì 409; không đổi trạng thái dùng và danh sách di sản.</summary>
    public async Task<AdminCurriculumTopicDto> UpdateAsync(Actor actor, int id, CurriculumTopicRequest request, CancellationToken ct)
    {
        EnsureAdmin(actor);
        await validator.EnsureValidAsync(request, ct);
        var topic = await FindAsync(id, "sửa chủ đề chương trình", ct);
        await EnsureNotDuplicateAsync(request, exceptId: id, ct);

        var before = Snapshot(topic);
        Apply(topic, request);
        audit.Record(AuditAction.CURRICULUM_TOPIC_UPDATED, AuditTargets.CurriculumTopics, topic.Id, actor, before, Snapshot(topic));
        await db.SaveChangesAsync(ct);
        return await AdminDtoAsync(topic, ct);
    }

    /// <summary>Ngừng dùng: giáo viên không thấy nữa, liên kết với di sản vẫn giữ. Đã ngừng rồi thì không đổi gì.</summary>
    public Task<AdminCurriculumTopicDto> DeactivateAsync(Actor actor, int id, CancellationToken ct) =>
        SetActiveAsync(actor, id, false, ct);

    /// <summary>Dùng lại chủ đề đã ngừng. Đang dùng rồi thì không đổi gì.</summary>
    public Task<AdminCurriculumTopicDto> ActivateAsync(Actor actor, int id, CancellationToken ct) =>
        SetActiveAsync(actor, id, true, ct);

    private async Task<AdminCurriculumTopicDto> SetActiveAsync(Actor actor, int id, bool active, CancellationToken ct)
    {
        EnsureAdmin(actor);
        var topic = await FindAsync(id, active ? "dùng lại chủ đề chương trình" : "ngừng dùng chủ đề chương trình", ct);
        if (topic.IsActive != active)
        {
            topic.IsActive = active;
            audit.Record(active ? AuditAction.CURRICULUM_TOPIC_ACTIVATED : AuditAction.CURRICULUM_TOPIC_DEACTIVATED,
                AuditTargets.CurriculumTopics, topic.Id, actor, new { isActive = !active }, new { isActive = active, title = topic.Title });
            await db.SaveChangesAsync(ct);
        }
        return await AdminDtoAsync(topic, ct);
    }

    // ---- Quản trị hệ thống: di sản của chủ đề ----

    /// <summary>Chủ đề kèm di sản đang gắn (mọi di sản chưa xóa, kể cả bản nháp và đã ẩn), theo tên.</summary>
    public async Task<AdminCurriculumTopicHeritagesDto> GetHeritagesForAdminAsync(Actor actor, int id, CancellationToken ct)
    {
        EnsureAdmin(actor);
        var topic = await db.CurriculumTopics.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct)
                    ?? throw AppException.NotFound(Messages.CurriculumTopicNotFound("xem di sản của chủ đề chương trình"));
        return await HeritagesDtoAsync(topic, ct);
    }

    /// <summary>
    /// Thay toàn bộ danh sách di sản của chủ đề (0-200 di sản, không trùng, chưa xóa; bản nháp và đã ẩn gắn được). Chỉ thêm và bỏ phần khác biệt;
    /// danh sách không đổi thì không ghi nhật ký.
    /// </summary>
    public async Task<AdminCurriculumTopicHeritagesDto> SetHeritagesAsync(Actor actor, int id, SetTopicHeritagesRequest request, CancellationToken ct)
    {
        EnsureAdmin(actor);
        await validator.EnsureValidAsync(request, ct);
        var topic = await FindAsync(id, "đổi di sản của chủ đề chương trình", ct);

        var wanted = request.HeritageIds!.ToList();
        var existing = await db.Heritages.AsNoTracking().Where(h => wanted.Contains(h.Id) && h.DeletedAt == null).Select(h => h.Id).ToListAsync(ct);
        if (existing.Count != wanted.Count)
            throw AppException.Validation("heritageIds", Messages.CurriculumHeritagesMissing(wanted.Count - existing.Count));

        var links = await db.HeritageCurriculumTopics.Where(l => l.TopicId == id).ToListAsync(ct);
        var current = links.Select(l => l.HeritageId).ToHashSet();
        var target = wanted.ToHashSet();
        var added = wanted.Where(h => !current.Contains(h)).ToList();
        var removed = links.Where(l => !target.Contains(l.HeritageId)).ToList();

        if (added.Count > 0 || removed.Count > 0)
        {
            db.HeritageCurriculumTopics.RemoveRange(removed);
            db.HeritageCurriculumTopics.AddRange(added.Select(h => new HeritageCurriculumTopic { HeritageId = h, TopicId = id }));
            audit.Record(AuditAction.CURRICULUM_TOPIC_HERITAGES_CHANGED, AuditTargets.CurriculumTopics, id, actor,
                new { count = current.Count },
                new { count = target.Count, added = added.Order().ToList(), removed = removed.Select(l => l.HeritageId).Order().ToList() });
            await db.SaveChangesAsync(ct);
        }
        return await HeritagesDtoAsync(topic, ct);
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

    private static void EnsureGrade(int? grade)
    {
        if (grade is { } g && !CurriculumRules.IsValidGrade(g))
            throw AppException.Validation("grade", Messages.Invalid("Khối", $"từ {SchoolClass.MinGrade} đến {SchoolClass.MaxGrade}"));
    }

    private async Task<CurriculumTopic> FindAsync(int id, string action, CancellationToken ct) =>
        await db.CurriculumTopics.SingleOrDefaultAsync(t => t.Id == id, ct)
        ?? throw AppException.NotFound(Messages.CurriculumTopicNotFound(action));

    private static void Apply(CurriculumTopic topic, CurriculumTopicRequest request)
    {
        topic.Subject = request.Subject!.Trim();
        topic.Grade = (byte)request.Grade!.Value;
        topic.GroupTitle = request.GroupTitle!.Trim();
        topic.Title = request.Title!.Trim();
        topic.Description = ValidationRules.TrimToNull(request.Description);
        topic.LocalProvince = request.LocalProvince!.Value;
        topic.SortOrder = request.SortOrder!.Value;
        topic.SourceUrl = ValidationRules.TrimToNull(request.SourceUrl);
    }

    /// <summary>
    /// Trùng khi cùng khối, môn, chủ đề lớn và nội dung (không phân biệt hoa thường và dấu). So trong bộ nhớ trên các chủ đề cùng khối
    /// để cùng một kết quả trên SQL Server (collation Vietnamese_CI_AI) và SQLite; ràng buộc UNIQUE của CSDL là lớp chặn cuối.
    /// </summary>
    private async Task EnsureNotDuplicateAsync(CurriculumTopicRequest request, int? exceptId, CancellationToken ct)
    {
        var grade = (byte)request.Grade!.Value;
        var sameGrade = await db.CurriculumTopics.AsNoTracking().Where(t => t.Grade == grade && t.Id != exceptId)
            .Select(t => new { t.Subject, t.GroupTitle, t.Title }).ToListAsync(ct);
        var key = Key(request.Subject, request.GroupTitle, request.Title);
        if (sameGrade.Any(t => Key(t.Subject, t.GroupTitle, t.Title) == key))
            throw AppException.Conflict("title", Messages.CurriculumTopicTaken(request.Title!.Trim()));
    }

    private static string Key(string? subject, string? groupTitle, string? title) =>
        string.Join('\u001f', SearchText.Fold(subject?.Trim()), SearchText.Fold(groupTitle?.Trim()), SearchText.Fold(title?.Trim()));

    private async Task<Dictionary<int, int>> HeritageCountsAsync(IReadOnlyCollection<int> topicIds, bool publishedOnly, CancellationToken ct)
    {
        if (topicIds.Count == 0) return [];
        var ids = topicIds.ToList();
        return await db.HeritageCurriculumTopics.AsNoTracking()
            .Where(l => ids.Contains(l.TopicId) && l.Heritage!.DeletedAt == null && (!publishedOnly || l.Heritage.Status == ContentStatus.PUBLISHED))
            .GroupBy(l => l.TopicId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
    }

    private async Task<AdminCurriculumTopicDto> AdminDtoAsync(CurriculumTopic topic, CancellationToken ct) =>
        ToAdminDto(topic, (await HeritageCountsAsync([topic.Id], publishedOnly: false, ct)).GetValueOrDefault(topic.Id));

    private async Task<AdminCurriculumTopicHeritagesDto> HeritagesDtoAsync(CurriculumTopic topic, CancellationToken ct)
    {
        var items = await db.HeritageCurriculumTopics.AsNoTracking()
            .Where(l => l.TopicId == topic.Id && l.Heritage!.DeletedAt == null)
            .Select(l => l.Heritage!)
            .OrderBy(h => h.Name).ThenBy(h => h.Id)
            .Select(h => new AdminCurriculumTopicHeritageDto(h.Id, h.Slug, h.Name, h.Province == null ? null : h.Province.Name, h.Status))
            .ToListAsync(ct);
        return new AdminCurriculumTopicHeritagesDto(ToAdminDto(topic, items.Count), items);
    }

    private static TeacherCurriculumTopicDto ToTeacherDto(CurriculumTopic t, int heritageCount) =>
        new(t.Id, t.Subject, t.Grade, t.GroupTitle, t.Title, t.Description, t.LocalProvince, heritageCount);

    private static AdminCurriculumTopicDto ToAdminDto(CurriculumTopic t, int heritageCount) =>
        new(t.Id, t.Subject, t.Grade, t.GroupTitle, t.Title, t.Description, t.LocalProvince, heritageCount, t.SortOrder, t.SourceUrl, t.IsActive);

    private static object Snapshot(CurriculumTopic t) => new
    {
        subject = t.Subject, grade = t.Grade, groupTitle = t.GroupTitle, title = t.Title, description = t.Description,
        localProvince = t.LocalProvince, sortOrder = t.SortOrder, sourceUrl = t.SourceUrl, isActive = t.IsActive,
    };
}

/// <summary>Chủ đề chương trình của bài học: chủ đề đang dùng cùng khối với lớp và gắn với ít nhất một di sản của bài (tính khi hiển thị, không lưu).</summary>
internal static class CurriculumTopicLookup
{
    /// <summary>Chủ đề của từng bài theo (mã bài, khối của lớp); bài không có chủ đề nào thì không có trong kết quả. Chủ đề xếp theo khối, thứ tự chương trình.</summary>
    public static async Task<Dictionary<long, List<CurriculumTopicBriefDto>>> ForLessonsAsync(
        IAppDbContext db, IReadOnlyCollection<(long LessonId, int Grade)> lessons, CancellationToken ct)
    {
        if (lessons.Count == 0) return [];
        var grades = lessons.ToDictionary(l => l.LessonId, l => l.Grade);
        var ids = grades.Keys.ToList();
        var rows = await (
            from lh in db.LessonHeritages.AsNoTracking()
            join link in db.HeritageCurriculumTopics.AsNoTracking() on lh.HeritageId equals link.HeritageId
            where ids.Contains(lh.LessonId) && lh.Heritage!.DeletedAt == null && link.Topic!.IsActive
            select new
            {
                lh.LessonId, link.Topic!.Id, link.Topic.Subject, link.Topic.Grade, link.Topic.GroupTitle, link.Topic.Title, link.Topic.SortOrder,
            }).Distinct().ToListAsync(ct);

        return rows.Where(r => r.Grade == grades[r.LessonId])
            .OrderBy(r => r.Grade).ThenBy(r => r.SortOrder).ThenBy(r => r.Id)
            .GroupBy(r => r.LessonId)
            .ToDictionary(g => g.Key, g => g.Select(r => new CurriculumTopicBriefDto(r.Id, r.Subject, r.Grade, r.GroupTitle, r.Title)).ToList());
    }
}
