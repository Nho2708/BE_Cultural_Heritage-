using DSVHVN.Application.Billing;
using DSVHVN.Application.Classes;
using DSVHVN.Application.Common;
using DSVHVN.Application.Quizzes;
using DSVHVN.Application.Speech;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DSVHVN.Application.Lessons;

/// <summary>
/// Phạm vi bài học của giáo viên: bài do chính người gọi tạo, ở lớp của trường mình chưa xóa mềm, bài chưa xóa mềm.
/// Bài của giáo viên khác (kể cả cùng trường), của trường khác hay của lớp đã xóa đều như không tồn tại (404).
/// </summary>
internal static class LessonScope
{
    public static IQueryable<Lesson> LessonsOf(this IAppDbContext db, Actor actor)
    {
        if (actor.Role != RoleCode.TEACHER) throw AppException.Forbidden(Messages.Forbidden);
        var orgId = actor.RequireOrganizationId();
        return db.Lessons.Where(l => l.TeacherId == actor.UserId && l.DeletedAt == null
                                     && l.Class!.OrganizationId == orgId && l.Class.DeletedAt == null);
    }

    /// <summary>Bài của giáo viên kèm lớp và các phần nội dung.</summary>
    public static async Task<Lesson> FindLessonAsync(this IAppDbContext db, Actor actor, long lessonId, string action, bool tracking,
        CancellationToken ct)
    {
        var query = db.LessonsOf(actor).Include(l => l.Class).Include(l => l.Sections).AsQueryable();
        if (!tracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(l => l.Id == lessonId, ct)
               ?? throw AppException.NotFound(Messages.NotFound(action, "bài học của bạn"));
    }
}

/// <summary>Nguồn mã bài học; tách thành giao diện để kiểm thử ép được trường hợp trùng mã.</summary>
public interface ILessonCodeGenerator
{
    string NewCode();
}

public sealed class RandomLessonCodeGenerator : ILessonCodeGenerator
{
    public string NewCode() => LessonCodes.NewCode();
}

/// <summary>Dựng chi tiết bài học cho giáo viên: nội dung, khung của cấp, điều kiện xuất bản, bài kiểm tra, hạn mức lượt AI.</summary>
public sealed class LessonDetailBuilder(IAppDbContext db, PlanGuard plans, LessonMaterialService materialService)
{
    /// <summary>Người gọi phải nạp sẵn <c>Class</c> và <c>Sections</c> của bài.</summary>
    public async Task<LessonDetailDto> BuildAsync(Lesson lesson, CancellationToken ct)
    {
        var schoolClass = lesson.Class!;
        var level = SchoolClass.SchoolLevelOf(schoolClass.Grade);
        var frame = LessonFrames.For(level);

        var heritages = await LoadHeritagesAsync(lesson.Id, ct);
        var materials = await materialService.LoadAsync(heritages.Select(h => h.Id).ToList(), ct);
        var sections = lesson.Sections.OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToList();
        var keyPoints = LessonJson.ParseKeyPoints(lesson.KeyPoints);

        var views = sections.Select(LessonMapper.ToView).ToList();
        var check = LessonChecks.Evaluate(level, new LessonContent(lesson.Title,
            heritages.Count(h => h.Status == ContentStatus.PUBLISHED), lesson.Summary, keyPoints, views));
        var words = LessonChecks.MainWordCount(lesson.Summary, views.Where(v => frame.Allows(v.Type)));

        var quizCount = await db.Quizzes.CountAsync(q => q.LessonId == lesson.Id && q.DeletedAt == null, ct);
        var quota = await plans.GetAiQuotaAsync(schoolClass.OrganizationId, ct);

        return new LessonDetailDto(
            lesson.Id, lesson.Title, schoolClass.Id, schoolClass.Name, schoolClass.Grade, level, SchoolLevels.NameOf(level),
            lesson.Status, lesson.AccessCode, lesson.AccessCode is null ? null : $"/bai-hoc/{lesson.AccessCode}",
            lesson.Summary, keyPoints, sections.Select(s => LessonMapper.ToDto(s, materials)).ToList(), heritages,
            LessonFrameDto.From(frame), words, LessonMapper.ToDto(check), quizCount, quota, lesson.CreatedAt, lesson.UpdatedAt);
    }

    /// <summary>Di sản của bài chưa xóa mềm (kể cả đã ẩn: giáo viên cần thấy để thay).</summary>
    public async Task<List<LessonHeritageDto>> LoadHeritagesAsync(long lessonId, CancellationToken ct) =>
        await db.LessonHeritages.AsNoTracking()
            .Where(lh => lh.LessonId == lessonId && lh.Heritage!.DeletedAt == null)
            .OrderBy(lh => lh.HeritageId)
            .Select(lh => new LessonHeritageDto(lh.HeritageId, lh.Heritage!.Slug, lh.Heritage.Name, lh.Heritage.ThumbnailUrl, lh.Heritage.Status))
            .ToListAsync(ct);
}

/// <summary>
/// Bài học của giáo viên cho lớp của mình (màn hình Bài học của tôi, Tạo bài học, Soạn bài học, Mã bài học, Nhân bản bài học sang lớp khác).
/// Bài là của riêng giáo viên tạo và dành cho đúng một lớp; cấp của bài là cấp của lớp. Trạng thái: Nháp, Đã xuất bản (có mã bài học
/// sinh lần đầu xuất bản) và Lưu trữ; không trở về Nháp.
/// </summary>
public sealed class LessonService(
    IAppDbContext db,
    RequestValidator validator,
    PlanGuard plans,
    LessonMaterialService materialService,
    LessonDetailBuilder detailBuilder,
    ILessonCodeGenerator codeGenerator,
    SpeechWarmUp speechWarmUp,
    TimeProvider clock,
    ILogger<LessonService> logger)
{
    // ---- Danh sách, chi tiết, thư viện ----

    public async Task<LessonPageDto> ListAsync(Actor actor, LessonListQuery query, CancellationToken ct)
    {
        var lessons = db.LessonsOf(actor).AsNoTracking();
        if (query.ClassId is { } classId) lessons = lessons.Where(l => l.ClassId == classId);
        if (query.Status is { } status) lessons = lessons.Where(l => l.Status == status);
        if (query.HeritageType is { } type)
            lessons = lessons.Where(l => l.Heritages.Any(h => h.Heritage!.HeritageType == type));
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            lessons = lessons.Where(l => l.Title != null && l.Title.Contains(keyword));
        }
        if (query.Difficulty is { } difficulty)
        {
            // Độ khó của bài không lưu: tính từ các câu trong các bài kiểm tra chưa xóa của bài; bài chưa có bài kiểm tra nào có câu thì không khớp mức nào.
            var allDifficulties = await LessonDifficultyReader.ReadAsync(db, lessons.Select(l => l.Id), ct);
            var matching = allDifficulties.Where(x => x.Value == difficulty).Select(x => x.Key).ToList();
            lessons = lessons.Where(l => matching.Contains(l.Id));
        }

        var rows = lessons
            .OrderByDescending(l => l.UpdatedAt ?? l.CreatedAt).ThenByDescending(l => l.Id)
            .Select(l => new
            {
                Lesson = l,
                ClassName = l.Class!.Name,
                l.Class.Grade,
                Quizzes = db.Quizzes.Count(q => q.LessonId == l.Id && q.DeletedAt == null),
            });
        var page = await rows.ToPagedAsync(query.Page, query.PageSize, ct);

        var ids = page.Items.Select(r => r.Lesson.Id).ToList();
        var heritageRows = await db.LessonHeritages.AsNoTracking()
            .Where(lh => ids.Contains(lh.LessonId) && lh.Heritage!.DeletedAt == null)
            .OrderBy(lh => lh.HeritageId)
            .Select(lh => new { lh.LessonId, Dto = new LessonHeritageDto(lh.HeritageId, lh.Heritage!.Slug, lh.Heritage.Name, lh.Heritage.ThumbnailUrl, lh.Heritage.Status) })
            .ToListAsync(ct);
        var byLesson = heritageRows.ToLookup(h => h.LessonId, h => h.Dto);
        var difficulties = await LessonDifficultyReader.ReadAsync(db, ids, ct);

        var items = page.Items.Select(r =>
        {
            var level = difficulties.GetValueOrDefault(r.Lesson.Id);
            return new LessonRowDto(
                r.Lesson.Id, r.Lesson.Title, r.Lesson.ClassId, r.ClassName, r.Grade, SchoolClass.SchoolLevelOf((byte)r.Grade),
                r.Lesson.Status, r.Lesson.AccessCode, byLesson[r.Lesson.Id].ToList(), r.Quizzes, r.Lesson.CreatedAt, r.Lesson.UpdatedAt,
                level, LessonDifficulty.LabelOf(level));
        }).ToList();
        return new LessonPageDto(items, page.Page, page.PageSize, page.TotalItems, page.TotalPages);
    }

    public async Task<LessonDetailDto> GetAsync(Actor actor, long lessonId, CancellationToken ct)
    {
        var lesson = await db.FindLessonAsync(actor, lessonId, "xem bài học", tracking: false, ct);
        return await detailBuilder.BuildAsync(lesson, ct);
    }

    /// <summary>Ảnh và sự kiện của các di sản đang xuất bản của bài để chọn cho bước câu chuyện, hình, mốc thời gian.</summary>
    public async Task<LessonMaterialsDto> GetMaterialsAsync(Actor actor, long lessonId, CancellationToken ct)
    {
        var lesson = await db.FindLessonAsync(actor, lessonId, "xem thư viện ảnh của bài học", tracking: false, ct);
        var materials = await materialService.LoadAsync(await PublishedHeritageIdsAsync(lesson.Id, ct), ct);
        return materials.ToDto();
    }

    // ---- Tạo ----

    /// <summary>
    /// Tạo bài học nháp cho một lớp của giáo viên: cần gói hiệu lực; 1-10 di sản đã xuất bản; cấp của bài là cấp của lớp.
    /// </summary>
    public async Task<LessonDetailDto> CreateAsync(Actor actor, CreateLessonRequest request, CancellationToken ct)
    {
        if (actor.Role != RoleCode.TEACHER) throw AppException.Forbidden(Messages.Forbidden);
        var orgId = actor.RequireOrganizationId();
        await validator.EnsureValidAsync(request, ct);

        var schoolClass = await db.FindClassAsync(actor, request.ClassId!.Value, "tạo bài học", tracking: false, ct);
        await EnsureEffectivePlanAsync(orgId, "tạo bài học", ct);
        var heritageIds = request.HeritageIds!.Distinct().ToList();
        await EnsureAllPublishedAsync(heritageIds, ct);

        var lesson = new Lesson
        {
            TeacherId = actor.UserId,
            ClassId = schoolClass.Id,
            Title = request.Title!.Trim(),
            Status = ContentStatus.DRAFT,
            Heritages = heritageIds.Select(id => new LessonHeritage { HeritageId = id }).ToList(),
        };
        db.Lessons.Add(lesson);
        await db.SaveChangesAsync(ct);

        lesson.Class = schoolClass;
        return await detailBuilder.BuildAsync(lesson, ct);
    }

    // ---- Lưu ----

    /// <summary>
    /// Lưu bài học (bài Nháp hoặc Đã xuất bản; bài Lưu trữ phải xuất bản lại mới sửa được). Nội dung phải đúng khung của cấp lớp: loại phần
    /// dùng được theo cấp, hình chọn trong thư viện ảnh của các di sản của bài, mốc nối với sự kiện của các di sản của bài. Độ dài ngoài khoảng của
    /// cấp chỉ cảnh báo. Bài đã xuất bản sửa được, thay đổi hiện ngay cho học sinh và vẫn phải giữ đủ điều kiện xuất bản; không đủ thì không lưu gì.
    /// Di sản của bài chỉ đổi được khi bài còn Nháp.
    /// </summary>
    public async Task<LessonDetailDto> UpdateAsync(Actor actor, long lessonId, UpdateLessonRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var lesson = await db.FindLessonAsync(actor, lessonId, "lưu bài học", tracking: true, ct);
        if (lesson.Status == ContentStatus.ARCHIVED)
            throw AppException.BusinessRule(Messages.Blocked("lưu bài học", "Bài học đã lưu trữ. Hãy xuất bản lại trước khi sửa."));

        var level = SchoolClass.SchoolLevelOf(lesson.Class!.Grade);
        var frame = LessonFrames.For(level);

        var currentIds = await db.LessonHeritages.Where(lh => lh.LessonId == lesson.Id).Select(lh => lh.HeritageId).ToListAsync(ct);
        var newIds = request.HeritageIds is null ? currentIds : request.HeritageIds.Distinct().ToList();
        if (request.HeritageIds is not null && !newIds.ToHashSet().SetEquals(currentIds))
        {
            if (lesson.Status != ContentStatus.DRAFT)
                throw AppException.BusinessRule(Messages.Blocked("đổi di sản của bài", "Chỉ đổi được di sản khi bài học còn là bản nháp."));
            await EnsureAllPublishedAsync(newIds, ct);
            var removed = await db.LessonHeritages.Where(lh => lh.LessonId == lesson.Id && !newIds.Contains(lh.HeritageId)).ToListAsync(ct);
            db.LessonHeritages.RemoveRange(removed);
            foreach (var id in newIds.Except(currentIds)) db.LessonHeritages.Add(new LessonHeritage { LessonId = lesson.Id, HeritageId = id });
        }

        var publishedIds = await PublishedHeritageIdsAsync(newIds, ct);
        if (request.Title is not null) lesson.Title = request.Title.Trim();
        if (request.Summary is not null) lesson.Summary = string.IsNullOrWhiteSpace(request.Summary) ? null : request.Summary.Trim();
        if (request.KeyPoints is not null) lesson.KeyPoints = LessonJson.SerializeKeyPoints(request.KeyPoints);

        if (request.Sections is not null)
        {
            var materials = await materialService.LoadAsync(publishedIds, ct);
            var sections = BuildSections(request.Sections, frame, level, materials, now: Now);
            db.LessonSections.RemoveRange(lesson.Sections);
            lesson.Sections = [];
            foreach (var section in sections)
            {
                section.LessonId = lesson.Id;
                db.LessonSections.Add(section);
                lesson.Sections.Add(section);
            }
        }
        else if (request.HeritageIds is not null)
        {
            // Bớt di sản mà không gửi lại các phần: hình và sự kiện của di sản vừa bớt không còn hợp lệ trong bài, nên gỡ khỏi các phần.
            var materials = await materialService.LoadAsync(publishedIds, ct);
            foreach (var s in lesson.Sections)
            {
                if (s.ImageUrl is not null && !materials.ImagesByUrl.ContainsKey(s.ImageUrl)) s.ImageUrl = null;
                if (s.EventId is { } e && !materials.EventsById.ContainsKey(e)) s.EventId = null;
            }
        }

        lesson.UpdatedAt = Now;
        if (lesson.Status == ContentStatus.PUBLISHED)
        {
            var check = Evaluate(lesson, level, publishedIds.Count);
            if (!check.IsReady) throw new AppException(422, Messages.NotReady("lưu bài học đã xuất bản", check.Missing));
        }

        await db.SaveChangesAsync(ct);
        return await GetAsync(actor, lessonId, ct);
    }

    // ---- Xuất bản, lưu trữ, xóa ----

    /// <summary>
    /// Xuất bản bài Nháp hoặc xuất bản lại bài Lưu trữ, cần đủ điều kiện xuất bản. Lần xuất bản đầu hệ thống sinh MỘT mã bài học
    /// (<c>BH</c> + 6 ký tự, bỏ 0, O, 1, I; trùng thì thử mã khác, tối đa 5 lần); xuất bản lại giữ nguyên mã. Không cần gói hiệu lực.
    /// Xuất bản bài đã xuất bản không đổi gì (không sinh mã mới). Các lần xuất bản của cùng một lớp chạy tuần tự để hai lần bấm đồng thời
    /// không sinh hai mã khác nhau.
    /// </summary>
    public async Task<PublishLessonResultDto> PublishAsync(Actor actor, long lessonId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var classId = await db.LessonsOf(actor).Where(l => l.Id == lessonId).Select(l => (long?)l.ClassId).SingleOrDefaultAsync(ct)
                      ?? throw AppException.NotFound(Messages.NotFound("xuất bản bài học", "bài học của bạn"));
        await db.LockClassAsync(classId, ct);

        var lesson = await db.FindLessonAsync(actor, lessonId, "xuất bản bài học", tracking: true, ct);
        var level = SchoolClass.SchoolLevelOf(lesson.Class!.Grade);

        var codeCreated = false;
        var becamePublished = false;
        if (lesson.Status != ContentStatus.PUBLISHED)
        {
            var publishedIds = await PublishedHeritageIdsAsync(lesson.Id, ct);
            var check = Evaluate(lesson, level, publishedIds.Count);
            if (!check.IsReady) throw new AppException(422, Messages.NotReady("xuất bản bài học", check.Missing));

            lesson.Status = ContentStatus.PUBLISHED;
            lesson.UpdatedAt = Now;
            becamePublished = true;
            if (lesson.AccessCode is null)
            {
                await AssignNewCodeAsync(lesson, ct);
                codeCreated = true;
            }
            else
            {
                await db.SaveChangesAsync(ct);
            }
        }
        await tx.CommitAsync(ct);

        // Bài của lớp cấp 1 vừa xuất bản thì xếp các câu sẽ đọc to vào hàng đợi tạo giọng nền; không chờ và không bao giờ làm hỏng lần xuất bản.
        if (becamePublished) await speechWarmUp.WarmLessonAsync(lessonId, ct);

        var detail = await GetAsync(actor, lessonId, ct);
        var message = codeCreated
            ? Messages.LessonPublished(detail.AccessCode!, detail.ClassName ?? string.Empty)
            : Messages.Success("Xuất bản bài học");
        return new PublishLessonResultDto(detail, codeCreated, detail.AccessCode!, message.Text);
    }

    /// <summary>
    /// Lưu trữ bài Đã xuất bản: học sinh không mở bằng mã được nữa, kết quả cũ giữ nguyên. Bị chặn khi bài còn bài kiểm tra đang mở
    /// (đã phát hành, chưa tới giờ đóng). Bài Nháp không lưu trữ (xóa nếu không dùng nữa).
    /// </summary>
    public async Task<LessonDetailDto> ArchiveAsync(Actor actor, long lessonId, CancellationToken ct)
    {
        var lesson = await db.FindLessonAsync(actor, lessonId, "lưu trữ bài học", tracking: true, ct);
        if (lesson.Status == ContentStatus.ARCHIVED) return await detailBuilder.BuildAsync(lesson, ct);
        if (lesson.Status != ContentStatus.PUBLISHED)
            throw AppException.BusinessRule(Messages.Blocked("lưu trữ bài học", "Chỉ lưu trữ được bài học đã xuất bản. Bài nháp thì xóa nếu không dùng nữa."));

        var now = clock.GetUtcNow().UtcDateTime;
        var hasOpenQuiz = await db.Quizzes.AnyAsync(q => q.LessonId == lessonId && q.DeletedAt == null
            && q.Status == QuizStatus.PUBLISHED && (q.CloseAt == null || q.CloseAt > now), ct);
        if (hasOpenQuiz)
            throw AppException.BusinessRule(Messages.Blocked("lưu trữ bài học", "Bài học còn bài kiểm tra đang mở. Hãy đóng bài kiểm tra trước."));

        lesson.Status = ContentStatus.ARCHIVED;
        lesson.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return await detailBuilder.BuildAsync(lesson, ct);
    }

    /// <summary>Xóa mềm bài chưa có bài kiểm tra nào; bài đã có bài kiểm tra chỉ lưu trữ. Mã bài học của bài đã xóa không cấp lại.</summary>
    public async Task DeleteAsync(Actor actor, long lessonId, CancellationToken ct)
    {
        var lesson = await db.FindLessonAsync(actor, lessonId, "xóa bài học", tracking: true, ct);
        if (await db.Quizzes.AnyAsync(q => q.LessonId == lessonId && q.DeletedAt == null, ct))
            throw AppException.BusinessRule(Messages.Blocked("xóa bài học", "Bài học đã có bài kiểm tra. Hãy lưu trữ bài học thay vì xóa."));
        lesson.DeletedAt = Now;
        await db.SaveChangesAsync(ct);
    }

    // ---- Nhân bản ----

    /// <summary>
    /// Nhân bản bài (mọi trạng thái) sang MỘT lớp khác do chính giáo viên phụ trách, cần gói hiệu lực, không tốn lượt AI. Tạo bài Nháp mới
    /// chưa có mã ở lớp đích, chép tiêu đề, tóm tắt, ý chính, các phần nội dung và di sản (di sản đã ẩn bị bỏ và báo số lượng; hình và mốc
    /// lấy từ di sản bị bỏ cũng bị gỡ). Lớp đích khác cấp thì vẫn chép nguyên nội dung kèm lời nhắc xem lại hoặc nhờ AI soạn lại.
    /// Tùy chọn <c>IncludeQuizzes</c> chép kèm các bài kiểm tra chưa xóa của bài thành bài kiểm tra nháp của bài mới: cùng câu, điểm, thứ tự, mô tả, độ khó,
    /// xáo câu, cho xem đáp án; giờ mở, giờ đóng để trống; câu đã ẩn hoặc xóa và câu có dạng hoặc cấp không dùng được ở cấp của lớp đích bị bỏ (báo số câu);
    /// thời gian làm chuẩn hóa theo cấp lớp đích. Không chép lượt làm bài. Bài gốc không đổi.
    /// </summary>
    public async Task<DuplicateLessonResultDto> DuplicateAsync(Actor actor, long lessonId, DuplicateLessonRequest request, CancellationToken ct)
    {
        if (actor.Role != RoleCode.TEACHER) throw AppException.Forbidden(Messages.Forbidden);
        var orgId = actor.RequireOrganizationId();
        await validator.EnsureValidAsync(request, ct);

        var source = await db.FindLessonAsync(actor, lessonId, "nhân bản bài học", tracking: false, ct);
        var target = await db.FindClassAsync(actor, request.TargetClassId!.Value, "nhân bản bài học", tracking: false, ct);
        if (target.Id == source.ClassId)
            throw AppException.Validation("targetClassId", Messages.Invalid("Lớp đích", "phải là một lớp khác lớp của bài gốc"));
        await EnsureEffectivePlanAsync(orgId, "nhân bản bài học", ct);

        var sourceHeritageIds = await db.LessonHeritages.AsNoTracking().Where(lh => lh.LessonId == lessonId)
            .Select(lh => lh.HeritageId).ToListAsync(ct);
        var keptIds = await PublishedHeritageIdsAsync(sourceHeritageIds, ct);
        var skipped = sourceHeritageIds.Count - keptIds.Count;
        var materials = await materialService.LoadAsync(keptIds, ct);

        var copy = new Lesson
        {
            TeacherId = actor.UserId,
            ClassId = target.Id,
            Title = source.Title,
            Summary = source.Summary,
            KeyPoints = source.KeyPoints,
            Status = ContentStatus.DRAFT,
            Heritages = keptIds.Select(id => new LessonHeritage { HeritageId = id }).ToList(),
        };
        var order = 0;
        foreach (var s in source.Sections.OrderBy(s => s.SortOrder).ThenBy(s => s.Id))
        {
            var imageOk = s.ImageUrl is null || materials.ImagesByUrl.ContainsKey(s.ImageUrl);
            if (!imageOk && s.SectionType == LessonSectionType.IMAGE) continue;
            copy.Sections.Add(new LessonSection
            {
                SectionType = s.SectionType,
                Title = s.Title,
                Body = s.Body,
                ImageUrl = imageOk ? s.ImageUrl : null,
                MilestoneYear = s.MilestoneYear,
                EventId = s.EventId is { } e && materials.EventsById.ContainsKey(e) ? e : null,
                SourceCitation = s.SourceCitation,
                SortOrder = ++order,
            });
        }
        var targetLevel = SchoolClass.SchoolLevelOf(target.Grade);
        var copiedQuizzes = 0;
        var skippedQuestions = 0;
        if (request.IncludeQuizzes)
        {
            var sourceQuizzes = await db.Quizzes.AsNoTracking()
                .Include(q => q.Questions).ThenInclude(x => x.Question)
                .Where(q => q.LessonId == lessonId && q.DeletedAt == null)
                .OrderBy(q => q.Id).ToListAsync(ct);
            foreach (var quiz in sourceQuizzes)
            {
                var (quizCopy, skippedHere) = QuizCopier.Copy(quiz, actor.UserId, name: null, targetLevel);
                quizCopy.Lesson = copy;
                db.Quizzes.Add(quizCopy);
                copiedQuizzes++;
                skippedQuestions += skippedHere;
            }
        }
        db.Lessons.Add(copy);
        await db.SaveChangesAsync(ct);

        var sourceLevel = SchoolClass.SchoolLevelOf(source.Class!.Grade);
        var levelNotice = sourceLevel == targetLevel
            ? null
            : Messages.LessonLevelDiffers(target.Name ?? string.Empty, targetLevel, sourceLevel);

        var detail = await GetAsync(actor, copy.Id, ct);
        return new DuplicateLessonResultDto(detail, skipped, levelNotice,
            Messages.LessonDuplicated(target.Name ?? string.Empty, skipped, copiedQuizzes, skippedQuestions).Text, copiedQuizzes, skippedQuestions);
    }

    // ---- Nội bộ ----

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private async Task EnsureEffectivePlanAsync(long organizationId, string action, CancellationToken ct)
    {
        if (await plans.GetEffectiveAsync(organizationId, ct) is null) throw new AppException(422, Messages.PlanRequired(action));
    }

    private async Task EnsureAllPublishedAsync(IReadOnlyList<long> ids, CancellationToken ct)
    {
        var published = await PublishedHeritageIdsAsync(ids, ct);
        if (published.Count != ids.Count)
            throw AppException.Validation("heritageIds", Messages.Invalid("Di sản của bài", "chỉ chọn được di sản đã xuất bản"));
    }

    private async Task<List<long>> PublishedHeritageIdsAsync(IReadOnlyCollection<long> ids, CancellationToken ct) =>
        await db.Heritages.AsNoTracking().Where(h => ids.Contains(h.Id) && h.DeletedAt == null && h.Status == ContentStatus.PUBLISHED)
            .Select(h => h.Id).ToListAsync(ct);

    private async Task<List<long>> PublishedHeritageIdsAsync(long lessonId, CancellationToken ct) =>
        await db.LessonHeritages.AsNoTracking()
            .Where(lh => lh.LessonId == lessonId && lh.Heritage!.DeletedAt == null && lh.Heritage.Status == ContentStatus.PUBLISHED)
            .Select(lh => lh.HeritageId).ToListAsync(ct);

    private static LessonCheck Evaluate(Lesson lesson, int level, int publishedHeritageCount) =>
        LessonChecks.Evaluate(level, new LessonContent(lesson.Title, publishedHeritageCount, lesson.Summary,
            LessonJson.ParseKeyPoints(lesson.KeyPoints), lesson.Sections.OrderBy(s => s.SortOrder).Select(LessonMapper.ToView).ToList()));

    /// <summary>Đổi các phần gửi lên thành thực thể theo thứ tự gửi, kiểm loại theo cấp, hình và sự kiện theo thư viện của các di sản của bài.</summary>
    private static List<LessonSection> BuildSections(IReadOnlyList<LessonSectionRequest> requests, LevelFrame frame, int level,
        LessonMaterials materials, DateTime now)
    {
        var sections = new List<LessonSection>(requests.Count);
        var errors = new Dictionary<string, string>();
        for (var i = 0; i < requests.Count; i++)
        {
            var r = requests[i];
            var type = r.Type!.Value;
            var at = $"sections[{i}]";
            if (!frame.Allows(type))
            {
                errors[$"{at}.type"] = Messages.Invalid("Loại phần", $"\"{LessonFrames.LabelOf(type)}\" không dùng được ở bài của lớp cấp {level}").Text;
                continue;
            }

            var imageUrl = ValidationRules.TrimToNull(r.ImageUrl);
            if (imageUrl is not null && !materials.ImagesByUrl.ContainsKey(imageUrl))
                errors[$"{at}.imageUrl"] = Messages.Invalid("Hình", "phải chọn từ thư viện ảnh của các di sản của bài").Text;

            long? eventId = null;
            if (type == LessonSectionType.MILESTONE && r.EventId is { } e)
            {
                if (materials.EventsById.ContainsKey(e)) eventId = e;
                else errors[$"{at}.eventId"] = Messages.Invalid("Sự kiện", "phải là sự kiện trên dòng thời gian của các di sản của bài").Text;
            }

            sections.Add(new LessonSection
            {
                SectionType = type,
                Title = ValidationRules.TrimToNull(r.Title),
                Body = ValidationRules.TrimToNull(r.Body),
                ImageUrl = imageUrl,
                MilestoneYear = type == LessonSectionType.MILESTONE ? r.MilestoneYear : null,
                EventId = eventId,
                SourceCitation = type == LessonSectionType.SOURCE_EXCERPT ? ValidationRules.TrimToNull(r.SourceCitation) : null,
                SortOrder = i + 1,
                CreatedAt = now,
            });
        }

        if (errors.Count > 0)
        {
            var first = errors.First();
            throw new AppException(400, new AppMessage(MessageCodes.Invalid, first.Value), errors);
        }
        return sections;
    }

    /// <summary>
    /// Sinh mã chưa có (kể cả của bài đã xóa: mã không cấp lại) rồi lưu cùng thay đổi trạng thái. Hai yêu cầu đồng thời sinh cùng mã thì
    /// ràng buộc không trùng chặn một bên, bên đó sinh mã khác; tối đa 5 lần, vẫn trùng thì báo lỗi hệ thống và ghi log.
    /// </summary>
    private async Task AssignNewCodeAsync(Lesson lesson, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= LessonCodes.MaxGenerateAttempts; attempt++)
        {
            var code = codeGenerator.NewCode();
            if (await db.Lessons.AnyAsync(l => l.AccessCode == code, ct)) continue;

            lesson.AccessCode = code;
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex)
            {
                logger.LogWarning(ex, "Mã bài học bị trùng ở lần thử {Attempt}/{Max}, sinh mã khác", attempt, LessonCodes.MaxGenerateAttempts);
                lesson.AccessCode = null;
            }
        }
        logger.LogError("Không sinh được mã bài học không trùng sau {Max} lần thử cho bài {LessonId}", LessonCodes.MaxGenerateAttempts, lesson.Id);
        throw new InvalidOperationException("Không sinh được mã bài học không trùng.");
    }
}
