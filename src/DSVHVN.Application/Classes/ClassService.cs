using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Classes;

/// <summary>
/// Quản lý lớp của giáo viên (màn hình Lớp học): tạo, sửa, xóa mềm. Mọi truy vấn lọc theo trường và giáo viên phụ trách của người gọi.
/// Lớp thuộc trường của giáo viên tạo, do chính người đó phụ trách và không đổi người phụ trách. Không cần gói hiệu lực.
/// </summary>
public sealed class ClassService(IAppDbContext db, RequestValidator validator, IAuditLogger audit, TimeProvider clock)
{
    public async Task<ClassPageDto> ListAsync(Actor actor, ClassListQuery query, CancellationToken ct)
    {
        var classes = db.ClassesOf(actor).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            classes = classes.Where(c => c.Name != null && c.Name.Contains(keyword));
        }
        if (!string.IsNullOrWhiteSpace(query.SchoolYear))
        {
            var year = query.SchoolYear.Trim();
            classes = classes.Where(c => c.SchoolYear == year);
        }
        if (query.Grade is { } grade) classes = classes.Where(c => c.Grade == grade);

        var rows = classes
            .OrderByDescending(c => c.SchoolYear).ThenBy(c => c.Grade).ThenBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new ClassRow(
                c,
                db.Students.Count(s => s.ClassId == c.Id && s.DeletedAt == null && s.IsActive),
                db.Lessons.Count(l => l.ClassId == c.Id && l.DeletedAt == null),
                db.Students.Count(s => s.ClassId == c.Id && s.DeletedAt == null && s.IsActive && s.User!.LastLoginAt == null)));
        var page = await rows.ToPagedAsync(query.Page, query.PageSize, ct);
        return new ClassPageDto(
            page.Items.Select(r => ClassDto.From(r.Class, r.Active, r.Lessons, r.Never)).ToList(),
            page.Page, page.PageSize, page.TotalItems, page.TotalPages);
    }

    private sealed record ClassRow(SchoolClass Class, int Active, int Lessons, int Never);

    public async Task<ClassDto> GetAsync(Actor actor, long classId, CancellationToken ct)
    {
        var c = await db.FindClassAsync(actor, classId, "xem lớp", tracking: false, ct);
        return await ToDtoAsync(c, ct);
    }

    public async Task<ClassDto> CreateAsync(Actor actor, ClassRequest request, CancellationToken ct)
    {
        if (actor.Role != RoleCode.TEACHER) throw AppException.Forbidden(Messages.Forbidden);
        var orgId = actor.RequireOrganizationId();
        await validator.EnsureValidAsync(request, ct);

        var name = request.Name!.Trim();
        var year = request.SchoolYear!.Trim();
        await EnsureNameFreeAsync(actor, name, year, exceptClassId: null, ct);

        var schoolClass = new SchoolClass
        {
            OrganizationId = orgId, TeacherId = actor.UserId, Name = name, Grade = (byte)request.Grade!.Value, SchoolYear = year,
        };
        db.Classes.Add(schoolClass);
        await db.SaveChangesAsync(ct);
        return ClassDto.From(schoolClass, 0, 0, 0);
    }

    /// <summary>
    /// Sửa tên, năm học, khối lớp. Sửa khối lớp sang cấp khác khi lớp đã có bài học thì phải có lời xác nhận
    /// (<see cref="ClassRequest.ConfirmLevelChange"/>): giao diện học của học sinh đổi theo cấp mới từ lần tải trang hoặc mở app kế tiếp,
    /// bài học và bài kiểm tra đã tạo giữ nguyên nội dung.
    /// </summary>
    public async Task<ClassDto> UpdateAsync(Actor actor, long classId, ClassRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var schoolClass = await db.FindClassAsync(actor, classId, "sửa lớp", tracking: true, ct);

        var name = request.Name!.Trim();
        var year = request.SchoolYear!.Trim();
        var newGrade = (byte)request.Grade!.Value;
        await EnsureNameFreeAsync(actor, name, year, schoolClass.Id, ct);

        var oldLevel = SchoolClass.SchoolLevelOf(schoolClass.Grade);
        var newLevel = SchoolClass.SchoolLevelOf(newGrade);
        if (oldLevel != newLevel && !request.ConfirmLevelChange
            && await db.Lessons.AnyAsync(l => l.ClassId == classId && l.DeletedAt == null, ct))
            throw new AppException(422, Messages.ClassLevelChangeNeedsConfirmation(schoolClass.Name ?? string.Empty, oldLevel, newLevel));

        schoolClass.Name = name;
        schoolClass.SchoolYear = year;
        schoolClass.Grade = newGrade;
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(schoolClass, ct);
    }

    /// <summary>
    /// Xóa mềm lớp. Bị chặn khi có bài kiểm tra của các bài học trong lớp đang mở (đã phát hành, chưa tới giờ đóng).
    /// Cùng giao dịch vô hiệu mọi học sinh đang hoạt động của lớp (không đăng nhập được nữa); kết quả cũ giữ nguyên.
    /// </summary>
    public async Task DeleteAsync(Actor actor, long classId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockClassAsync(classId, ct);
        var schoolClass = await db.FindClassAsync(actor, classId, "xóa lớp", tracking: true, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var hasOpenQuiz = await db.Quizzes.AnyAsync(q => q.Lesson!.ClassId == classId && q.DeletedAt == null
            && q.Status == QuizStatus.PUBLISHED && (q.CloseAt == null || q.CloseAt > now), ct);
        if (hasOpenQuiz)
            throw AppException.BusinessRule(Messages.Blocked("xóa lớp", "Lớp còn bài kiểm tra đang mở. Hãy đóng bài kiểm tra trước."));

        var students = await db.Students.Where(s => s.ClassId == classId && s.DeletedAt == null && s.IsActive).ToListAsync(ct);
        foreach (var student in students) student.IsActive = false;
        schoolClass.DeletedAt = now;

        if (students.Count > 0)
            audit.Record(AuditAction.STUDENT_DEACTIVATED, AuditTargets.Classes, classId, actor,
                newValue: new { classId, className = schoolClass.Name, count = students.Count, reason = "classDeleted" });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // Tên lớp không trùng trong cùng giáo viên và năm học (lớp chưa xóa).
    private async Task EnsureNameFreeAsync(Actor actor, string name, string year, long? exceptClassId, CancellationToken ct)
    {
        var lowered = name.ToLower();
        var taken = await db.ClassesOf(actor).AnyAsync(c => c.SchoolYear == year && c.Name != null
            && c.Name.ToLower() == lowered && (exceptClassId == null || c.Id != exceptClassId), ct);
        if (taken)
            throw AppException.Conflict("name", Messages.Taken("Tên lớp", name));
    }

    private async Task<ClassDto> ToDtoAsync(SchoolClass c, CancellationToken ct)
    {
        var active = await db.Students.CountAsync(s => s.ClassId == c.Id && s.DeletedAt == null && s.IsActive, ct);
        var lessons = await db.Lessons.CountAsync(l => l.ClassId == c.Id && l.DeletedAt == null, ct);
        var never = await db.Students.CountAsync(s => s.ClassId == c.Id && s.DeletedAt == null && s.IsActive
                                                      && s.User!.LastLoginAt == null, ct);
        return ClassDto.From(c, active, lessons, never);
    }
}

/// <summary>Số liệu tổng hợp của trường (quản trị trường): không xem được lớp hay học sinh cụ thể.</summary>
public sealed class SchoolSummaryService(IAppDbContext db)
{
    public async Task<SchoolSummaryDto> GetAsync(Actor actor, CancellationToken ct)
    {
        var orgId = actor.RequireOrganizationId();
        var classes = await db.Classes.AsNoTracking()
            .Where(c => c.OrganizationId == orgId && c.DeletedAt == null)
            .Select(c => new
            {
                c.Grade,
                Active = db.Students.Count(s => s.ClassId == c.Id && s.DeletedAt == null && s.IsActive),
            })
            .ToListAsync(ct);

        var levels = Enumerable.Range(1, 3).Select(level =>
        {
            var inLevel = classes.Where(c => SchoolClass.SchoolLevelOf(c.Grade) == level).ToList();
            return new SchoolLevelSummaryDto(level, SchoolLevels.NameOf(level), inLevel.Count, inLevel.Sum(c => c.Active));
        }).ToList();
        return new SchoolSummaryDto(classes.Count, classes.Sum(c => c.Active), levels);
    }
}
