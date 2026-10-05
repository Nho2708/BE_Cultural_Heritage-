using DSVHVN.Application.Common;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Lessons;

/// <summary>
/// Dựng dữ liệu gửi Dịch vụ AI từ các di sản: nội dung, dòng thời gian, nguồn tham chiếu và ảnh có sẵn (AI chỉ chọn trong ảnh có sẵn).
/// Dùng chung cho soạn nội dung bài học và sinh câu hỏi. Không có dữ liệu cá nhân nào của học sinh hay lớp.
/// </summary>
public sealed class AiHeritageInputBuilder(IAppDbContext db)
{
    public async Task<List<AiHeritageInput>> BuildAsync(List<long> heritageIds, LessonMaterials materials, CancellationToken ct)
    {
        var heritages = await db.Heritages.AsNoTracking().Where(h => heritageIds.Contains(h.Id))
            .Select(h => new { h.Id, h.Name, h.ShortDescription, h.Content }).ToListAsync(ct);
        var references = await db.HeritageReferences.AsNoTracking().Where(r => heritageIds.Contains(r.HeritageId))
            .OrderBy(r => r.Id).Select(r => new { r.HeritageId, r.Title, r.Url }).ToListAsync(ct);
        var events = await db.Events.AsNoTracking().Where(e => heritageIds.Contains(e.Timeline!.HeritageId))
            .Select(e => new { e.Id, e.Name, e.Description, e.StartYear, HeritageId = e.Timeline!.HeritageId, Phase = e.Timeline!.Name })
            .ToListAsync(ct);
        var dateLabels = materials.EventsById;

        return heritageIds.Select(id =>
        {
            var h = heritages.Single(x => x.Id == id);
            return new AiHeritageInput(
                h.Id, h.Name ?? string.Empty, h.ShortDescription, h.Content,
                events.Where(e => e.HeritageId == id).OrderBy(e => e.StartYear).ThenBy(e => e.Id)
                    .Select(e => new AiTimelineInput(e.Id, e.Phase ?? string.Empty, e.Name ?? string.Empty, e.Description, e.StartYear,
                        dateLabels.TryGetValue(e.Id, out var option) ? option.DateLabel : HeritageTimeText.Year(e.StartYear))).ToList(),
                references.Where(r => r.HeritageId == id).Select(r => new AiSourceInput(r.Title ?? string.Empty, r.Url)).ToList(),
                materials.Images.Where(i => i.HeritageId == id && i.Url is not null)
                    .Select(i => new AiImageInput(i.MediaId, i.Url!, i.Caption)).ToList());
        }).ToList();
    }
}
