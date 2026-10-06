using DSVHVN.Application.Common;
using DSVHVN.Application.Heritages;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Translations;

/// <summary>
/// Nội dung tiếng Việt của một di sản dùng để dịch và tóm tắt: chính di sản, các giai đoạn, sự kiện (theo thứ tự hiển thị) và ảnh của di sản lẫn của sự kiện.
/// Chỉ đọc, không theo dõi thay đổi.
/// </summary>
public sealed record HeritageSource(
    Heritage Heritage,
    IReadOnlyList<Timeline> Timelines,
    IReadOnlyList<TimelineEvent> Events,
    IReadOnlyList<Media> Media)
{
    /// <summary>Có chữ tiếng Việt để dịch: tên và ít nhất mô tả ngắn hoặc nội dung.</summary>
    public bool HasContent =>
        !string.IsNullOrWhiteSpace(Heritage.Name)
        && (!string.IsNullOrWhiteSpace(Heritage.Content) || !string.IsNullOrWhiteSpace(Heritage.ShortDescription));

    /// <summary>Phần chữ gửi cho Dịch vụ AI: chỉ giai đoạn, sự kiện có tên và ảnh có chú thích; địa chỉ và ghi công không gửi.</summary>
    public AiHeritageText ToAiText() => new(
        Heritage.Name!.Trim(),
        Heritage.ShortDescription,
        Heritage.Content,
        Timelines.Where(t => !string.IsNullOrWhiteSpace(t.Name)).Select(t => new AiTimelineText(t.Id, t.Name!, t.Description)).ToList(),
        Events.Where(e => !string.IsNullOrWhiteSpace(e.Name)).Select(e => new AiEventText(e.Id, e.TimelineId, e.Name!, e.Description)).ToList(),
        Media.Where(m => !string.IsNullOrWhiteSpace(m.Caption)).Select(m => new AiMediaText(m.Id, m.Caption!)).ToList());

    /// <summary>Cùng phần chữ ở dạng trả ra cho màn hình duyệt (bản gốc, kèm địa chỉ và chú thích của mọi ảnh).</summary>
    public TranslationTextDto ToTextDto() => new(
        Heritage.Name, Heritage.ShortDescription, Heritage.Content, Heritage.Address,
        Timelines.Select(t => new TimelineTextDto(t.Id, t.Name, t.Description)).ToList(),
        Events.Select(e => new EventTextDto(e.Id, e.TimelineId, e.Name, e.Description)).ToList(),
        Media.Select(m => new MediaTextDto(m.Id, m.EventId, m.Caption)).ToList());
}

/// <summary>Các dòng bản dịch của một di sản ở một ngôn ngữ: dòng chính (kèm trạng thái) và các dòng giai đoạn, sự kiện, chú thích ảnh.</summary>
public sealed record TranslationRows(
    HeritageTranslation Heritage,
    IReadOnlyList<TimelineTranslation> Timelines,
    IReadOnlyList<EventTranslation> Events,
    IReadOnlyList<MediaTranslation> Media)
{
    /// <summary>Bản dịch ở dạng trả ra cho màn hình duyệt; phần chưa có bản dịch để trống (null).</summary>
    public TranslationTextDto ToTextDto(HeritageSource source) => new(
        Heritage.Name, Heritage.ShortDescription, Heritage.Content, Heritage.Address,
        source.Timelines.Select(t => new TimelineTextDto(t.Id, Timelines.FirstOrDefault(x => x.TimelineId == t.Id)?.Name,
            Timelines.FirstOrDefault(x => x.TimelineId == t.Id)?.Description)).ToList(),
        source.Events.Select(e => new EventTextDto(e.Id, e.TimelineId, Events.FirstOrDefault(x => x.EventId == e.Id)?.Name,
            Events.FirstOrDefault(x => x.EventId == e.Id)?.Description)).ToList(),
        source.Media.Select(m => new MediaTextDto(m.Id, m.EventId, Media.FirstOrDefault(x => x.MediaId == m.Id)?.Caption)).ToList());

    /// <summary>
    /// Phần chữ của bản dịch để làm nguồn dịch tiếp (tiếng Hàn, Trung, Nhật dịch từ tiếng Anh đã duyệt): phần nào chưa có bản dịch thì lấy tiếng Việt
    /// của phần đó để không mất chữ.
    /// </summary>
    public AiHeritageText ToAiText(HeritageSource source) => new(
        Heritage.Name,
        Heritage.ShortDescription ?? source.Heritage.ShortDescription,
        Heritage.Content ?? source.Heritage.Content,
        source.Timelines.Where(t => !string.IsNullOrWhiteSpace(t.Name)).Select(t =>
        {
            var row = Timelines.FirstOrDefault(x => x.TimelineId == t.Id);
            return new AiTimelineText(t.Id, row?.Name ?? t.Name!, row is null ? t.Description : row.Description);
        }).ToList(),
        source.Events.Where(e => !string.IsNullOrWhiteSpace(e.Name)).Select(e =>
        {
            var row = Events.FirstOrDefault(x => x.EventId == e.Id);
            return new AiEventText(e.Id, e.TimelineId, row?.Name ?? e.Name!, row is null ? e.Description : row.Description);
        }).ToList(),
        source.Media.Where(m => !string.IsNullOrWhiteSpace(m.Caption)).Select(m =>
            new AiMediaText(m.Id, Media.FirstOrDefault(x => x.MediaId == m.Id)?.Caption ?? m.Caption!)).ToList());
}

/// <summary>Nạp nội dung tiếng Việt và các dòng bản dịch của một di sản.</summary>
public sealed class HeritageSourceLoader(IAppDbContext db)
{
    /// <summary>Nội dung tiếng Việt của di sản chưa xóa mềm (mọi trạng thái); không có thì 404.</summary>
    public async Task<HeritageSource> LoadAsync(long heritageId, string action, CancellationToken ct)
    {
        var heritage = await db.Heritages.AsNoTracking().SingleOrDefaultAsync(h => h.Id == heritageId && h.DeletedAt == null, ct)
                       ?? throw AppException.NotFound(Messages.NotFound(action, "di sản"));
        var timelines = HeritageMapper.OrderTimelines(
            await db.Timelines.AsNoTracking().Where(t => t.HeritageId == heritageId).ToListAsync(ct)).ToList();
        var timelineOrder = timelines.Select((t, i) => (t.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var events = (await db.Events.AsNoTracking().Where(e => e.Timeline!.HeritageId == heritageId).ToListAsync(ct))
            .GroupBy(e => e.TimelineId).OrderBy(g => timelineOrder.GetValueOrDefault(g.Key, int.MaxValue))
            .SelectMany(g => HeritageMapper.OrderEvents(g)).ToList();
        var media = await db.Media.AsNoTracking()
            .Where(m => m.HeritageId == heritageId || (m.EventId != null && m.Event!.Timeline!.HeritageId == heritageId))
            .OrderBy(m => m.EventId == null ? 0 : 1).ThenBy(m => m.EventId).ThenBy(m => m.SortOrder ?? int.MaxValue).ThenBy(m => m.Id)
            .ToListAsync(ct);
        return new HeritageSource(heritage, timelines, events, media);
    }

    /// <summary>Các dòng bản dịch của di sản ở một ngôn ngữ; null nếu chưa có dòng chính.</summary>
    public async Task<TranslationRows?> LoadRowsAsync(long heritageId, LanguageCode language, CancellationToken ct)
    {
        var heritage = await db.HeritageTranslations.AsNoTracking()
            .SingleOrDefaultAsync(t => t.HeritageId == heritageId && t.LanguageCode == language, ct);
        if (heritage is null) return null;
        var timelines = await db.TimelineTranslations.AsNoTracking()
            .Where(t => t.LanguageCode == language && t.Timeline!.HeritageId == heritageId).ToListAsync(ct);
        var events = await db.EventTranslations.AsNoTracking()
            .Where(t => t.LanguageCode == language && t.Event!.Timeline!.HeritageId == heritageId).ToListAsync(ct);
        var media = await db.MediaTranslations.AsNoTracking()
            .Where(t => t.LanguageCode == language
                        && (t.Media!.HeritageId == heritageId || (t.Media.EventId != null && t.Media.Event!.Timeline!.HeritageId == heritageId)))
            .ToListAsync(ct);
        return new TranslationRows(heritage, timelines, events, media);
    }
}
