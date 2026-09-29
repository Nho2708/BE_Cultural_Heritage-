using DSVHVN.Application.Common;
using DSVHVN.Domain.Heritages;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Heritages;

/// <summary>
/// Timeline theo từng di sản (không có trục thời gian chung): giai đoạn của di sản và sự kiện của giai đoạn.
/// Thêm, sửa, xóa, sắp xếp. Xóa giai đoạn xóa luôn sự kiện và ảnh của sự kiện; xóa sự kiện xóa luôn ảnh của sự kiện.
/// </summary>
public sealed class TimelineService(IAppDbContext db, RequestValidator validator, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ---- Giai đoạn ----

    public async Task<TimelineDto> CreateTimelineAsync(long heritageId, TimelineRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var heritage = await db.FindHeritageAsync(heritageId, "thêm giai đoạn", ct);
        var timeline = new Timeline
        {
            HeritageId = heritageId,
            Name = request.Name!.Trim(),
            Description = ValidationRules.TrimToNull(request.Description),
            StartYear = request.StartYear,
            EndYear = request.EndYear,
            SortOrder = request.SortOrder ?? await NextTimelineOrderAsync(heritageId, ct),
        };
        db.Timelines.Add(timeline);
        heritage.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return HeritageMapper.ToDto(timeline, [], Array.Empty<Media>().ToLookup(m => m.Id));
    }

    public async Task<TimelineDto> UpdateTimelineAsync(long timelineId, TimelineRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var timeline = await db.FindTimelineAsync(timelineId, "sửa giai đoạn", ct);
        timeline.Name = request.Name!.Trim();
        timeline.Description = ValidationRules.TrimToNull(request.Description);
        timeline.StartYear = request.StartYear;
        timeline.EndYear = request.EndYear;
        if (request.SortOrder is not null) timeline.SortOrder = request.SortOrder;
        timeline.Heritage!.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return await LoadTimelineAsync(timelineId, ct);
    }

    public async Task DeleteTimelineAsync(long timelineId, CancellationToken ct)
    {
        var timeline = await db.FindTimelineAsync(timelineId, "xóa giai đoạn", ct);
        var events = await db.Events.Where(e => e.TimelineId == timelineId).ToListAsync(ct);
        var eventIds = events.Select(e => e.Id).ToList();
        db.Media.RemoveRange(await db.Media.Where(m => m.EventId != null && eventIds.Contains(m.EventId.Value)).ToListAsync(ct));
        db.Events.RemoveRange(events);
        db.Timelines.Remove(timeline);
        timeline.Heritage!.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Đặt thứ tự các giai đoạn của di sản: <c>Ids</c> là toàn bộ giai đoạn theo thứ tự hiển thị mới; thứ tự lưu là 1, 2, 3...</summary>
    public async Task<IReadOnlyList<TimelineDto>> ReorderTimelinesAsync(long heritageId, OrderRequest request, CancellationToken ct)
    {
        var heritage = await db.FindHeritageAsync(heritageId, "sắp xếp giai đoạn", ct);
        var timelines = await db.Timelines.Where(t => t.HeritageId == heritageId).ToListAsync(ct);
        var ids = HeritageLookup.ValidateOrder(request, timelines.Select(t => t.Id).ToList(), "giai đoạn");
        for (var i = 0; i < ids.Count; i++) timelines.Single(t => t.Id == ids[i]).SortOrder = i + 1;
        heritage.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return await LoadTimelinesAsync(heritageId, ct);
    }

    // ---- Sự kiện ----

    public async Task<EventDto> CreateEventAsync(long timelineId, EventRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var timeline = await db.FindTimelineAsync(timelineId, "thêm sự kiện", ct);
        var item = new TimelineEvent { TimelineId = timelineId };
        ApplyEvent(item, request);
        item.SortOrder = request.SortOrder ?? await NextEventOrderAsync(timelineId, ct);
        db.Events.Add(item);
        timeline.Heritage!.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return HeritageMapper.ToDto(item, []);
    }

    public async Task<EventDto> UpdateEventAsync(long eventId, EventRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var item = await db.FindEventAsync(eventId, "sửa sự kiện", ct);
        ApplyEvent(item, request);
        if (request.SortOrder is not null) item.SortOrder = request.SortOrder;
        item.Timeline!.Heritage!.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        var media = await db.Media.AsNoTracking().Where(m => m.EventId == eventId).ToListAsync(ct);
        return HeritageMapper.ToDto(item, media);
    }

    public async Task DeleteEventAsync(long eventId, CancellationToken ct)
    {
        var item = await db.FindEventAsync(eventId, "xóa sự kiện", ct);
        db.Media.RemoveRange(await db.Media.Where(m => m.EventId == eventId).ToListAsync(ct));
        db.Events.Remove(item);
        item.Timeline!.Heritage!.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Đặt thứ tự các sự kiện của giai đoạn (có tác dụng khi nhiều sự kiện cùng năm, tháng, ngày).</summary>
    public async Task<IReadOnlyList<EventDto>> ReorderEventsAsync(long timelineId, OrderRequest request, CancellationToken ct)
    {
        var timeline = await db.FindTimelineAsync(timelineId, "sắp xếp sự kiện", ct);
        var events = await db.Events.Where(e => e.TimelineId == timelineId).ToListAsync(ct);
        var ids = HeritageLookup.ValidateOrder(request, events.Select(e => e.Id).ToList(), "sự kiện");
        for (var i = 0; i < ids.Count; i++) events.Single(e => e.Id == ids[i]).SortOrder = i + 1;
        timeline.Heritage!.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return (await LoadTimelineAsync(timelineId, ct)).Events;
    }

    // ---- Nội bộ ----

    private static void ApplyEvent(TimelineEvent item, EventRequest r)
    {
        item.Name = r.Name!.Trim();
        item.Description = ValidationRules.TrimToNull(r.Description);
        item.StartYear = r.StartYear!.Value;
        item.StartMonth = r.StartMonth;
        item.StartDay = r.StartDay;
        item.EndYear = r.EndYear;
        item.EndMonth = r.EndYear is null ? null : r.EndMonth;
        item.EndDay = r.EndYear is null ? null : r.EndDay;
    }

    private async Task<int> NextTimelineOrderAsync(long heritageId, CancellationToken ct) =>
        (await db.Timelines.Where(t => t.HeritageId == heritageId).MaxAsync(t => (int?)t.SortOrder, ct) ?? 0) + 1;

    private async Task<int> NextEventOrderAsync(long timelineId, CancellationToken ct) =>
        (await db.Events.Where(e => e.TimelineId == timelineId).MaxAsync(e => (int?)e.SortOrder, ct) ?? 0) + 1;

    private async Task<IReadOnlyList<TimelineDto>> LoadTimelinesAsync(long heritageId, CancellationToken ct)
    {
        var timelines = await db.Timelines.AsNoTracking().Where(t => t.HeritageId == heritageId).ToListAsync(ct);
        var timelineIds = timelines.Select(t => t.Id).ToList();
        var events = await db.Events.AsNoTracking().Where(e => timelineIds.Contains(e.TimelineId)).ToListAsync(ct);
        var eventIds = events.Select(e => e.Id).ToList();
        var media = await db.Media.AsNoTracking().Where(m => m.EventId != null && eventIds.Contains(m.EventId.Value)).ToListAsync(ct);
        var mediaByEvent = media.ToLookup(m => m.EventId!.Value);
        var eventsByTimeline = events.ToLookup(e => e.TimelineId);
        return HeritageMapper.OrderTimelines(timelines)
            .Select(t => HeritageMapper.ToDto(t, eventsByTimeline[t.Id], mediaByEvent)).ToList();
    }

    private async Task<TimelineDto> LoadTimelineAsync(long timelineId, CancellationToken ct)
    {
        var heritageId = await db.Timelines.Where(t => t.Id == timelineId).Select(t => t.HeritageId).SingleAsync(ct);
        return (await LoadTimelinesAsync(heritageId, ct)).Single(t => t.Id == timelineId);
    }
}
