using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Heritages;

/// <summary>
/// Kho di sản phía quản trị hệ thống: danh sách, tạo và sửa thông tin chung, ảnh đại diện, đổi trạng thái
/// (xuất bản, ẩn, hiện lại), xóa mềm. Trạng thái: DRAFT sang PUBLISHED (xuất bản), PUBLISHED sang ARCHIVED (ẩn),
/// ARCHIVED sang PUBLISHED (hiện lại). Xuất bản và hiện lại chỉ khi đủ điều kiện (<see cref="HeritagePublishing"/>).
/// Chỉ xuất bản và ẩn (kể cả hiện lại, ghi như xuất bản) ghi nhật ký thao tác vì danh mục hành động chỉ có hai mã này.
/// </summary>
public sealed class HeritageService(IAppDbContext db, RequestValidator validator, IAuditLogger audit, TimeProvider clock)
{
    private int CurrentYear => VietnamTime.Today(clock.GetUtcNow()).Year;

    public async Task<IReadOnlyList<ProvinceDto>> ListProvincesAsync(CancellationToken ct) =>
        (await db.Provinces.AsNoTracking().OrderBy(p => p.Code).ThenBy(p => p.Id).ToListAsync(ct))
        .Select(HeritageMapper.ToDto).ToList();

    public async Task<PagedResult<HeritageSummaryDto>> ListAsync(HeritageListQuery query, CancellationToken ct)
    {
        var heritages = db.Heritages.AsNoTracking().Where(h => h.DeletedAt == null);

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            // Tên tìm được cả có dấu lẫn không dấu nhờ collation Vietnamese_CI_AI; đường dẫn (không dấu) bổ trợ khi CSDL không có collation đó.
            var keyword = query.Keyword.Trim();
            var slugKey = HeritageRules.Slugify(keyword);
            heritages = slugKey.Length == 0
                ? heritages.Where(h => h.Name!.Contains(keyword))
                : heritages.Where(h => h.Name!.Contains(keyword) || h.Slug!.Contains(slugKey));
        }
        if (query.ProvinceId is { } provinceId) heritages = heritages.Where(h => h.ProvinceId == provinceId);
        if (query.HeritageType is { } type) heritages = heritages.Where(h => h.HeritageType == type);
        if (query.RecognitionLevel is { } level) heritages = heritages.Where(h => h.RecognitionLevel == level);
        if (query.Status is { } status) heritages = heritages.Where(h => h.Status == status);

        var page = await heritages
            .OrderByDescending(h => h.UpdatedAt ?? h.CreatedAt).ThenByDescending(h => h.Id)
            .Select(h => new
            {
                h.Id, h.Name, h.Slug, h.ThumbnailUrl, h.ProvinceId,
                ProvinceName = h.Province == null ? null : h.Province.Name,
                h.HeritageType, h.RecognitionLevel, h.Status, h.CreatedAt, h.UpdatedAt,
                TimelineCount = db.Timelines.Count(t => t.HeritageId == h.Id),
                MediaCount = db.Media.Count(m => m.HeritageId == h.Id),
                ReferenceCount = db.HeritageReferences.Count(r => r.HeritageId == h.Id),
            })
            .ToPagedAsync(query.Page, query.PageSize, ct);

        var items = page.Items.Select(h => new HeritageSummaryDto(
            h.Id, h.Name, h.Slug, h.ThumbnailUrl, h.ProvinceId, h.ProvinceName, h.HeritageType, h.RecognitionLevel,
            h.Status ?? ContentStatus.DRAFT, h.TimelineCount, h.MediaCount, h.ReferenceCount, h.CreatedAt, h.UpdatedAt)).ToList();
        return new PagedResult<HeritageSummaryDto>(items, page.Page, page.PageSize, page.TotalItems, page.TotalPages);
    }

    public async Task<HeritageDetailDto> GetAsync(long id, CancellationToken ct)
    {
        var heritage = await db.Heritages.AsNoTracking().Include(h => h.Province)
                           .SingleOrDefaultAsync(h => h.Id == id && h.DeletedAt == null, ct)
                       ?? throw AppException.NotFound(Messages.NotFound("xem di sản", "di sản"));
        return await ToDetailAsync(heritage, ct);
    }

    /// <summary>Đường dẫn gợi ý từ tên (bỏ dấu, chữ thường, nối bằng gạch ngang), thêm hậu tố -2, -3... nếu đã có di sản dùng.</summary>
    public async Task<string> SuggestSlugAsync(string? name, long? excludeHeritageId, CancellationToken ct)
    {
        var slug = HeritageRules.Slugify(name);
        if (slug.Length == 0) throw AppException.Validation("name", Messages.Required("tên di sản"));
        return await UniqueSlugAsync(slug, excludeHeritageId ?? 0, ct);
    }

    /// <summary>Tạo di sản ở trạng thái DRAFT. Chỉ tên là bắt buộc; các trường còn lại có thì phải hợp lệ, đủ điều kiện mới xuất bản được.</summary>
    public async Task<HeritageDetailDto> CreateAsync(HeritageRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        await EnsureProvinceExistsAsync(request.ProvinceId, ct);

        var explicitSlug = ValidationRules.TrimToNull(request.Slug);
        if (explicitSlug is not null) await EnsureSlugFreeAsync(explicitSlug, 0, ct);
        var slug = explicitSlug ?? await UniqueSlugAsync(HeritageRules.Slugify(request.Name), 0, ct);

        var heritage = new Heritage { Slug = slug, Status = ContentStatus.DRAFT };
        Apply(heritage, request);
        db.Heritages.Add(heritage);
        await SaveAsync(heritage.Slug, 0, ct);
        return await GetAsync(heritage.Id, ct);
    }

    /// <summary>
    /// Sửa thông tin chung (thay toàn bộ các trường của <see cref="HeritageRequest"/>; <c>slug</c> bỏ trống thì giữ đường dẫn hiện tại).
    /// Di sản đang PUBLISHED vẫn sửa được nhưng sau khi sửa phải còn đủ điều kiện xuất bản, không thì từ chối và không lưu gì.
    /// </summary>
    public async Task<HeritageDetailDto> UpdateAsync(long id, HeritageRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var heritage = await FindAsync(id, "sửa di sản", ct);
        await EnsureProvinceExistsAsync(request.ProvinceId, ct);

        var newSlug = ValidationRules.TrimToNull(request.Slug);
        if (newSlug is not null && newSlug != heritage.Slug) await EnsureSlugFreeAsync(newSlug, id, ct);
        Apply(heritage, request);
        if (newSlug is not null) heritage.Slug = newSlug;

        if (heritage.Status == ContentStatus.PUBLISHED) await EnsureReadyAsync(heritage, "lưu di sản đã xuất bản", ct);
        await SaveAsync(heritage.Slug, id, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// Chọn ảnh đại diện trong thư viện ảnh của chính di sản (để có ghi công), hoặc bỏ chọn khi <c>MediaId</c> là null.
    /// Di sản đang PUBLISHED phải luôn có ảnh đại diện nên không bỏ chọn được.
    /// </summary>
    public async Task<HeritageDetailDto> SetThumbnailAsync(long id, ThumbnailRequest request, CancellationToken ct)
    {
        var heritage = await FindAsync(id, "chọn ảnh đại diện", ct);
        if (request.MediaId is null)
        {
            if (heritage.Status == ContentStatus.PUBLISHED)
                throw AppException.BusinessRule(Messages.Blocked("bỏ ảnh đại diện",
                    "Di sản đang xuất bản phải có ảnh đại diện. Hãy chọn ảnh khác thay vì bỏ chọn."));
            heritage.ThumbnailUrl = null;
        }
        else
        {
            var media = await db.Media.AsNoTracking()
                            .SingleOrDefaultAsync(m => m.Id == request.MediaId && m.HeritageId == id, ct)
                        ?? throw AppException.Validation("mediaId",
                            Messages.Invalid("Ảnh đại diện", "phải chọn trong thư viện ảnh của di sản này"));
            if (media.MediaType != HeritageRules.MediaImage)
                throw AppException.Validation("mediaId", Messages.Invalid("Ảnh đại diện", "phải là ảnh (loại IMAGE)"));
            heritage.ThumbnailUrl = media.Url;
        }
        heritage.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>DRAFT sang PUBLISHED khi đủ điều kiện. Ghi HERITAGE_PUBLISHED (người thực hiện là người xuất bản).</summary>
    public async Task<HeritageDetailDto> PublishAsync(long id, Actor actor, CancellationToken ct)
    {
        const string action = "xuất bản di sản";
        var heritage = await FindAsync(id, action, ct);
        if (heritage.Status != ContentStatus.DRAFT)
            throw AppException.BusinessRule(Messages.Blocked(action, heritage.Status == ContentStatus.PUBLISHED
                ? "Di sản đang được xuất bản."
                : "Di sản đang bị ẩn, hãy dùng chức năng hiện lại."));
        await EnsureReadyAsync(heritage, action, ct);
        await MoveAsync(heritage, ContentStatus.PUBLISHED, AuditAction.HERITAGE_PUBLISHED, actor, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>PUBLISHED sang ARCHIVED: rời mọi trang công khai, không chọn được vào bài học hoặc câu hỏi mới; dữ liệu cũ giữ nguyên. Ghi HERITAGE_ARCHIVED.</summary>
    public async Task<HeritageDetailDto> ArchiveAsync(long id, Actor actor, CancellationToken ct)
    {
        const string action = "ẩn di sản";
        var heritage = await FindAsync(id, action, ct);
        if (heritage.Status != ContentStatus.PUBLISHED)
            throw AppException.BusinessRule(Messages.Blocked(action, "Chỉ ẩn được di sản đang xuất bản."));
        await MoveAsync(heritage, ContentStatus.ARCHIVED, AuditAction.HERITAGE_ARCHIVED, actor, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>ARCHIVED sang PUBLISHED, kiểm lại đủ điều kiện như xuất bản. Ghi HERITAGE_PUBLISHED.</summary>
    public async Task<HeritageDetailDto> RestoreAsync(long id, Actor actor, CancellationToken ct)
    {
        const string action = "hiện lại di sản";
        var heritage = await FindAsync(id, action, ct);
        if (heritage.Status != ContentStatus.ARCHIVED)
            throw AppException.BusinessRule(Messages.Blocked(action, "Chỉ hiện lại được di sản đang bị ẩn."));
        await EnsureReadyAsync(heritage, action, ct);
        await MoveAsync(heritage, ContentStatus.PUBLISHED, AuditAction.HERITAGE_PUBLISHED, actor, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// Xóa mềm di sản DRAFT hoặc ARCHIVED. Di sản đã gắn bài học hoặc câu hỏi chỉ được ẩn, không xóa; di sản đang xuất bản phải ẩn trước.
    /// Đường dẫn của di sản đã xóa vẫn giữ chỗ (cột slug của CSDL không trùng trên mọi dòng).
    /// </summary>
    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        const string action = "xóa di sản";
        var heritage = await FindAsync(id, action, ct);
        if (heritage.Status == ContentStatus.PUBLISHED)
            throw AppException.BusinessRule(Messages.Blocked(action, "Di sản đang xuất bản. Hãy ẩn di sản trước."));
        var used = await db.LessonHeritages.AnyAsync(l => l.HeritageId == id, ct)
                   || await db.Questions.AnyAsync(q => q.HeritageId == id, ct);
        if (used)
            throw AppException.BusinessRule(Messages.Blocked(action, "Di sản đã được dùng trong bài học hoặc câu hỏi, chỉ có thể ẩn."));
        heritage.DeletedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    private async Task MoveAsync(Heritage heritage, ContentStatus to, AuditAction action, Actor actor, CancellationToken ct)
    {
        var from = heritage.Status;
        heritage.Status = to;
        audit.Record(action, AuditTargets.Heritages, heritage.Id, actor,
            oldValue: new { status = from },
            newValue: new { status = to, name = heritage.Name, slug = heritage.Slug });
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureReadyAsync(Heritage heritage, string action, CancellationToken ct)
    {
        var media = await db.Media.AsNoTracking().Where(m => m.HeritageId == heritage.Id).ToListAsync(ct);
        var referenceCount = await db.HeritageReferences.CountAsync(r => r.HeritageId == heritage.Id, ct);
        var missing = HeritagePublishing.FindMissing(heritage, media.Any(m => HeritageMapper.IsThumbnail(m, heritage.ThumbnailUrl)),
            referenceCount, CurrentYear);
        if (missing.Count == 0) return;

        var errors = missing.ToDictionary(m => m.Field, m => $"Còn thiếu {m.Label}.");
        throw new AppException(422, Messages.NotReady(action, missing.Select(m => m.Label)), errors);
    }

    private void Apply(Heritage heritage, HeritageRequest r)
    {
        heritage.Name = r.Name!.Trim();
        heritage.ShortDescription = ValidationRules.TrimToNull(r.ShortDescription);
        heritage.Content = ValidationRules.TrimToNull(r.Content);
        heritage.HeritageType = r.HeritageType;
        heritage.RecognitionLevel = r.RecognitionLevel;
        heritage.RecognizedYear = r.RecognizedYear;
        heritage.ProvinceId = r.ProvinceId;
        heritage.Address = ValidationRules.TrimToNull(r.Address);
        heritage.Latitude = r.Latitude is { } lat ? decimal.Round(lat, HeritageRules.CoordinateScale) : null;
        heritage.Longitude = r.Longitude is { } lon ? decimal.Round(lon, HeritageRules.CoordinateScale) : null;
        heritage.PlaceId = ValidationRules.TrimToNull(r.PlaceId);
    }

    private async Task<Heritage> FindAsync(long id, string action, CancellationToken ct) =>
        await db.Heritages.SingleOrDefaultAsync(h => h.Id == id && h.DeletedAt == null, ct)
        ?? throw AppException.NotFound(Messages.NotFound(action, "di sản"));

    private async Task EnsureProvinceExistsAsync(int? provinceId, CancellationToken ct)
    {
        if (provinceId is null) return;
        if (!await db.Provinces.AnyAsync(p => p.Id == provinceId, ct))
            throw AppException.Validation("provinceId", Messages.Invalid("Tỉnh/thành", "không có trong danh sách 34 tỉnh, thành phố"));
    }

    private async Task EnsureSlugFreeAsync(string slug, long exceptId, CancellationToken ct)
    {
        if (await db.Heritages.AnyAsync(h => h.Slug == slug && h.Id != exceptId, ct)) throw SlugTaken();
    }

    private async Task<string> UniqueSlugAsync(string baseSlug, long exceptId, CancellationToken ct)
    {
        var slug = baseSlug;
        for (var n = 2; await db.Heritages.AnyAsync(h => h.Slug == slug && h.Id != exceptId, ct); n++)
        {
            var suffix = $"-{n}";
            var stem = baseSlug.Length + suffix.Length > HeritageRules.SlugMaxLength
                ? baseSlug[..(HeritageRules.SlugMaxLength - suffix.Length)].TrimEnd('-')
                : baseSlug;
            slug = stem + suffix;
        }
        return slug;
    }

    /// <summary>Lưu; hai yêu cầu cùng lúc giành một đường dẫn thì yêu cầu thua báo trùng đường dẫn thay vì lỗi hệ thống.</summary>
    private async Task SaveAsync(string? slug, long exceptId, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            if (slug is not null && await SlugExistsAsync(slug, exceptId, ct)) throw SlugTaken();
            throw;
        }
    }

    // Sau lỗi lưu, DbContext còn giữ thực thể chưa lưu; kiểm tra bằng truy vấn không theo dõi.
    private Task<bool> SlugExistsAsync(string slug, long exceptId, CancellationToken ct) =>
        db.Heritages.AsNoTracking().AnyAsync(h => h.Slug == slug && h.Id != exceptId, ct);

    private static AppException SlugTaken()
    {
        var message = Messages.Duplicate("Đường dẫn", "đã có di sản khác dùng đường dẫn này");
        return AppException.Conflict("slug", message);
    }

    private async Task<HeritageDetailDto> ToDetailAsync(Heritage heritage, CancellationToken ct)
    {
        var id = heritage.Id;
        var timelines = await db.Timelines.AsNoTracking().Where(t => t.HeritageId == id).ToListAsync(ct);
        var timelineIds = timelines.Select(t => t.Id).ToList();
        var events = await db.Events.AsNoTracking().Where(e => timelineIds.Contains(e.TimelineId)).ToListAsync(ct);
        var eventIds = events.Select(e => e.Id).ToList();
        var media = await db.Media.AsNoTracking()
            .Where(m => m.HeritageId == id || (m.EventId != null && eventIds.Contains(m.EventId.Value))).ToListAsync(ct);
        var references = await db.HeritageReferences.AsNoTracking().Where(r => r.HeritageId == id).OrderBy(r => r.Id).ToListAsync(ct);

        var heritageMedia = media.Where(m => m.HeritageId == id).ToList();
        var mediaByEvent = media.Where(m => m.EventId != null).ToLookup(m => m.EventId!.Value);
        var eventsByTimeline = events.ToLookup(e => e.TimelineId);

        return new HeritageDetailDto(
            heritage.Id, heritage.Name, heritage.Slug, heritage.ShortDescription, heritage.Content,
            heritage.HeritageType, heritage.RecognitionLevel, heritage.RecognizedYear,
            heritage.Province is null ? null : HeritageMapper.ToDto(heritage.Province),
            heritage.Address, heritage.Latitude, heritage.Longitude, heritage.PlaceId, heritage.ThumbnailUrl,
            heritage.Status ?? ContentStatus.DRAFT, heritage.CreatedAt, heritage.UpdatedAt,
            HeritageMapper.BuildReadiness(heritage, heritageMedia, references, CurrentYear),
            HeritageMapper.OrderTimelines(timelines).Select(t => HeritageMapper.ToDto(t, eventsByTimeline[t.Id], mediaByEvent)).ToList(),
            HeritageMapper.OrderMedia(heritageMedia).Select(m => HeritageMapper.ToDto(m, heritage.ThumbnailUrl)).ToList(),
            references.Select(HeritageMapper.ToDto).ToList());
    }
}
