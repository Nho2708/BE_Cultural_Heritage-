using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Translations;

/// <summary>
/// Các phần chữ của một di sản ở ngôn ngữ khách chọn. <see cref="Row"/> là bản dịch hiện hành (null khi chọn tiếng Việt hoặc chưa có bản hiện hành: nội dung lùi về tiếng Việt).
/// Giai đoạn, sự kiện, chú thích ảnh đi theo trạng thái của <see cref="Row"/> (cùng di sản, cùng ngôn ngữ); phần nào chưa có dòng dịch thì lùi về tiếng Việt cho riêng phần đó.
/// </summary>
public sealed class LocalizedParts
{
    public static readonly LocalizedParts None = new(null, new Dictionary<long, TimelineTranslation>(),
        new Dictionary<long, EventTranslation>(), new Dictionary<long, MediaTranslation>());

    public LocalizedParts(HeritageTranslation? row, IReadOnlyDictionary<long, TimelineTranslation> timelines,
        IReadOnlyDictionary<long, EventTranslation> events, IReadOnlyDictionary<long, MediaTranslation> media)
    {
        Row = row;
        Timelines = timelines;
        Events = events;
        Media = media;
    }

    public HeritageTranslation? Row { get; }
    public IReadOnlyDictionary<long, TimelineTranslation> Timelines { get; }
    public IReadOnlyDictionary<long, EventTranslation> Events { get; }
    public IReadOnlyDictionary<long, MediaTranslation> Media { get; }

    public string Name(string? original) => Row is null ? original ?? string.Empty : Row.Name;
    public string? ShortDescription(string? original) => Row?.ShortDescription ?? original;
    public string? Content(string? original) => Row?.Content ?? original;
    public string? TimelineName(long id, string? original) => Timelines.TryGetValue(id, out var t) ? t.Name : original;
    public string? TimelineDescription(long id, string? original) => Timelines.TryGetValue(id, out var t) ? t.Description ?? original : original;
    public string? EventName(long id, string? original) => Events.TryGetValue(id, out var e) ? e.Name : original;
    public string? EventDescription(long id, string? original) => Events.TryGetValue(id, out var e) ? e.Description ?? original : original;
    public string? Caption(long id, string? original) => Media.TryGetValue(id, out var m) ? m.Caption : original;
}

/// <summary>
/// Chọn nội dung công khai theo ngôn ngữ (quy tắc Bản dịch nội dung di sản, Khi thiếu bản dịch): tiếng Việt là bản gốc; tiếng Anh chỉ hiện bản đã duyệt;
/// tiếng Hàn, Trung, Nhật hiện ngay bản dịch máy kèm nhãn; bản chờ duyệt và bị từ chối không hiện. Bản "cần dịch lại" vẫn hiện bản cũ.
/// Chưa có bản hiện hành thì lùi về tiếng Việt và báo bằng <see cref="LocalizationDto.IsFallback"/> (một thông báo cho cả trang).
/// Chỉ đọc, không gọi AI.
/// </summary>
public sealed class HeritageLocalizer(IAppDbContext db)
{
    /// <summary>Dòng dịch chính hiện hành của nhiều di sản ở một ngôn ngữ (cho thẻ, kết quả tìm, ghim bản đồ); tiếng Việt thì rỗng.</summary>
    public async Task<IReadOnlyDictionary<long, HeritageTranslation>> LoadVisibleAsync(
        IReadOnlyCollection<long> heritageIds, LanguageCode language, CancellationToken ct)
    {
        if (language == LanguageCode.vi || heritageIds.Count == 0) return new Dictionary<long, HeritageTranslation>();
        var rows = await db.HeritageTranslations.AsNoTracking()
            .Where(t => heritageIds.Contains(t.HeritageId) && t.LanguageCode == language
                        && (t.Status == TranslationStatus.APPROVED || t.Status == TranslationStatus.MACHINE))
            .ToListAsync(ct);
        return rows.ToDictionary(t => t.HeritageId);
    }

    /// <summary>Mọi phần chữ hiện hành của một di sản ở một ngôn ngữ (cho trang chi tiết và dòng thời gian).</summary>
    public async Task<LocalizedParts> LoadPartsAsync(long heritageId, LanguageCode language, CancellationToken ct)
    {
        if (language == LanguageCode.vi) return LocalizedParts.None;
        var row = (await LoadVisibleAsync([heritageId], language, ct)).GetValueOrDefault(heritageId);
        if (row is null) return LocalizedParts.None;

        var timelines = await db.TimelineTranslations.AsNoTracking()
            .Where(t => t.LanguageCode == language && t.Timeline!.HeritageId == heritageId).ToListAsync(ct);
        var events = await db.EventTranslations.AsNoTracking()
            .Where(t => t.LanguageCode == language && t.Event!.Timeline!.HeritageId == heritageId).ToListAsync(ct);
        var media = await db.MediaTranslations.AsNoTracking()
            .Where(t => t.LanguageCode == language
                        && (t.Media!.HeritageId == heritageId || (t.Media.EventId != null && t.Media.Event!.Timeline!.HeritageId == heritageId)))
            .ToListAsync(ct);
        return new LocalizedParts(row, timelines.ToDictionary(t => t.TimelineId), events.ToDictionary(e => e.EventId), media.ToDictionary(m => m.MediaId));
    }

    /// <summary>Mô tả ngôn ngữ của một phản hồi: có bản hiện hành thì hiện bản đó (máy dịch thì gắn nhãn), không thì tiếng Việt kèm cờ lùi.</summary>
    public static LocalizationDto Describe(LanguageCode requested, HeritageTranslation? row)
    {
        if (requested == LanguageCode.vi)
            return new LocalizationDto(Languages.Tag(requested), Languages.Tag(LanguageCode.vi), false, false);
        return row is null
            ? new LocalizationDto(Languages.Tag(requested), Languages.Tag(LanguageCode.vi), true, false)
            : new LocalizationDto(Languages.Tag(requested), Languages.Tag(requested), false, row.Status == TranslationStatus.MACHINE);
    }

    /// <summary>Tóm tắt hiện hành của một di sản (dùng cho trang chi tiết biết có nút "Tóm tắt" hay không): có tóm tắt tiếng Việt đã duyệt.</summary>
    public Task<bool> HasSummaryAsync(long heritageId, CancellationToken ct) =>
        db.HeritageSummaries.AsNoTracking().AnyAsync(
            s => s.HeritageId == heritageId && s.LanguageCode == LanguageCode.vi && s.Status == TranslationStatus.APPROVED, ct);
}
