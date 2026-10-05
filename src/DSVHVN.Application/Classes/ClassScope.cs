using DSVHVN.Application.Common;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Classes;

/// <summary>
/// Phạm vi lớp của giáo viên: lớp phải thuộc trường của người gọi, do chính người gọi phụ trách và chưa xóa mềm.
/// Lớp của giáo viên khác (kể cả cùng trường) hoặc của trường khác trả 404 như không tồn tại.
/// </summary>
public static class ClassScope
{
    public static IQueryable<SchoolClass> ClassesOf(this IAppDbContext db, Actor actor)
    {
        if (actor.Role != RoleCode.TEACHER) throw AppException.Forbidden(Messages.Forbidden);
        var orgId = actor.RequireOrganizationId();
        return db.Classes.Where(c => c.OrganizationId == orgId && c.TeacherId == actor.UserId && c.DeletedAt == null);
    }

    public static async Task<SchoolClass> FindClassAsync(this IAppDbContext db, Actor actor, long classId, string action,
        bool tracking, CancellationToken ct)
    {
        var query = db.ClassesOf(actor);
        if (!tracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(c => c.Id == classId, ct)
               ?? throw AppException.NotFound(Messages.NotFound(action, "lớp của bạn"));
    }
}
