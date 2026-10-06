using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Heritages;

/// <summary>Đổi thực thể sang DTO và xếp thứ tự hiển thị của timeline; dùng chung cho các dịch vụ của kho di sản.</summary>
internal static class HeritageMapper
{
    public static ProvinceDto ToDto(Province p) => new(p.Id, p.Code, p.Name, p.Region);

    public static ReferenceDto ToDto(HeritageReference r) =>
        new(r.Id, r.HeritageId, r.Title, r.Url, r.SourceType, HeritageRules.SourceTypeLabel(r.SourceType));

    /// <summary>
    /// Ảnh đại diện là ảnh (IMAGE) của chính di sản có đường dẫn trùng <c>heritages.thumbnail_url</c>;
    /// ảnh của sự kiện không bao giờ là ảnh đại diện.
    /// </summary>
    public static bool IsThumbnail(Media m, string? thumbnailUrl) =>
        m.HeritageId is not null && m.MediaType == HeritageRules.MediaImage
        && !string.IsNullOrEmpty(thumbnailUrl) && m.Url == thumbnailUrl;

    public static MediaDto ToDto(Media m, string? thumbnailUrl)
    {
        var parts = HeritageRules.ParseCredit(m.Credit);
        return new MediaDto(m.Id, m.HeritageId, m.EventId, m.MediaType, m.Url, m.Caption, m.Credit,
            parts?.Author, parts?.License, parts?.Source, m.SortOrder, IsThumbnail(m, thumbnailUrl),
            StoredFileRules.ThumbnailUrlOf(m.Url));
    }

    public static IEnumerable<Media> OrderMedia(IEnumerable<Media> media) =>
        media.OrderBy(m => m.SortOrder ?? int.MaxValue).ThenBy(m => m.Id);

    public static IEnumerable<Timeline> OrderTimelines(IEnumerable<Timeline> timelines) =>
        timelines.OrderBy(t => t.SortOrder ?? int.MaxValue).ThenBy(t => t.StartYear ?? int.MaxValue).ThenBy(t => t.Id);

    /// <summary>Sự kiện xếp theo năm, tháng, ngày rồi thứ tự (sự kiện thiếu tháng hoặc ngày đứng trước trong cùng kỳ).</summary>
    public static IEnumerable<TimelineEvent> OrderEvents(IEnumerable<TimelineEvent> events) =>
        events.OrderBy(e => e.StartYear).ThenBy(e => e.StartMonth ?? 0).ThenBy(e => e.StartDay ?? 0)
            .ThenBy(e => e.SortOrder ?? int.MaxValue).ThenBy(e => e.Id);

    public static EventDto ToDto(TimelineEvent e, IEnumerable<Media> eventMedia) =>
        new(e.Id, e.TimelineId, e.Name, e.Description, e.StartYear, e.StartMonth, e.StartDay,
            e.EndYear, e.EndMonth, e.EndDay, e.SortOrder,
            OrderMedia(eventMedia).Select(m => ToDto(m, null)).ToList());

    public static TimelineDto ToDto(Timeline t, IEnumerable<TimelineEvent> events, ILookup<long, Media> mediaByEvent) =>
        new(t.Id, t.HeritageId, t.Name, t.Description, t.StartYear, t.EndYear, t.SortOrder,
            OrderEvents(events).Select(e => ToDto(e, mediaByEvent[e.Id])).ToList());

    /// <summary>
    /// Điều kiện xuất bản còn thiếu và lời nhắc theo quy trình. Lời nhắc: chưa có văn bản nhà nước; chưa có bài của
    /// Cục Di sản văn hóa hoặc UNESCO (nhận biết qua tiêu đề hoặc tên miền dsvh.gov.vn, unesco.org).
    /// </summary>
    public static PublishReadinessDto BuildReadiness(Heritage heritage, IReadOnlyCollection<Media> heritageMedia,
        IReadOnlyCollection<HeritageReference> references, int currentYear)
    {
        var thumbnailInLibrary = heritageMedia.Any(m => IsThumbnail(m, heritage.ThumbnailUrl));
        var missing = HeritagePublishing.FindMissing(heritage, thumbnailInLibrary, references.Count, currentYear)
            .Select(m => new MissingRequirementDto(m.Field, m.Label)).ToList();

        var warnings = new List<string>();
        if (!references.Any(r => r.SourceType == HeritageRules.SourceGovDoc))
            warnings.Add("Chưa có nguồn là văn bản nhà nước (loại nguồn Văn bản nhà nước).");
        if (!references.Any(IsCulturalHeritageAgencyOrUnesco))
            warnings.Add("Chưa có nguồn là bài của Cục Di sản văn hóa hoặc UNESCO.");
        return new PublishReadinessDto(missing.Count == 0, missing, warnings);
    }

    private static bool IsCulturalHeritageAgencyOrUnesco(HeritageReference r) =>
        (r.Title?.Contains("Cục Di sản văn hóa", StringComparison.OrdinalIgnoreCase) ?? false)
        || (r.Title?.Contains("UNESCO", StringComparison.OrdinalIgnoreCase) ?? false)
        || (r.Url?.Contains("dsvh.gov.vn", StringComparison.OrdinalIgnoreCase) ?? false)
        || (r.Url?.Contains("unesco.org", StringComparison.OrdinalIgnoreCase) ?? false);
}
