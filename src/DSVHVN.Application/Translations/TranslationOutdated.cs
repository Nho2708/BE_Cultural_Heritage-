using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Translations;

/// <summary>
/// Đánh dấu "cần dịch lại" (<c>is_outdated = 1</c>) cho mọi bản dịch và tóm tắt của một di sản khi phần chữ tiếng Việt của di sản, giai đoạn, sự kiện hoặc
/// chú thích ảnh bị sửa sau khi dịch. Cũng tính tình trạng bản dịch của một di sản cho màn hình Biên tập di sản (nhắc "Dịch và tóm tắt bằng AI" khi chưa dịch). Các bản vẫn hiện như cũ cho tới khi quản trị hệ thống bấm dịch lại. Chạy sau khi lưu thay đổi; di sản chưa dịch thì không có gì để đánh dấu.
/// </summary>
public static class TranslationOutdated
{
    /// <summary>Tình trạng bản dịch của một di sản (xem <see cref="TranslationState"/>): cần dịch lại trước, rồi chờ duyệt, rồi đã dịch, không có bản nào thì chưa dịch.</summary>
    public static async Task<TranslationState> StateOfAsync(IAppDbContext db, long heritageId, CancellationToken ct)
    {
        var rows = await db.HeritageTranslations.AsNoTracking().Where(t => t.HeritageId == heritageId)
            .Select(t => new { t.IsOutdated, t.Status }).ToListAsync(ct);
        rows.AddRange(await db.HeritageSummaries.AsNoTracking().Where(t => t.HeritageId == heritageId)
            .Select(t => new { t.IsOutdated, t.Status }).ToListAsync(ct));
        if (rows.Count == 0) return TranslationState.NONE;
        if (rows.Any(r => r.IsOutdated)) return TranslationState.OUTDATED;
        return rows.Any(r => r.Status == TranslationStatus.PENDING) ? TranslationState.PENDING : TranslationState.APPROVED;
    }

    public static async Task MarkAsync(IAppDbContext db, long heritageId, CancellationToken ct)
    {
        await db.HeritageTranslations.Where(t => t.HeritageId == heritageId && !t.IsOutdated)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsOutdated, true), ct);
        await db.HeritageSummaries.Where(t => t.HeritageId == heritageId && !t.IsOutdated)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsOutdated, true), ct);
    }
}
