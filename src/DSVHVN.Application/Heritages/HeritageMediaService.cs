using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Heritages;

/// <summary>
/// Thư viện ảnh của di sản và ảnh minh họa của sự kiện. Chỉ lưu đường dẫn (ảnh không tải lên hệ thống; video, audio là đường dẫn nhúng),
/// kèm ghi công bắt buộc "tác giả · giấy phép · nguồn". Mỗi dòng thuộc đúng một trong hai: di sản hoặc sự kiện.
/// Ảnh đại diện là một ảnh trong thư viện của di sản, nhận biết bằng đường dẫn trùng <c>heritages.thumbnail_url</c>.
/// </summary>
public sealed class HeritageMediaService(IAppDbContext db, RequestValidator validator, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<MediaDto> AddToHeritageAsync(long heritageId, MediaRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var heritage = await db.FindHeritageAsync(heritageId, "thêm ảnh", ct);
        var url = request.Url!.Trim();
        await EnsureUrlFreeAsync(m => m.HeritageId == heritageId, url, 0, ct);

        var media = new Media
        {
            HeritageId = heritageId,
            SortOrder = request.SortOrder ?? await NextOrderAsync(m => m.HeritageId == heritageId, ct),
        };
        Apply(media, request);
        db.Media.Add(media);
        if (request.SetAsThumbnail == true)
        {
            RequireImageForThumbnail(media);
            heritage.ThumbnailUrl = media.Url;
        }
        heritage.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return HeritageMapper.ToDto(media, heritage.ThumbnailUrl);
    }

    public async Task<MediaDto> AddToEventAsync(long eventId, MediaRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        if (request.SetAsThumbnail == true)
            throw AppException.Validation("setAsThumbnail", Messages.Invalid("Ảnh đại diện", "chỉ chọn được trong thư viện ảnh của di sản"));
        var item = await db.FindEventAsync(eventId, "thêm ảnh cho sự kiện", ct);
        await EnsureUrlFreeAsync(m => m.EventId == eventId, request.Url!.Trim(), 0, ct);

        var media = new Media
        {
            EventId = eventId,
            SortOrder = request.SortOrder ?? await NextOrderAsync(m => m.EventId == eventId, ct),
        };
        Apply(media, request);
        db.Media.Add(media);
        item.Timeline!.Heritage!.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return HeritageMapper.ToDto(media, null);
    }

    /// <summary>
    /// Sửa ảnh (thay toàn bộ các trường của <see cref="MediaRequest"/>). Ảnh đang là ảnh đại diện mà đổi đường dẫn thì ảnh đại diện đổi theo;
    /// <c>SetAsThumbnail</c> true chọn làm ảnh đại diện, false bỏ chọn (không bỏ được khi di sản đang xuất bản), bỏ trống giữ nguyên.
    /// </summary>
    public async Task<MediaDto> UpdateAsync(long mediaId, MediaRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var media = await db.FindMediaAsync(mediaId, "sửa ảnh", ct);
        var heritage = HeritageLookup.HeritageOf(media);
        var url = request.Url!.Trim();
        var ownerHeritageId = media.HeritageId;
        var ownerEventId = media.EventId;
        await EnsureUrlFreeAsync(ownerHeritageId is not null ? m => m.HeritageId == ownerHeritageId : m => m.EventId == ownerEventId,
            url, mediaId, ct);

        var wasThumbnail = HeritageMapper.IsThumbnail(media, heritage.ThumbnailUrl);
        Apply(media, request);
        if (request.SortOrder is not null) media.SortOrder = request.SortOrder;

        if (media.HeritageId is null)
        {
            if (request.SetAsThumbnail == true)
                throw AppException.Validation("setAsThumbnail", Messages.Invalid("Ảnh đại diện", "chỉ chọn được trong thư viện ảnh của di sản"));
        }
        else if (request.SetAsThumbnail == false && wasThumbnail)
        {
            EnsureThumbnailNotRequired(heritage, "bỏ ảnh đại diện", "Di sản đang xuất bản phải có ảnh đại diện. Hãy chọn ảnh khác thay vì bỏ chọn.");
            heritage.ThumbnailUrl = null;
        }
        else if (wasThumbnail || request.SetAsThumbnail == true)
        {
            RequireImageForThumbnail(media);
            heritage.ThumbnailUrl = media.Url;
        }

        heritage.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return HeritageMapper.ToDto(media, heritage.ThumbnailUrl);
    }

    /// <summary>Xóa ảnh. Ảnh đại diện của di sản đang xuất bản không xóa được (chọn ảnh đại diện khác trước); di sản chưa xuất bản thì bỏ luôn ảnh đại diện.</summary>
    public async Task DeleteAsync(long mediaId, CancellationToken ct)
    {
        var media = await db.FindMediaAsync(mediaId, "xóa ảnh", ct);
        var heritage = HeritageLookup.HeritageOf(media);
        if (HeritageMapper.IsThumbnail(media, heritage.ThumbnailUrl))
        {
            EnsureThumbnailNotRequired(heritage, "xóa ảnh",
                "Đây là ảnh đại diện của di sản đang xuất bản. Hãy chọn ảnh đại diện khác trước.");
            heritage.ThumbnailUrl = null;
        }
        db.Media.Remove(media);
        heritage.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Đặt thứ tự thư viện ảnh của di sản: <c>Ids</c> là toàn bộ ảnh của di sản (không gồm ảnh của sự kiện) theo thứ tự mới.</summary>
    public async Task<IReadOnlyList<MediaDto>> ReorderHeritageMediaAsync(long heritageId, OrderRequest request, CancellationToken ct)
    {
        var heritage = await db.FindHeritageAsync(heritageId, "sắp xếp ảnh", ct);
        var media = await db.Media.Where(m => m.HeritageId == heritageId).ToListAsync(ct);
        var ids = HeritageLookup.ValidateOrder(request, media.Select(m => m.Id).ToList(), "ảnh");
        for (var i = 0; i < ids.Count; i++) media.Single(m => m.Id == ids[i]).SortOrder = i + 1;
        heritage.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return HeritageMapper.OrderMedia(media).Select(m => HeritageMapper.ToDto(m, heritage.ThumbnailUrl)).ToList();
    }

    /// <summary>Đặt thứ tự ảnh minh họa của một sự kiện.</summary>
    public async Task<IReadOnlyList<MediaDto>> ReorderEventMediaAsync(long eventId, OrderRequest request, CancellationToken ct)
    {
        var item = await db.FindEventAsync(eventId, "sắp xếp ảnh của sự kiện", ct);
        var media = await db.Media.Where(m => m.EventId == eventId).ToListAsync(ct);
        var ids = HeritageLookup.ValidateOrder(request, media.Select(m => m.Id).ToList(), "ảnh");
        for (var i = 0; i < ids.Count; i++) media.Single(m => m.Id == ids[i]).SortOrder = i + 1;
        item.Timeline!.Heritage!.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return HeritageMapper.OrderMedia(media).Select(m => HeritageMapper.ToDto(m, null)).ToList();
    }

    private static void Apply(Media media, MediaRequest r)
    {
        media.MediaType = r.MediaType!.Trim();
        media.Url = r.Url!.Trim();
        media.Caption = ValidationRules.TrimToNull(r.Caption);
        media.Credit = HeritageRules.ComposeCredit(r.Author!, r.License!, r.Source!);
    }

    private static void RequireImageForThumbnail(Media media)
    {
        if (media.MediaType != HeritageRules.MediaImage)
            throw AppException.Validation("setAsThumbnail", Messages.Invalid("Ảnh đại diện", "phải là ảnh (loại IMAGE)"));
    }

    private static void EnsureThumbnailNotRequired(Heritage heritage, string action, string reason)
    {
        if (heritage.Status == ContentStatus.PUBLISHED) throw AppException.BusinessRule(Messages.Blocked(action, reason));
    }

    /// <summary>Không có hai ảnh cùng đường dẫn trong cùng thư viện (di sản hoặc sự kiện), vì ảnh đại diện nhận biết bằng đường dẫn.</summary>
    private async Task EnsureUrlFreeAsync(System.Linq.Expressions.Expression<Func<Media, bool>> sameLibrary, string url, long exceptId,
        CancellationToken ct)
    {
        if (await db.Media.Where(sameLibrary).AnyAsync(m => m.Url == url && m.Id != exceptId, ct))
            throw AppException.Conflict("url", Messages.Duplicate("Đường dẫn", "đường dẫn này đã có trong thư viện"));
    }

    private async Task<int> NextOrderAsync(System.Linq.Expressions.Expression<Func<Media, bool>> sameLibrary, CancellationToken ct) =>
        (await db.Media.Where(sameLibrary).MaxAsync(m => (int?)m.SortOrder, ct) ?? 0) + 1;
}
