using System.Text.Json;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Infrastructure.Persistence.Seed;

/// <summary>Kết quả một lần nạp dữ liệu tỉnh/thành và di sản.</summary>
public sealed class HeritageSeedResult
{
    public int ProvincesAdded { get; set; }
    public int ProvincesExisting { get; set; }
    public int HeritagesAdded { get; set; }
    public int HeritagesExisting { get; set; }
    public int TimelinesAdded { get; set; }
    public int EventsAdded { get; set; }
    public int MediaAdded { get; set; }
    public int ReferencesAdded { get; set; }

    /// <summary>Di sản bị bỏ qua vì dữ liệu không khớp cột (quá độ dài, sai giá trị enum, thiếu năm của sự kiện...).</summary>
    public List<string> Rejected { get; } = [];

    /// <summary>Trường có trong tệp nhưng CSDL không có cột tương ứng (không lưu).</summary>
    public SortedSet<string> IgnoredFields { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Nạp danh mục 34 tỉnh/thành và các di sản (kèm giai đoạn, sự kiện, ảnh, nguồn) từ tệp JSON của nhóm dữ liệu vào bảng
/// <c>provinces</c>, <c>heritages</c>, <c>timelines</c>, <c>events</c>, <c>media</c>, <c>heritage_references</c>.
/// Chạy lại được: tỉnh đã có mã thì bỏ qua, di sản đã có slug thì bỏ qua cả di sản (không ghi đè, không nhân đôi).
/// Dữ liệu không khớp cột thì không ép (không cắt chuỗi, không đoán giá trị): bỏ qua di sản đó và ghi lý do vào kết quả.
/// </summary>
public static class HeritageDataSeeder
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // Trường của tệp dữ liệu được đọc vào cột; trường khác của mỗi di sản được liệt kê là "không lưu".
    private static readonly HashSet<string> MappedHeritageFields =
    [
        "province_code", "name", "slug", "short_description", "content", "heritage_type", "recognition_level",
        "recognized_year", "address", "latitude", "longitude", "place_id", "thumbnail_url", "status",
        "timelines", "media", "references",
    ];

    public static async Task<HeritageSeedResult> SeedAsync(AppDbContext db, string provincesFile, string heritagesFile,
        CancellationToken ct)
    {
        var result = new HeritageSeedResult();
        var provinces = await ReadAsync<List<ProvinceJson>>(provincesFile, ct);
        var heritageFile = await ReadAsync<HeritageFileJson>(heritagesFile, ct);
        using var heritageDocument = JsonDocument.Parse(await File.ReadAllTextAsync(heritagesFile, ct));
        CollectIgnoredFields(heritageDocument.RootElement, result);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var existingCodes = (await db.Provinces.Select(p => p.Code).ToListAsync(ct)).Where(c => c != null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var p in provinces)
        {
            if (existingCodes.Contains(p.Code))
            {
                result.ProvincesExisting++;
                continue;
            }
            var province = new Province { Code = p.Code, Name = p.Name, Region = p.Region };
            var problems = ColumnFit.Check(db, province);
            if (problems.Count > 0)
            {
                result.Rejected.Add($"Tỉnh {p.Code}: {string.Join("; ", problems)}");
                continue;
            }
            db.Provinces.Add(province);
            existingCodes.Add(p.Code);
            result.ProvincesAdded++;
        }
        await db.SaveChangesAsync(ct);

        var provinceIds = await db.Provinces.Where(p => p.Code != null).ToDictionaryAsync(p => p.Code!, p => p.Id, ct);
        var existingSlugs = (await db.Heritages.Select(h => h.Slug).ToListAsync(ct)).Where(s => s != null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var source in (heritageFile.Provinces ?? []).SelectMany(p => p.Heritages ?? []))
        {
            var label = source.Slug ?? source.Name ?? "(không tên)";
            if (source.Slug is not null && existingSlugs.Contains(source.Slug))
            {
                result.HeritagesExisting++;
                continue;
            }

            var problems = new List<string>();
            var heritage = ToHeritage(source, provinceIds, problems);
            problems.AddRange(CheckGraph(db, heritage));
            if (problems.Count > 0)
            {
                result.Rejected.Add($"Di sản {label}: {string.Join("; ", problems)}");
                continue;
            }

            db.Heritages.Add(heritage);
            if (heritage.Slug is not null) existingSlugs.Add(heritage.Slug);
            result.HeritagesAdded++;
            result.TimelinesAdded += heritage.Timelines.Count;
            result.EventsAdded += heritage.Timelines.Sum(t => t.Events.Count);
            result.MediaAdded += heritage.Media.Count + heritage.Timelines.Sum(t => t.Events.Sum(e => e.Media.Count));
            result.ReferencesAdded += heritage.References.Count;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private static Heritage ToHeritage(HeritageJson s, IReadOnlyDictionary<string, int> provinceIds, List<string> problems)
    {
        int? provinceId = null;
        if (s.ProvinceCode is { } code)
        {
            if (provinceIds.TryGetValue(code, out var id)) provinceId = id;
            else problems.Add($"không có tỉnh mã {code}");
        }

        return new Heritage
        {
            ProvinceId = provinceId,
            Name = s.Name,
            Slug = s.Slug,
            ShortDescription = s.ShortDescription,
            Content = s.Content,
            HeritageType = ParseEnum<HeritageType>(s.HeritageType, "heritage_type", problems),
            RecognitionLevel = ParseEnum<RecognitionLevel>(s.RecognitionLevel, "recognition_level", problems),
            RecognizedYear = s.RecognizedYear,
            Address = s.Address,
            Latitude = s.Latitude,
            Longitude = s.Longitude,
            PlaceId = s.PlaceId,
            ThumbnailUrl = s.ThumbnailUrl,
            Status = ParseEnum<ContentStatus>(s.Status, "status", problems),
            Timelines = (s.Timelines ?? []).Select(t => new Timeline
            {
                Name = t.Name,
                Description = t.Description,
                StartYear = t.StartYear,
                EndYear = t.EndYear,
                SortOrder = t.SortOrder,
                Events = (t.Events ?? []).Select(e => ToEvent(e, problems)).ToList(),
            }).ToList(),
            Media = (s.Media ?? []).Select(ToMedia).ToList(),
            References = (s.References ?? []).Select(r => new HeritageReference
            {
                Title = r.Title,
                Url = r.Url,
                SourceType = r.SourceType,
            }).ToList(),
        };
    }

    private static TimelineEvent ToEvent(EventJson e, List<string> problems)
    {
        // events.start_year là NOT NULL: sự kiện thiếu năm thì không nạp, không tự đặt năm.
        if (e.StartYear is null) problems.Add($"sự kiện \"{e.Name}\" thiếu năm bắt đầu");
        return new TimelineEvent
        {
            Name = e.Name,
            Description = e.Description,
            StartYear = e.StartYear ?? 0,
            StartMonth = e.StartMonth,
            StartDay = e.StartDay,
            EndYear = e.EndYear,
            EndMonth = e.EndMonth,
            EndDay = e.EndDay,
            SortOrder = e.SortOrder,
            Media = (e.Media ?? []).Select(ToMedia).ToList(),
        };
    }

    private static Media ToMedia(MediaJson m) => new()
    {
        MediaType = m.MediaType,
        Url = m.Url,
        Caption = m.Caption,
        Credit = m.Credit,
        SortOrder = m.SortOrder,
    };

    private static TEnum? ParseEnum<TEnum>(string? value, string column, List<string> problems) where TEnum : struct, Enum
    {
        if (value is null) return null;
        if (Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)) return parsed;
        problems.Add($"{column} = \"{value}\" không thuộc danh sách giá trị");
        return null;
    }

    private static IEnumerable<string> CheckGraph(AppDbContext db, Heritage h)
    {
        foreach (var p in ColumnFit.Check(db, h)) yield return "heritages." + p;
        foreach (var t in h.Timelines)
        {
            foreach (var p in ColumnFit.Check(db, t)) yield return "timelines." + p;
            foreach (var e in t.Events)
            {
                foreach (var p in ColumnFit.Check(db, e)) yield return "events." + p;
                foreach (var m in e.Media)
                foreach (var p in ColumnFit.Check(db, m)) yield return "media." + p;
            }
        }
        foreach (var m in h.Media)
        foreach (var p in ColumnFit.Check(db, m)) yield return "media." + p;
        foreach (var r in h.References)
        foreach (var p in ColumnFit.Check(db, r)) yield return "heritage_references." + p;
    }

    private static void CollectIgnoredFields(JsonElement root, HeritageSeedResult result)
    {
        if (!root.TryGetProperty("provinces", out var provinces) || provinces.ValueKind != JsonValueKind.Array) return;
        foreach (var province in provinces.EnumerateArray())
        {
            if (!province.TryGetProperty("heritages", out var heritages) || heritages.ValueKind != JsonValueKind.Array) continue;
            foreach (var heritage in heritages.EnumerateArray())
            foreach (var field in heritage.EnumerateObject())
                if (!MappedHeritageFields.Contains(field.Name)) result.IgnoredFields.Add(field.Name);
        }
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct)
               ?? throw new InvalidDataException($"Tệp dữ liệu rỗng: {path}");
    }

    private sealed record ProvinceJson(string Code, string? Name, string? Region);

    private sealed record HeritageFileJson(List<ProvinceGroupJson>? Provinces);

    private sealed record ProvinceGroupJson(string? Code, List<HeritageJson>? Heritages);

    private sealed record HeritageJson(
        string? ProvinceCode, string? Name, string? Slug, string? ShortDescription, string? Content,
        string? HeritageType, string? RecognitionLevel, int? RecognizedYear, string? Address,
        decimal? Latitude, decimal? Longitude, string? PlaceId, string? ThumbnailUrl, string? Status,
        List<TimelineJson>? Timelines, List<MediaJson>? Media, List<ReferenceJson>? References);

    private sealed record TimelineJson(
        string? Name, string? Description, int? StartYear, int? EndYear, int? SortOrder, List<EventJson>? Events);

    private sealed record EventJson(
        string? Name, string? Description, int? StartYear, short? StartMonth, short? StartDay,
        int? EndYear, short? EndMonth, short? EndDay, int? SortOrder, List<MediaJson>? Media);

    private sealed record MediaJson(string? MediaType, string? Url, string? Caption, string? Credit, int? SortOrder);

    private sealed record ReferenceJson(string? Title, string? Url, string? SourceType);
}

/// <summary>Kiểm một thực thể có vừa cột của CSDL không (độ dài chuỗi, số chữ số của decimal) theo chính model EF Core.</summary>
internal static class ColumnFit
{
    public static List<string> Check(AppDbContext db, object entity)
    {
        var problems = new List<string>();
        var type = db.Model.FindEntityType(entity.GetType())
                   ?? throw new InvalidOperationException($"{entity.GetType().Name} không thuộc model.");
        foreach (var property in type.GetProperties())
        {
            if (property.PropertyInfo is null) continue;
            var value = property.PropertyInfo.GetValue(entity);
            var column = property.GetColumnName();
            switch (value)
            {
                case string text when property.GetMaxLength() is { } max && text.Length > max:
                    problems.Add($"{column} dài {text.Length} ký tự, cột tối đa {max}");
                    break;
                case decimal number when property.GetPrecision() is { } precision && property.GetScale() is { } scale:
                    if (!FitsDecimal(number, precision, scale))
                        problems.Add($"{column} = {number} không vừa decimal({precision},{scale})");
                    break;
            }
        }
        return problems;
    }

    private static bool FitsDecimal(decimal value, int precision, int scale)
    {
        // Không được làm tròn (số chữ số thập phân vượt scale) và phần nguyên không vượt precision - scale chữ số.
        if (decimal.Round(value, scale) != value) return false;
        var integerDigits = Math.Truncate(Math.Abs(value)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            .TrimStart('0').Length;
        return integerDigits <= precision - scale;
    }
}
