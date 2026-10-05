using DSVHVN.Application.Common;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
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
/// Học sinh mở bài học bằng mã bài học (web học tập và app chế độ Học sinh). Chỉ đọc: mở bài không ghi vào CSDL.
/// <list type="bullet">
/// <item>Phải đăng nhập (vai trò học sinh, không còn bị buộc đổi mật khẩu).</item>
/// <item>Mã đúng khi: tồn tại, bài chưa xóa và đang Đã xuất bản, bài thuộc đúng lớp của em, lớp chưa xóa, trường chưa ngừng, em còn hoạt động.
/// Sai bất kỳ điều kiện nào đều trả cùng một thông báo (không cho biết mã có ở lớp khác hay không) và tính vào giới hạn nhập sai.</item>
/// <item>Hợp lệ thì trả bài theo khung và luồng của cấp lớp em: nội dung, các phần của cấp em, ý chính, thẻ di sản đang xuất bản,
/// bài kiểm tra đã phát hành của bài kèm trạng thái với riêng em. Không có thông tin liên hệ của giáo viên.</item>
/// <item>Mở bài không phụ thuộc gói của trường.</item>
/// </list>
/// </summary>
public sealed class StudentLessonService(
    IAppDbContext db,
    LessonMaterialService materialService,
    ILessonCodeThrottle throttle,
    TimeProvider clock)
{
    public async Task<StudentLessonDto> OpenByCodeAsync(Actor actor, string? rawCode, CancellationToken ct)
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

        var lesson = LessonCodes.IsWellFormed(code)
            ? await db.Lessons.AsNoTracking()
                .Where(l => l.AccessCode == code && l.DeletedAt == null && l.Status == ContentStatus.PUBLISHED && l.ClassId == student.ClassId)
                .Include(l => l.Sections)
                .SingleOrDefaultAsync(ct)
            : null;

        if (lesson is null)
        {
            if (throttle.RegisterFailure(actor.UserId, actor.IpAddress) is { } nowBlocked)
                throw new AppException(429, Messages.LessonCodeBlocked(nowBlocked));
            throw AppException.NotFound(Messages.LessonCodeInvalid);
        }

        var level = SchoolClass.SchoolLevelOf(student.Grade);
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
            lesson.Id, lesson.AccessCode!, lesson.Title, student.ClassName, student.TeacherName, level, SchoolLevels.NameOf(level),
            new StudentLayoutDto(frame.Layout, frame.SequentialFlow, frame.Flow),
            lesson.Summary, LessonJson.ParseKeyPoints(lesson.KeyPoints), sections, heritages,
            await LoadQuizzesAsync(lesson.Id, student.Id, ct));
    }

    /// <summary>Bài kiểm tra đã phát hành hoặc đã đóng của bài; DRAFT không hiện. Trạng thái tính riêng cho em từ lượt làm bài của em.</summary>
    private async Task<List<StudentQuizDto>> LoadQuizzesAsync(long lessonId, long studentId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var quizzes = await db.Quizzes.AsNoTracking()
            .Where(q => q.LessonId == lessonId && q.DeletedAt == null && (q.Status == QuizStatus.PUBLISHED || q.Status == QuizStatus.CLOSED))
            .OrderBy(q => q.OpenAt ?? q.PublishedAt).ThenBy(q => q.Id)
            .Select(q => new
            {
                q.Id, q.Name, q.Description, q.OpenAt, q.CloseAt, q.TimeLimitMinutes, q.Status,
                Questions = q.Questions.Count,
                Attempt = db.Attempts.Where(a => a.QuizId == q.Id && a.StudentId == studentId).Select(a => a.Status).FirstOrDefault(),
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
