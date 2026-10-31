using DSVHVN.Application.Common;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Lessons;

/// <summary>
/// Giới hạn nhập sai mã bài học, đếm trong bộ nhớ máy chủ (không có bảng): mỗi tài khoản học sinh tối đa 10 lần sai trong 10 phút, vượt thì
/// bị chặn nhập mã 10 phút; mỗi địa chỉ IP tối đa 30 lần sai trong 10 phút (cả lớp thường chung một IP của trường), vượt thì chặn IP đó 15 phút.
/// Đang bị chặn thì không xét mã (kể cả mã đúng). Học sinh vẫn học và làm bài ở các bài đã mở.
/// </summary>
public interface ILessonCodeThrottle
{
    /// <summary>Thời gian còn bị chặn nhập mã theo tài khoản hoặc theo IP (lấy lâu hơn), hoặc null nếu không bị chặn.</summary>
    TimeSpan? GetBlock(long userId, string? ipAddress);

    /// <summary>Ghi một lần nhập sai. Trả thời gian chặn nếu lần sai này làm khóa nhập mã bắt đầu (theo tài khoản hoặc theo IP), không thì null.</summary>
    TimeSpan? RegisterFailure(long userId, string? ipAddress);
}

/// <summary>
/// Học sinh mở bài học bằng mã bài học hoặc vào thẳng bài kiểm tra bằng mã bài kiểm tra (web học tập và app chế độ Học sinh). Mở bài ghi lại lượt mở của em
/// (<c>lesson_visits</c>) để bài nằm trong "Bài học của em" ở mọi thiết bị, kể cả bài chưa có bài kiểm tra.
/// <list type="bullet">
/// <item>Phải đăng nhập (vai trò học sinh, không còn bị buộc đổi mật khẩu).</item>
/// <item>Mã đúng khi: tồn tại, bài chưa xóa và đang Đã xuất bản, bài thuộc đúng lớp của em, lớp chưa xóa, trường chưa ngừng, em còn hoạt động.
/// Sai bất kỳ điều kiện nào đều trả cùng một thông báo (không cho biết mã có ở lớp khác hay không) và tính vào giới hạn nhập sai.</item>
/// <item>Hợp lệ thì trả bài theo khung và luồng của cấp lớp em: nội dung, các phần của cấp em, ý chính, thẻ di sản đang xuất bản,
/// bài kiểm tra đã phát hành của bài kèm trạng thái với riêng em. Không có thông tin liên hệ của giáo viên.</item>
/// <item>Mở bài không phụ thuộc gói của trường.</item>
/// </list>
/// Cùng cách kiểm mã (và cùng giới hạn nhập sai) dùng cho hai thao tác của bài cấp 1: ghi em đã học xong bài và lấy các câu ôn của bước Nhớ lại
/// (<see cref="LessonProgressService"/>).
/// </summary>
public sealed class StudentLessonService(
    IAppDbContext db,
    LessonMaterialService materialService,
    ILessonCodeThrottle throttle,
    TimeProvider clock)
{
    /// <summary>
    /// Em và bài học đã qua bước kiểm mã: dùng chung cho mở bài, ghi bài đã học xong và lấy câu ôn. <paramref name="FocusQuizId"/> có giá trị khi em gõ mã bài kiểm tra.
    /// </summary>
    internal sealed record ResolvedLesson(
        long StudentId, long ClassId, byte Grade, string? ClassName, string? TeacherName, Lesson Lesson, long? FocusQuizId = null);

    /// <summary>
    /// Mở bằng mã bài học hoặc mã bài kiểm tra. Với mã bài kiểm tra, kết quả vẫn là bài học chứa bài đó, kèm <see cref="StudentLessonDto.FocusQuizId"/> để giao diện vào thẳng bài kiểm tra.
    /// </summary>
    public async Task<StudentLessonDto> OpenByCodeAsync(Actor actor, string? rawCode, CancellationToken ct)
    {
        var resolved = await ResolveByCodeAsync(actor, rawCode, includeSections: true, ct, allowQuizCode: true);
        await RememberVisitAsync(resolved.StudentId, resolved.Lesson.Id, ct);
        return await BuildAsync(resolved.Lesson, resolved.Grade, resolved.ClassName, resolved.TeacherName, resolved.StudentId, ct, resolved.FocusQuizId);
    }

    /// <summary>Mỗi em mỗi bài một dòng: mở lần đầu thì thêm, mở lại thì cập nhật giờ. Hai yêu cầu cùng thêm một lúc thì ràng buộc không trùng chỉ cho một dòng.</summary>
    private async Task RememberVisitAsync(long studentId, long lessonId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var visit = await db.LessonVisits.SingleOrDefaultAsync(v => v.StudentId == studentId && v.LessonId == lessonId, ct);
        if (visit is not null)
        {
            visit.LastOpenedAt = now;
            await db.SaveChangesAsync(ct);
            return;
        }

        visit = new LessonVisit { StudentId = studentId, LessonId = lessonId, FirstOpenedAt = now, LastOpenedAt = now };
        db.LessonVisits.Add(visit);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.LessonVisits.Entry(visit).State = EntityState.Detached;
            if (!await db.LessonVisits.AnyAsync(v => v.StudentId == studentId && v.LessonId == lessonId, ct)) throw;
        }
    }

    /// <summary>
    /// Kiểm mã bài học của em: phải là học sinh, chưa bị chặn nhập mã; mã đúng khi bài chưa xóa, Đã xuất bản và thuộc lớp của em. Sai thì cùng một thông báo 404
    /// và tính vào giới hạn nhập sai. <paramref name="allowQuizCode"/> nhận thêm mã bài kiểm tra đã phát hành hoặc đã đóng của bài thuộc lớp em.
    /// </summary>
    internal async Task<ResolvedLesson> ResolveByCodeAsync(Actor actor, string? rawCode, bool includeSections, CancellationToken ct, bool allowQuizCode = false)
    {
        if (actor.Role != RoleCode.STUDENT) throw AppException.Forbidden(Messages.Forbidden);

        var code = LessonCodes.Normalize(rawCode);
        if (code.Length == 0) throw AppException.Validation("code", Messages.Required("mã bài học"));

        if (throttle.GetBlock(actor.UserId, actor.IpAddress) is { } blocked)
            throw new AppException(429, Messages.LessonCodeBlocked(blocked));

        var student = await db.Students.AsNoTracking()
            .Where(s => s.UserId == actor.UserId && s.DeletedAt == null && s.IsActive && s.Class!.DeletedAt == null)
            .Select(s => new { s.Id, s.ClassId, s.Class!.Grade, ClassName = s.Class.Name, TeacherName = s.Class.Teacher!.FullName })
            .SingleOrDefaultAsync(ct)
            ?? throw AppException.Unauthorized(Messages.Forbidden);

        var query = db.Lessons.AsNoTracking()
            .Where(l => l.DeletedAt == null && l.Status == ContentStatus.PUBLISHED && l.ClassId == student.ClassId);
        if (includeSections) query = query.Include(l => l.Sections);

        Lesson? lesson = null;
        long? focusQuizId = null;
        if (LessonCodes.IsWellFormed(code))
        {
            lesson = await query.SingleOrDefaultAsync(l => l.AccessCode == code, ct);
        }
        else if (allowQuizCode && QuizCodes.IsWellFormed(code))
        {
            var quiz = await db.Quizzes.AsNoTracking()
                .Where(q => q.AccessCode == code && q.DeletedAt == null && (q.Status == QuizStatus.PUBLISHED || q.Status == QuizStatus.CLOSED))
                .Select(q => new { q.Id, q.LessonId })
                .SingleOrDefaultAsync(ct);
            if (quiz is not null)
            {
                lesson = await query.SingleOrDefaultAsync(l => l.Id == quiz.LessonId, ct);
                if (lesson is not null) focusQuizId = quiz.Id;
            }
        }

        if (lesson is null)
        {
            if (throttle.RegisterFailure(actor.UserId, actor.IpAddress) is { } nowBlocked)
                throw new AppException(429, Messages.LessonCodeBlocked(nowBlocked));
            throw AppException.NotFound(Messages.LessonCodeInvalid);
        }

        return new ResolvedLesson(student.Id, student.ClassId, student.Grade, student.ClassName, student.TeacherName, lesson, focusQuizId);
    }

    /// <summary>
    /// Giáo viên phụ trách bài xem thử bài như học sinh của lớp (nút "Xem như học sinh" ở Soạn bài học): đúng khung và luồng của cấp lớp,
    /// kể cả bài Nháp chưa có mã và bài Lưu trữ. <b>Chỉ đọc, không ghi gì</b>: không tạo lượt làm bài, tiến độ hay lịch sử, không gọi Dịch vụ AI,
    /// không tính lượt AI, không đếm vào giới hạn nhập mã. Bài kiểm tra hiện như học sinh chưa làm (trạng thái tính theo giờ mở, đóng) và
    /// có <see cref="LessonPreviewDto.QuizzesViewOnly"/> để giao diện chỉ cho xem câu hỏi, không nộp. Bài của giáo viên khác hoặc trường khác trả 404;
    /// vai trò khác giáo viên trả 403.
    /// </summary>
    public async Task<LessonPreviewDto> PreviewAsync(Actor actor, long lessonId, CancellationToken ct)
    {
        if (actor.Role != RoleCode.TEACHER) throw AppException.Forbidden(Messages.Forbidden);
        var lesson = await db.FindLessonAsync(actor, lessonId, "xem như học sinh", tracking: false, ct);
        var teacherName = await db.Users.AsNoTracking().Where(u => u.Id == actor.UserId).Select(u => u.FullName).SingleOrDefaultAsync(ct);
        var view = await BuildAsync(lesson, lesson.Class!.Grade, lesson.Class.Name, teacherName, studentId: null, ct);
        return new LessonPreviewDto(view, lesson.Status, QuizzesViewOnly: true);
    }

    private async Task<StudentLessonDto> BuildAsync(
        Lesson lesson, byte grade, string? className, string? teacherName, long? studentId, CancellationToken ct, long? focusQuizId = null)
    {
        var level = SchoolClass.SchoolLevelOf(grade);
        var frame = LessonFrames.For(level);

        var heritages = await db.LessonHeritages.AsNoTracking()
            .Where(lh => lh.LessonId == lesson.Id && lh.Heritage!.DeletedAt == null && lh.Heritage.Status == ContentStatus.PUBLISHED)
            .OrderBy(lh => lh.HeritageId)
            .Select(lh => new StudentHeritageCardDto(
                lh.HeritageId, lh.Heritage!.Slug!, lh.Heritage.Name!, lh.Heritage.ShortDescription, lh.Heritage.ThumbnailUrl,
                lh.Heritage.Province == null ? null : lh.Heritage.Province.Name, lh.Heritage.Latitude, lh.Heritage.Longitude,
                lh.Heritage.Timelines.Any()))
            .ToListAsync(ct);

        var allHeritageIds = await db.LessonHeritages.AsNoTracking().Where(lh => lh.LessonId == lesson.Id)
            .Select(lh => lh.HeritageId).ToListAsync(ct);
        var materials = await materialService.LoadAsync(allHeritageIds, ct);

        var sections = lesson.Sections.Where(s => frame.Allows(s.SectionType))
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
            .Select(s => LessonMapper.ToStudentDto(s, materials)).ToList();

        return new StudentLessonDto(
            lesson.Id, lesson.AccessCode ?? string.Empty, lesson.Title, className, teacherName, level, SchoolLevels.NameOf(level),
            new StudentLayoutDto(frame.Layout, frame.SequentialFlow, frame.Flow),
            lesson.Summary, LessonJson.ParseKeyPoints(lesson.KeyPoints), sections, heritages,
            await LoadQuizzesAsync(lesson.Id, studentId, ct), focusQuizId);
    }

    /// <summary>Bài kiểm tra đã phát hành hoặc đã đóng của bài; DRAFT không hiện. Trạng thái tính riêng cho em từ lượt làm bài của em.</summary>
    private async Task<List<StudentQuizDto>> LoadQuizzesAsync(long lessonId, long? studentId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var quizzes = await db.Quizzes.AsNoTracking()
            .Where(q => q.LessonId == lessonId && q.DeletedAt == null && (q.Status == QuizStatus.PUBLISHED || q.Status == QuizStatus.CLOSED))
            .OrderBy(q => q.OpenAt ?? q.PublishedAt).ThenBy(q => q.Id)
            .Select(q => new
            {
                q.Id, q.Name, q.Description, q.OpenAt, q.CloseAt, q.TimeLimitMinutes, q.Status,
                Questions = q.Questions.Count,
                Attempt = studentId == null ? null : db.Attempts.Where(a => a.QuizId == q.Id && a.StudentId == studentId).Select(a => (AttemptStatus?)a.Status).FirstOrDefault(),
            })
            .ToListAsync(ct);

        return quizzes.Select(q =>
        {
            var state = StateOf(q.Status, q.OpenAt, q.CloseAt, q.Attempt, now);
            return new StudentQuizDto(q.Id, q.Name, q.Description, q.OpenAt, q.CloseAt, q.TimeLimitMinutes, q.Questions, state, LabelOf(state));
        }).ToList();
    }

    /// <summary>Em đang làm hoặc đã làm thì theo lượt của em; chưa làm thì chưa mở, đang mở hoặc đã đóng theo giờ và trạng thái của bài kiểm tra.</summary>
    public static StudentQuizState StateOf(QuizStatus? status, DateTime? openAt, DateTime? closeAt, AttemptStatus? attempt, DateTime now)
    {
        if (attempt == AttemptStatus.IN_PROGRESS) return StudentQuizState.IN_PROGRESS;
        if (attempt is AttemptStatus.SUBMITTED or AttemptStatus.EXPIRED) return StudentQuizState.SUBMITTED;
        if (status == QuizStatus.CLOSED || (closeAt is { } close && now >= close)) return StudentQuizState.CLOSED;
        if (openAt is { } open && now < open) return StudentQuizState.NOT_OPEN;
        return StudentQuizState.OPEN;
    }

    public static string LabelOf(StudentQuizState state) => state switch
    {
        StudentQuizState.NOT_OPEN => "Chưa mở",
        StudentQuizState.OPEN => "Đang mở",
        StudentQuizState.IN_PROGRESS => "Em đang làm",
        StudentQuizState.SUBMITTED => "Em đã làm",
        _ => "Đã đóng",
    };
}
