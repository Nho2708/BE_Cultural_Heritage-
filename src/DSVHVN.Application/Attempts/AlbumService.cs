using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Attempts;

/// <summary>Album di sản của một em đã dựng xong: mọi thẻ theo thứ tự hiện và số thẻ đã có.</summary>
public sealed record AlbumData(IReadOnlyList<StudentAlbumCardDto> Cards)
{
    public int Collected => Cards.Count(c => c.Collected);

    public int Total => Cards.Count;
}

/// <summary>
/// Album di sản của em ở cấp 1, tính khi hiển thị (chỉ có bảng ghi bài đã học xong, không có bảng thẻ). Mỗi di sản (đã xuất bản, chưa xóa) của các bài chưa xóa
/// của lớp là một thẻ: bài Đã xuất bản, hoặc bài Lưu trữ mà em đã có thẻ. Một di sản nằm ở nhiều bài vẫn là một thẻ, ghi bài sớm nhất. Thẻ đã có khi em ghi
/// bài chứa di sản đó là đã học xong, hoặc có lượt đã nộp (nộp tay hay hết giờ) của một bài kiểm tra thuộc bài đó; thời điểm có thẻ là lúc sớm nhất của hai nguồn.
/// Thẻ đã có xếp trước (mới nhất trước), thẻ chưa có sau theo thứ tự bài học.
/// </summary>
public sealed class AlbumService(IAppDbContext db)
{
    internal async Task<AlbumData> LoadAsync(StudentContext student, CancellationToken ct) => await LoadAsync(student.Id, student.ClassId, ct);

    public async Task<AlbumData> LoadAsync(long studentId, long classId, CancellationToken ct)
    {
        // Bài chưa xóa của lớp theo thứ tự học (thầy cô tạo bài trước thì học trước).
        var lessons = await db.Lessons.AsNoTracking()
            .Where(l => l.ClassId == classId && l.DeletedAt == null && (l.Status == ContentStatus.PUBLISHED || l.Status == ContentStatus.ARCHIVED))
            .OrderBy(l => l.CreatedAt).ThenBy(l => l.Id)
            .Select(l => new { l.Id, l.Title, l.Status })
            .ToListAsync(ct);
        if (lessons.Count == 0) return new AlbumData([]);
        var lessonIds = lessons.Select(l => l.Id).ToList();

        // Lúc em có thẻ của từng bài: sớm nhất của lần ghi bài đã học xong và lượt đã nộp.
        var completions = await db.LessonCompletions.AsNoTracking()
            .Where(c => c.StudentId == studentId && lessonIds.Contains(c.LessonId))
            .Select(c => new { c.LessonId, At = (DateTime?)c.CompletedAt })
            .ToListAsync(ct);
        var submissions = await db.Attempts.AsNoTracking()
            .Where(a => a.StudentId == studentId && a.Status != AttemptStatus.IN_PROGRESS && lessonIds.Contains(a.Quiz!.LessonId))
            .Select(a => new { a.Quiz!.LessonId, At = a.SubmittedAt ?? a.StartedAt })
            .ToListAsync(ct);
        var collectedAtByLesson = completions.Concat(submissions)
            .Where(x => x.At != null)
            .GroupBy(x => x.LessonId)
            .ToDictionary(g => g.Key, g => g.Min(x => x.At!.Value));

        var visible = lessons.Where(l => l.Status == ContentStatus.PUBLISHED || collectedAtByLesson.ContainsKey(l.Id)).ToList();
        var visibleIds = visible.Select(l => l.Id).ToList();
        var links = await db.LessonHeritages.AsNoTracking()
            .Where(lh => visibleIds.Contains(lh.LessonId) && lh.Heritage!.DeletedAt == null && lh.Heritage.Status == ContentStatus.PUBLISHED)
            .Select(lh => new
            {
                lh.LessonId, lh.HeritageId, lh.Heritage!.Slug, lh.Heritage.Name, lh.Heritage.ShortDescription, lh.Heritage.ThumbnailUrl,
                lh.Heritage.HeritageType,
            })
            .ToListAsync(ct);

        var cards = new List<(StudentAlbumCardDto Card, int LessonIndex)>();
        foreach (var group in links.GroupBy(x => x.HeritageId))
        {
            // Bài sớm nhất của lớp có di sản này (theo thứ tự học).
            var first = group.Select(x => (Link: x, Index: visible.FindIndex(l => l.Id == x.LessonId))).OrderBy(x => x.Index).First();
            var collectedAt = group.Select(x => collectedAtByLesson.TryGetValue(x.LessonId, out var at) ? (DateTime?)at : null)
                .Where(x => x != null).Min();
            var link = first.Link;
            cards.Add((new StudentAlbumCardDto(
                link.HeritageId, link.Slug ?? string.Empty, link.Name ?? string.Empty, link.ShortDescription, link.ThumbnailUrl, link.HeritageType,
                AlbumRules.GroupName(link.HeritageType), collectedAt != null, collectedAt, link.LessonId, visible[first.Index].Title), first.Index));
        }

        var ordered = cards.Where(c => c.Card.Collected).OrderByDescending(c => c.Card.CollectedAt).ThenBy(c => c.LessonIndex).ThenBy(c => c.Card.HeritageId)
            .Concat(cards.Where(c => !c.Card.Collected).OrderBy(c => c.LessonIndex).ThenBy(c => c.Card.HeritageId))
            .Select(c => c.Card)
            .ToList();
        return new AlbumData(ordered);
    }

    /// <summary>
    /// Thẻ các di sản (đã xuất bản) của một bài, đều đã có (em vừa học xong bài). Thẻ lấy từ album cho khớp bài sớm nhất; di sản không nằm trong album
    /// (bài đã bị xóa) thì dựng từ chính bài này.
    /// </summary>
    public async Task<List<StudentAlbumCardDto>> LessonCardsAsync(AlbumData album, long lessonId, string? lessonTitle, DateTime? fallbackCollectedAt, CancellationToken ct)
    {
        var heritages = await db.LessonHeritages.AsNoTracking()
            .Where(lh => lh.LessonId == lessonId && lh.Heritage!.DeletedAt == null && lh.Heritage.Status == ContentStatus.PUBLISHED)
            .OrderBy(lh => lh.HeritageId)
            .Select(lh => new
            {
                lh.HeritageId, lh.Heritage!.Slug, lh.Heritage.Name, lh.Heritage.ShortDescription, lh.Heritage.ThumbnailUrl, lh.Heritage.HeritageType,
            })
            .ToListAsync(ct);
        return heritages.Select(h =>
        {
            var card = album.Cards.FirstOrDefault(c => c.HeritageId == h.HeritageId);
            return card is null
                ? new StudentAlbumCardDto(h.HeritageId, h.Slug ?? string.Empty, h.Name ?? string.Empty, h.ShortDescription, h.ThumbnailUrl, h.HeritageType,
                    AlbumRules.GroupName(h.HeritageType), true, fallbackCollectedAt, lessonId, lessonTitle)
                : card with { Collected = true, CollectedAt = card.CollectedAt ?? fallbackCollectedAt };
        }).ToList();
    }

    /// <summary>Câu mang về nhà theo di sản đầu tiên của bài (thứ tự mã di sản, chỉ di sản đã xuất bản); bài không có di sản thì null.</summary>
    public async Task<string?> TakeHomeAsync(long lessonId, CancellationToken ct)
    {
        var first = await db.LessonHeritages.AsNoTracking()
            .Where(lh => lh.LessonId == lessonId && lh.Heritage!.DeletedAt == null && lh.Heritage.Status == ContentStatus.PUBLISHED)
            .OrderBy(lh => lh.HeritageId)
            .Select(lh => new { lh.Heritage!.Name, lh.Heritage.HeritageType })
            .FirstOrDefaultAsync(ct);
        return first is null ? null : AlbumRules.TakeHomeQuestion(first.HeritageType, first.Name);
    }

    /// <summary>
    /// Lời gọi từ yêu cầu hiện tại của em: dựng album của chính em. Dùng ở trang Album và các nơi cần tổng số thẻ.
    /// </summary>
    public async Task<StudentAlbumDto> GetAsync(Actor actor, CancellationToken ct)
    {
        var student = await StudentContext.LoadAsync(db, actor, ct);
        var album = await LoadAsync(student, ct);
        return new StudentAlbumDto(album.Collected, album.Total, album.Cards);
    }
}
