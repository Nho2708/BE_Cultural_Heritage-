using DSVHVN.Application.Common;
using DSVHVN.Domain.Heritages;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Heritages;

/// <summary>
/// Tìm di sản và các phần con (giai đoạn, sự kiện, ảnh, nguồn) chưa bị xóa mềm. Đối tượng không có, hoặc thuộc di sản đã xóa,
/// đều trả 404. Mọi thao tác trên phần con lấy di sản cha theo dõi được để cập nhật mốc sửa gần nhất của di sản.
/// </summary>
internal static class HeritageLookup
{
    public static async Task<Heritage> FindHeritageAsync(this IAppDbContext db, long id, string action, CancellationToken ct) =>
        await db.Heritages.SingleOrDefaultAsync(h => h.Id == id && h.DeletedAt == null, ct)
        ?? throw AppException.NotFound(Messages.NotFound(action, "di sản"));

    public static async Task<Timeline> FindTimelineAsync(this IAppDbContext db, long id, string action, CancellationToken ct)
    {
        var timeline = await db.Timelines.Include(t => t.Heritage).SingleOrDefaultAsync(t => t.Id == id, ct);
        return timeline is { Heritage.DeletedAt: null }
            ? timeline
            : throw AppException.NotFound(Messages.NotFound(action, "giai đoạn"));
    }

    public static async Task<TimelineEvent> FindEventAsync(this IAppDbContext db, long id, string action, CancellationToken ct)
    {
        var found = await db.Events.Include(e => e.Timeline!).ThenInclude(t => t.Heritage).SingleOrDefaultAsync(e => e.Id == id, ct);
        return found is { Timeline.Heritage.DeletedAt: null }
            ? found
            : throw AppException.NotFound(Messages.NotFound(action, "sự kiện"));
    }

    public static async Task<Media> FindMediaAsync(this IAppDbContext db, long id, string action, CancellationToken ct)
    {
        var media = await db.Media.Include(m => m.Heritage)
            .Include(m => m.Event!).ThenInclude(e => e.Timeline!).ThenInclude(t => t.Heritage)
            .SingleOrDefaultAsync(m => m.Id == id, ct);
        return media is not null && HeritageOf(media) is { DeletedAt: null }
            ? media
            : throw AppException.NotFound(Messages.NotFound(action, "ảnh"));
    }

    public static async Task<HeritageReference> FindReferenceAsync(this IAppDbContext db, long id, string action, CancellationToken ct)
    {
        var reference = await db.HeritageReferences.Include(r => r.Heritage).SingleOrDefaultAsync(r => r.Id == id, ct);
        return reference is { Heritage.DeletedAt: null }
            ? reference
            : throw AppException.NotFound(Messages.NotFound(action, "nguồn tham chiếu"));
    }

    /// <summary>Di sản chứa ảnh: trực tiếp hoặc qua sự kiện và giai đoạn.</summary>
    public static Heritage HeritageOf(Media media) => media.Heritage ?? media.Event!.Timeline!.Heritage!;

    /// <summary>Đúng bộ id hiện có, không thiếu, không thừa, không lặp thì trả id theo thứ tự mới; không thì báo lỗi thứ tự.</summary>
    public static IReadOnlyList<long> ValidateOrder(OrderRequest request, IReadOnlyCollection<long> currentIds, string what)
    {
        var ids = request.Ids ?? [];
        if (ids.Count != currentIds.Count || ids.Distinct().Count() != ids.Count || !ids.All(currentIds.Contains))
            throw AppException.Validation("ids", Messages.Invalid("Thứ tự", $"phải gồm đủ và đúng các {what} hiện có, không lặp"));
        return ids;
    }
}
