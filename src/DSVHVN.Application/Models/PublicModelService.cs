using DSVHVN.Application.Common;
using DSVHVN.Application.Files;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Models;

/// <summary>
/// Mô hình 3D cho khách và học sinh (nút "Xem 3D", trình xem dùng chung của web học tập và app). Chỉ mô hình đang hiển thị của di sản đã xuất bản; xem miễn phí,
/// không qua quảng cáo, không cần đăng nhập. Di sản không có mô hình đang hiển thị, hoặc mô hình vừa bị ẩn thì 404 kèm mã MODEL_UNAVAILABLE:
/// trình xem quay về bộ ảnh của di sản.
/// </summary>
public sealed class PublicModelService(IAppDbContext db)
{
    /// <summary>Mô hình của di sản đã xuất bản theo đường dẫn (<c>slug</c>).</summary>
    public async Task<PublicModelDto> GetAsync(string? slug, CancellationToken ct)
    {
        var key = slug?.Trim().ToLowerInvariant();
        var heritage = HeritageRules.IsValidSlug(key)
            ? await db.Heritages.AsNoTracking()
                .Where(h => h.Slug == key && h.DeletedAt == null && h.Status == ContentStatus.PUBLISHED)
                .Select(h => new { h.Id }).SingleOrDefaultAsync(ct)
            : null;
        if (heritage is null) throw AppException.NotFound(Messages.NotFound("xem mô hình 3D", "di sản"));
        return await VisibleAsync(heritage.Id, ct);
    }

    /// <summary>Mô hình đang hiển thị của một di sản (không kiểm trạng thái di sản; việc đó là của người gọi, vd trang xem trước bản nháp).</summary>
    public async Task<PublicModelDto> VisibleAsync(long heritageId, CancellationToken ct)
    {
        var model = await db.HeritageModels.AsNoTracking()
            .Where(m => m.HeritageId == heritageId && m.DeletedAt == null && m.Status == ContentStatus.PUBLISHED)
            .OrderBy(m => m.SortOrder ?? int.MaxValue).ThenByDescending(m => m.Id)
            .FirstOrDefaultAsync(ct);
        return model is null ? throw AppException.NotFound(ModelMessages.Unavailable) : ToDto(model);
    }

    /// <summary>Di sản có mô hình đang hiển thị không (bật nút "Xem 3D").</summary>
    public Task<bool> HasVisibleAsync(long heritageId, CancellationToken ct) =>
        db.HeritageModels.AsNoTracking().AnyAsync(m => m.HeritageId == heritageId && m.DeletedAt == null && m.Status == ContentStatus.PUBLISHED, ct);

    private static PublicModelDto ToDto(HeritageModel m) => new(
        m.Id, m.Title, m.FileUrl, ModelFileService.ContentType, m.FileSizeBytes, m.PosterUrl, m.Author, m.LicenseName, m.LicenseUrl, m.SourceUrl, m.Credit);
}
