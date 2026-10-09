using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Heritages;

/// <summary>
/// Dịch vụ tra cứu và khám phá di sản văn hóa: phục vụ Bản đồ số, Dòng thời gian và trang chi tiết di sản.
/// </summary>
public sealed class HeritageService(IAppDbContext db)
{
    public async Task<PagedResult<HeritageSummaryDto>> ListAsync(HeritageListQuery query, CancellationToken ct)
    {
        var heritages = db.Heritages
            .AsNoTracking()
            .Include(h => h.Province)
            .Where(h => h.DeletedAt == null && h.Status == ContentStatus.PUBLISHED);

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            heritages = heritages.Where(h =>
                (h.Name != null && h.Name.Contains(keyword)) ||
                (h.Address != null && h.Address.Contains(keyword)) ||
                (h.ShortDescription != null && h.ShortDescription.Contains(keyword)));
        }

        if (query.ProvinceId.HasValue)
        {
            heritages = heritages.Where(h => h.ProvinceId == query.ProvinceId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Region))
        {
            var region = query.Region.Trim();
            heritages = heritages.Where(h => h.Province != null && h.Province.Region == region);
        }

        if (query.HeritageType.HasValue)
        {
            heritages = heritages.Where(h => h.HeritageType == query.HeritageType.Value);
        }

        if (query.RecognitionLevel.HasValue)
        {
            heritages = heritages.Where(h => h.RecognitionLevel == query.RecognitionLevel.Value);
        }

        return await heritages
            .OrderByDescending(h => h.RecognizedYear)
            .ThenBy(h => h.Name)
            .Select(h => new HeritageSummaryDto(
                h.Id,
                h.Name ?? string.Empty,
                h.Slug ?? string.Empty,
                h.ShortDescription,
                h.HeritageType,
                h.RecognitionLevel,
                h.RecognizedYear,
                h.Address,
                h.Latitude,
                h.Longitude,
                h.ThumbnailUrl,
                h.ProvinceId,
                h.Province != null ? h.Province.Name : null,
                h.Province != null ? h.Province.Region : null))
            .ToPagedAsync(query.Page, query.PageSize, ct);
    }

    public async Task<HeritageDetailDto> GetBySlugAsync(string slug, CancellationToken ct)
    {
        var cleanSlug = slug.Trim();
        var heritage = await db.Heritages
            .AsNoTracking()
            .Include(h => h.Province)
            .Include(h => h.Timelines.OrderBy(t => t.SortOrder))
                .ThenInclude(t => t.Events.OrderBy(e => e.SortOrder))
                    .ThenInclude(e => e.Media.OrderBy(m => m.SortOrder))
            .Include(h => h.Media.OrderBy(m => m.SortOrder))
            .Include(h => h.References)
            .FirstOrDefaultAsync(h => h.Slug == cleanSlug && h.DeletedAt == null && h.Status == ContentStatus.PUBLISHED, ct);

        if (heritage is null)
        {
            throw AppException.NotFound(Messages.NotFound("xem", "di sản"));
        }

        return MapToDetail(heritage);
    }

    public async Task<HeritageDetailDto> GetByIdAsync(long id, CancellationToken ct)
    {
        var heritage = await db.Heritages
            .AsNoTracking()
            .Include(h => h.Province)
            .Include(h => h.Timelines.OrderBy(t => t.SortOrder))
                .ThenInclude(t => t.Events.OrderBy(e => e.SortOrder))
                    .ThenInclude(e => e.Media.OrderBy(m => m.SortOrder))
            .Include(h => h.Media.OrderBy(m => m.SortOrder))
            .Include(h => h.References)
            .FirstOrDefaultAsync(h => h.Id == id && h.DeletedAt == null && h.Status == ContentStatus.PUBLISHED, ct);

        if (heritage is null)
        {
            throw AppException.NotFound(Messages.NotFound("xem", "di sản"));
        }

        return MapToDetail(heritage);
    }

    public async Task<List<ProvinceSummaryDto>> ListProvincesAsync(CancellationToken ct)
    {
        return await db.Provinces
            .AsNoTracking()
            .OrderBy(p => p.Id)
            .Select(p => new ProvinceSummaryDto(
                p.Id,
                p.Code,
                p.Name,
                p.Region,
                db.Heritages.Count(h => h.ProvinceId == p.Id && h.DeletedAt == null && h.Status == ContentStatus.PUBLISHED)))
            .ToListAsync(ct);
    }

    private static HeritageDetailDto MapToDetail(Domain.Heritages.Heritage h)
    {
        return new HeritageDetailDto(
            h.Id,
            h.Name ?? string.Empty,
            h.Slug ?? string.Empty,
            h.ShortDescription,
            h.Content,
            h.HeritageType,
            h.RecognitionLevel,
            h.RecognizedYear,
            h.Address,
            h.Latitude,
            h.Longitude,
            h.PlaceId,
            h.ThumbnailUrl,
            h.ProvinceId,
            h.Province?.Name,
            h.Province?.Region,
            h.Timelines.Select(t => new TimelineDto(
                t.Id,
                t.Name,
                t.Description,
                t.StartYear,
                t.EndYear,
                t.SortOrder,
                t.Events.Select(e => new TimelineEventDto(
                    e.Id,
                    e.Name,
                    e.Description,
                    e.StartYear,
                    e.StartMonth,
                    e.StartDay,
                    e.EndYear,
                    e.EndMonth,
                    e.EndDay,
                    e.SortOrder,
                    e.Media.Select(m => new MediaDto(
                        m.Id,
                        m.MediaType,
                        m.Url,
                        m.Caption,
                        m.Credit,
                        m.SortOrder)).ToList())).ToList())).ToList(),
            h.Media.Where(m => m.EventId == null).Select(m => new MediaDto(
                m.Id,
                m.MediaType,
                m.Url,
                m.Caption,
                m.Credit,
                m.SortOrder)).ToList(),
            h.References.Select(r => new HeritageReferenceDto(
                r.Id,
                r.Title,
                r.Url,
                r.SourceType)).ToList());
    }
}
