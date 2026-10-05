using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using DSVHVN.Application.Common;
using DSVHVN.Application.Heritages;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Lessons;

/// <summary>Ý chính của bài lưu trong cột <c>lessons.key_points</c> dạng mảng JSON; giữ chữ tiếng Việt đọc được trong CSDL.</summary>
internal static class LessonJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() },
    };

    public static string? SerializeKeyPoints(IEnumerable<string> points)
    {
        var list = points.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        return list.Count == 0 ? null : JsonSerializer.Serialize(list, Options);
    }

    public static IReadOnlyList<string> ParseKeyPoints(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>
/// Thư viện ảnh và dòng thời gian của các di sản của bài: nguồn để giáo viên chọn hình cho bước câu chuyện, hình, mốc thời gian,
/// để kiểm hình và sự kiện giáo viên gửi lên có thuộc di sản của bài, và để AI chọn hình (AI không tạo ảnh mới).
/// </summary>
public sealed class LessonMaterials
{
    public required IReadOnlyList<LessonImageOptionDto> Images { get; init; }
    public required IReadOnlyList<LessonEventOptionDto> Events { get; init; }

    public IReadOnlyDictionary<string, LessonImageOptionDto> ImagesByUrl =>
        _byUrl ??= Images.Where(i => !string.IsNullOrEmpty(i.Url)).GroupBy(i => i.Url!).ToDictionary(g => g.Key, g => g.First());

    public IReadOnlyDictionary<long, LessonEventOptionDto> EventsById => _byEvent ??= Events.ToDictionary(e => e.EventId);

    private IReadOnlyDictionary<string, LessonImageOptionDto>? _byUrl;
    private IReadOnlyDictionary<long, LessonEventOptionDto>? _byEvent;

    public LessonImageCreditDto? CreditOf(string? url) =>
        url is not null && ImagesByUrl.TryGetValue(url, out var i)
            ? new LessonImageCreditDto(i.Caption, i.Credit, i.Author, i.License, i.Source)
            : null;

    public LessonMaterialsDto ToDto() => new(Images, Events);
}

/// <summary>Đọc <see cref="LessonMaterials"/> từ CSDL cho một tập di sản.</summary>
public sealed class LessonMaterialService(IAppDbContext db)
{
    public async Task<LessonMaterials> LoadAsync(IReadOnlyCollection<long> heritageIds, CancellationToken ct)
    {
        if (heritageIds.Count == 0) return new LessonMaterials { Images = [], Events = [] };

        var names = await db.Heritages.AsNoTracking().Where(h => heritageIds.Contains(h.Id))
            .Select(h => new { h.Id, h.Name }).ToDictionaryAsync(h => h.Id, h => h.Name, ct);

        var onHeritage = await db.Media.AsNoTracking()
            .Where(m => m.MediaType == HeritageRules.MediaImage && m.HeritageId != null && heritageIds.Contains(m.HeritageId.Value))
            .OrderBy(m => m.SortOrder ?? int.MaxValue).ThenBy(m => m.Id)
            .ToListAsync(ct);
        var onEvents = await db.Media.AsNoTracking()
            .Where(m => m.MediaType == HeritageRules.MediaImage && m.EventId != null && heritageIds.Contains(m.Event!.Timeline!.HeritageId))
            .Select(m => new { Media = m, HeritageId = m.Event!.Timeline!.HeritageId })
            .ToListAsync(ct);

        var images = new List<LessonImageOptionDto>();
        foreach (var m in onHeritage) images.Add(ToImage(m, m.HeritageId!.Value, null, names));
        foreach (var e in onEvents.OrderBy(e => e.Media.SortOrder ?? int.MaxValue).ThenBy(e => e.Media.Id))
            images.Add(ToImage(e.Media, e.HeritageId, e.Media.EventId, names));

        var events = await db.Events.AsNoTracking()
            .Where(e => heritageIds.Contains(e.Timeline!.HeritageId))
            .Select(e => new
            {
                e.Id, e.Name, e.StartYear, e.StartMonth, e.StartDay, e.EndYear, e.EndMonth, e.EndDay,
                HeritageId = e.Timeline!.HeritageId, Phase = e.Timeline!.Name, PhaseOrder = e.Timeline!.SortOrder,
            })
            .ToListAsync(ct);
        var eventOptions = events
            .OrderBy(e => e.StartYear).ThenBy(e => e.StartMonth ?? 0).ThenBy(e => e.StartDay ?? 0).ThenBy(e => e.Id)
            .Select(e => new LessonEventOptionDto(e.Id, e.HeritageId, names.GetValueOrDefault(e.HeritageId), e.Phase, e.Name, e.StartYear,
                HeritageTimeText.EventRange(e.StartYear, e.StartMonth, e.StartDay, e.EndYear, e.EndMonth, e.EndDay)))
            .ToList();

        return new LessonMaterials { Images = images, Events = eventOptions };
    }

    private static LessonImageOptionDto ToImage(Media m, long heritageId, long? eventId, Dictionary<long, string?> names)
    {
        var parts = HeritageRules.ParseCredit(m.Credit);
        return new LessonImageOptionDto(m.Id, m.Url, m.Caption, m.Credit, parts?.Author, parts?.License, parts?.Source,
            heritageId, names.GetValueOrDefault(heritageId), eventId);
    }
}

/// <summary>Đổi thực thể phần nội dung sang DTO, dùng chung cho giáo viên và học sinh.</summary>
internal static class LessonMapper
{
    public static string? MilestoneLabel(int? year) => year is null ? null : HeritageTimeText.Year(year.Value);

    public static LessonSectionDto ToDto(LessonSection s, LessonMaterials materials) => new(
        s.Id == 0 ? null : s.Id, s.SectionType, LessonFrames.LabelOf(s.SectionType), s.Title, s.Body, s.ImageUrl,
        materials.CreditOf(s.ImageUrl), s.MilestoneYear, MilestoneLabel(s.MilestoneYear), s.EventId, s.SourceCitation, s.SortOrder);

    public static StudentSectionDto ToStudentDto(LessonSection s, LessonMaterials materials) => new(
        s.SectionType, s.Title, s.Body, s.ImageUrl, materials.CreditOf(s.ImageUrl), s.MilestoneYear, MilestoneLabel(s.MilestoneYear),
        s.EventId, s.SourceCitation, s.SortOrder);

    public static LessonSectionView ToView(LessonSection s) =>
        new(s.SectionType, s.Body, s.ImageUrl, s.MilestoneYear, s.SourceCitation);

    public static LessonReadinessDto ToDto(LessonCheck check) => new(check.IsReady, check.Missing, check.Warnings);
}
