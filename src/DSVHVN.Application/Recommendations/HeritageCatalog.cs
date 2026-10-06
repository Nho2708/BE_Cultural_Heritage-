using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Recommendations;

/// <summary>
/// Thuộc tính nhẹ của mọi di sản chưa xóa (đủ để so độ tương đồng và dựng thẻ gợi ý), nạp một lần cho mỗi yêu cầu.
/// Di sản chưa xuất bản chỉ làm di sản tham chiếu, không bao giờ được gợi ý.
/// </summary>
internal sealed class HeritageCatalog
{
    private const int MaxHeritages = 5000;

    private readonly Dictionary<long, Entry> _byId;

    private HeritageCatalog(Dictionary<long, Entry> byId) => _byId = byId;

    private sealed record Entry(HeritageFeatures Features, bool Published, string? Slug, string? ThumbnailUrl, string? ShortDescription, string? ProvinceName);

    /// <summary>Di sản đã xuất bản: ứng viên để gợi ý.</summary>
    public IEnumerable<HeritageFeatures> Published => _byId.Values.Where(e => e.Published).Select(e => e.Features);

    public bool IsPublished(long id) => _byId.TryGetValue(id, out var e) && e.Published;

    public HeritageFeatures? Features(long id) => _byId.TryGetValue(id, out var e) ? e.Features : null;

    public static async Task<HeritageCatalog> LoadAsync(IAppDbContext db, CancellationToken ct)
    {
        var rows = await db.Heritages.AsNoTracking()
            .Where(h => h.DeletedAt == null)
            .OrderBy(h => h.Id)
            .Take(MaxHeritages)
            .Select(h => new
            {
                h.Id, h.Name, h.Slug, h.ShortDescription, h.ThumbnailUrl, h.HeritageType, h.RecognitionLevel, h.ProvinceId, h.Status,
                ProvinceName = h.Province == null ? null : h.Province.Name,
                Region = h.Province == null ? null : h.Province.Region,
            })
            .ToListAsync(ct);
        var eras = await db.Timelines.AsNoTracking()
            .Where(t => t.Heritage!.DeletedAt == null)
            .GroupBy(t => t.HeritageId)
            .Select(g => new { HeritageId = g.Key, From = g.Min(t => t.StartYear), To = g.Max(t => t.EndYear ?? t.StartYear) })
            .ToDictionaryAsync(x => x.HeritageId, ct);

        var byId = rows.ToDictionary(
            h => h.Id,
            h =>
            {
                var era = eras.GetValueOrDefault(h.Id);
                var text = $"{h.Name} {h.ShortDescription}";
                var features = new HeritageFeatures(h.Id, h.Name, h.HeritageType, h.ProvinceId, h.Region, h.RecognitionLevel, era?.From, era?.To, text);
                return new Entry(features, h.Status == ContentStatus.PUBLISHED, h.Slug, h.ThumbnailUrl, h.ShortDescription, h.ProvinceName);
            });
        return new HeritageCatalog(byId);
    }

    /// <summary>Thẻ di sản gợi ý; null nếu di sản không có hoặc chưa xuất bản.</summary>
    public RecommendedHeritageDto? Card(long id, string reason)
    {
        if (!_byId.TryGetValue(id, out var e) || !e.Published) return null;
        return new RecommendedHeritageDto(id, e.Slug, e.Features.Name, e.ThumbnailUrl, e.ShortDescription, e.ProvinceName,
            e.Features.Type is { } type ? HeritageLabels.Type(type) : null, reason);
    }

    public ClassReviewHeritageDto? ReviewCard(long id, int answered, int wrong)
    {
        if (!_byId.TryGetValue(id, out var e) || !e.Published) return null;
        return new ClassReviewHeritageDto(id, e.Slug, e.Features.Name, e.ThumbnailUrl, e.ShortDescription, e.ProvinceName,
            e.Features.Type is { } type ? HeritageLabels.Type(type) : null, answered, wrong,
            AttemptRules.RatePercent(wrong, answered), RecommendationRules.ClassReviewReason(wrong, answered));
    }
}
