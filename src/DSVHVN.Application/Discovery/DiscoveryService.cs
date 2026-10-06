using System.Linq.Expressions;
using DSVHVN.Application.Common;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Models;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Discovery;

/// <summary>
/// Khám phá di sản công khai của web học tập (khách và học sinh, không cần đăng nhập): trang chủ, tìm kiếm và lọc, bản đồ,
/// chi tiết, timeline. Mọi truy vấn chỉ thấy di sản PUBLISHED chưa xóa mềm; di sản nháp, đã ẩn hay đã xóa đều như không tồn tại (404).
/// Chỉ đọc, không ghi CSDL. Mọi truy vấn nhận ngôn ngữ của khách (<see cref="LanguageCode"/>): tên, mô tả, nội dung, giai đoạn, sự kiện, chú thích ảnh lấy bản dịch hiện hành
/// của ngôn ngữ đó (tiếng Anh đã duyệt, tiếng Hàn, Trung, Nhật là bản dịch máy kèm nhãn); phần chưa có bản hiện hành lùi về tiếng Việt kèm cờ lùi.
/// <para>
/// Tìm từ khóa không dấu lẫn có dấu: cả từ khóa và cột <c>heritages.search_text</c> cùng được gấp bằng <see cref="SearchText.Fold"/>
/// (bỏ dấu, đ thành d, chữ thường) rồi so bằng LIKE, vì collation Vietnamese_CI_AI không coi ă, â, ê, ô, ơ, ư, đ là chữ có dấu.
/// Mỗi từ của từ khóa khớp đầu một từ trong di sản ("thang lo" ra "Thăng Long"); nhiều từ thì di sản phải có đủ các từ (VÀ,
/// không cần đứng liền nhau); kết quả khớp nguyên cụm trong tên đứng trước.
/// </para>
/// </summary>
public sealed class DiscoveryService(IAppDbContext db, HeritageLocalizer localizer, PublicModelService models)
{
    /// <summary>Trang chủ hiện tối đa 8 di sản nổi bật.</summary>
    public const int FeaturedLimit = 8;

    /// <summary>Bản đồ trả tối đa 500 ghim mỗi lần.</summary>
    public const int MaxMapPins = 500;

    // Hạng để xếp kết quả: UNESCO trước, rồi quốc gia đặc biệt, quốc gia, cấp tỉnh, chưa xếp hạng.
    private static readonly Expression<Func<Heritage, int>> LevelRank = h =>
        h.RecognitionLevel == RecognitionLevel.UNESCO ? 0
        : h.RecognitionLevel == RecognitionLevel.NATIONAL_SPECIAL ? 1
        : h.RecognitionLevel == RecognitionLevel.NATIONAL ? 2
        : h.RecognitionLevel == RecognitionLevel.PROVINCIAL ? 3
        : 4;

    private static readonly Expression<Func<Heritage, HeritageCardDto>> ToCard = h => new HeritageCardDto(
        h.Id, h.Slug!, h.Name!, h.ShortDescription, h.ThumbnailUrl,
        h.ProvinceId,
        h.Province == null ? null : h.Province.Name,
        h.Province == null ? null : h.Province.Region,
        h.HeritageType, h.RecognitionLevel, h.RecognizedYear, false, false);

    private IQueryable<Heritage> Published() =>
        db.Heritages.AsNoTracking().Where(h => h.DeletedAt == null && h.Status == ContentStatus.PUBLISHED);

    // ---- Danh mục ----

    /// <summary>34 tỉnh/thành theo mã, kèm vùng miền; mở cho khách để dựng ô lọc.</summary>
    public async Task<IReadOnlyList<ProvinceDto>> ListProvincesAsync(CancellationToken ct) =>
        (await db.Provinces.AsNoTracking().OrderBy(p => p.Code).ThenBy(p => p.Id).ToListAsync(ct))
        .Select(HeritageMapper.ToDto).ToList();

    public async Task<FilterCatalogDto> GetFilterCatalogAsync(CancellationToken ct) => new(
        await ListProvincesAsync(ct),
        HeritageLabels.Regions.Select(r => new FilterOptionDto(r, HeritageLabels.Region(r))).ToList(),
        Enum.GetValues<HeritageType>().Select(t => new FilterOptionDto(t.ToString(), HeritageLabels.Type(t))).ToList(),
        Enum.GetValues<RecognitionLevel>().Select(l => new FilterOptionDto(l.ToString(), HeritageLabels.Level(l))).ToList(),
        HeritagePeriods.All.Select(p => new PeriodOptionDto(p.Period.ToString(), p.Label, p.FromYear, p.ToYear)).ToList());

    // ---- Trang chủ ----

    /// <summary>
    /// Di sản nổi bật theo quy tắc cố định trong mã (không cấu hình được): tối đa 8 di sản cấp UNESCO hoặc quốc gia đặc biệt,
    /// mới cập nhật trước. Kèm tổng số di sản và số theo loại, theo vùng cho khối "khám phá theo loại và vùng".
    /// </summary>
    public async Task<HomeDto> GetHomeAsync(LanguageCode language, CancellationToken ct)
    {
        var featured = await GetFeaturedAsync(FeaturedLimit, language, ct);

        var total = await Published().CountAsync(ct);
        var byType = await Published().Where(h => h.HeritageType != null)
            .GroupBy(h => h.HeritageType!.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var byRegion = await Published().Where(h => h.Province != null && h.Province.Region != null)
            .GroupBy(h => h.Province!.Region!).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);

        return new HomeDto(
            featured,
            total,
            Enum.GetValues<HeritageType>()
                .Select(t => new CountItemDto(t.ToString(), HeritageLabels.Type(t), byType.FirstOrDefault(x => x.Key == t)?.Count ?? 0))
                .ToList(),
            HeritageLabels.Regions
                .Select(r => new CountItemDto(r, HeritageLabels.Region(r), byRegion.FirstOrDefault(x => x.Key == r)?.Count ?? 0))
                .ToList());
    }

    /// <summary>Di sản nổi bật của trang chủ (UNESCO hoặc quốc gia đặc biệt, mới cập nhật trước), lấy tối đa <paramref name="limit"/> di sản; dùng chung với gợi ý cho khách chưa có lịch sử.</summary>
    internal async Task<IReadOnlyList<HeritageCardDto>> GetFeaturedAsync(int limit, LanguageCode language, CancellationToken ct)
    {
        var featured = await Published()
            .Where(h => h.RecognitionLevel == RecognitionLevel.UNESCO || h.RecognitionLevel == RecognitionLevel.NATIONAL_SPECIAL)
            .OrderByDescending(h => h.UpdatedAt ?? h.CreatedAt).ThenByDescending(h => h.Id)
            .Take(limit)
            .Select(ToCard)
            .ToListAsync(ct);
        return await LocalizeCardsAsync(featured, language, ct);
    }

    /// <summary>
    /// Thẻ của các di sản đã xuất bản theo danh sách id, giữ đúng thứ tự của <paramref name="ids"/> và theo ngôn ngữ yêu cầu; id không có, chưa xuất bản hoặc đã xóa thì bỏ qua.
    /// Dùng cho lịch sử, gợi ý và "Gần bạn" của khách.
    /// </summary>
    internal async Task<IReadOnlyList<HeritageCardDto>> GetCardsAsync(IReadOnlyList<long> ids, LanguageCode language, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var cards = await Published().Where(h => ids.Contains(h.Id)).Select(ToCard).ToListAsync(ct);
        var byId = (await LocalizeCardsAsync(cards, language, ct)).ToDictionary(c => c.Id);
        return ids.Distinct().Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    // ---- Tìm kiếm và lọc ----

    /// <summary>
    /// Tìm và lọc di sản đã xuất bản, phân trang (mặc định 20, tối đa 100). Kết quả xếp: khớp nguyên cụm từ khóa trong tên trước,
    /// rồi cấp xếp hạng cao trước, rồi theo tên.
    /// </summary>
    public async Task<PagedResult<HeritageCardDto>> SearchAsync(HeritageFilter filter, LanguageCode language, int? page, int? pageSize, CancellationToken ct)
    {
        var prepared = Prepare(filter);
        Paging.Normalize(page, pageSize);
        var result = await Ordered(Filtered(Published(), filter, prepared, language), prepared, language)
            .Select(ToCard)
            .ToPagedAsync(page, pageSize, ct);
        return result with { Items = await LocalizeCardsAsync(result.Items, language, ct) };
    }

    // ---- Bản đồ ----

    /// <summary>Ghim bản đồ theo cùng bộ lọc với tìm kiếm; chỉ di sản có tọa độ được ghim, tối đa <see cref="MaxMapPins"/>.</summary>
    public async Task<HeritageMapDto> GetMapAsync(HeritageFilter filter, LanguageCode language, CancellationToken ct)
    {
        var prepared = Prepare(filter);
        var matched = Filtered(Published(), filter, prepared, language);
        var located = matched.Where(h => h.Latitude != null && h.Longitude != null);

        var totalMatched = await matched.CountAsync(ct);
        var locatedCount = await located.CountAsync(ct);
        var rows = await Ordered(located, prepared, language)
            .Take(MaxMapPins)
            .Select(h => new
            {
                h.Id, h.Slug, h.Name, h.Latitude, h.Longitude, h.ShortDescription, h.ThumbnailUrl, h.ProvinceId,
                ProvinceName = h.Province == null ? null : h.Province.Name,
                h.HeritageType, h.RecognitionLevel,
                HasTimeline = h.Timelines.Any(),
            })
            .ToListAsync(ct);

        var pins = rows.Select(r => new HeritagePinDto(
            r.Id, r.Slug!, r.Name!, r.Latitude!.Value, r.Longitude!.Value, r.ShortDescription, r.ThumbnailUrl,
            r.ProvinceId, r.ProvinceName, r.HeritageType, r.RecognitionLevel, r.HasTimeline)).ToList();
        if (language != LanguageCode.vi && pins.Count > 0)
        {
            var translated = await localizer.LoadVisibleAsync(pins.Select(p => p.Id).ToList(), language, ct);
            pins = pins.Select(p => translated.TryGetValue(p.Id, out var row)
                ? p with { Name = row.Name, ShortDescription = row.ShortDescription ?? p.ShortDescription, IsMachineTranslated = row.Status == TranslationStatus.MACHINE }
                : p with { IsFallback = true }).ToList();
        }
        return new HeritageMapDto(pins, totalMatched, totalMatched - locatedCount, locatedCount > pins.Count);
    }

    // ---- Chi tiết ----

    /// <summary>Chi tiết theo đường dẫn (<c>slug</c>); di sản không có, chưa xuất bản, đã ẩn hoặc đã xóa đều 404.</summary>
    public async Task<PublicHeritageDetailDto> GetDetailAsync(string? slug, LanguageCode language, CancellationToken ct)
    {
        var heritage = await FindPublishedAsync(slug, "xem di sản", withProvince: true, ct);
        return await BuildDetailAsync(heritage, language, ct);
    }

    /// <summary>
    /// Dựng chi tiết công khai từ một di sản đã nạp kèm tỉnh. Dùng chung cho trang chi tiết (di sản đã xuất bản) và trang xem trước bản nháp của
    /// quản trị hệ thống, để hai nơi hiện đúng cùng một dữ liệu; việc kiểm trạng thái là của người gọi.
    /// </summary>
    public async Task<PublicHeritageDetailDto> BuildDetailAsync(Heritage heritage, LanguageCode language, CancellationToken ct)
    {
        var parts = await localizer.LoadPartsAsync(heritage.Id, language, ct);
        var media = HeritageMapper.OrderMedia(
            await db.Media.AsNoTracking().Where(m => m.HeritageId == heritage.Id).ToListAsync(ct)).ToList();
        var references = await db.HeritageReferences.AsNoTracking()
            .Where(r => r.HeritageId == heritage.Id).OrderBy(r => r.Id).ToListAsync(ct);
        var timelines = HeritageMapper.OrderTimelines(
            await db.Timelines.AsNoTracking().Where(t => t.HeritageId == heritage.Id).ToListAsync(ct)).ToList();
        var eventCounts = await db.Events.AsNoTracking().Where(e => e.Timeline!.HeritageId == heritage.Id)
            .GroupBy(e => e.TimelineId).Select(g => new { TimelineId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TimelineId, x => x.Count, ct);

        return new PublicHeritageDetailDto(
            heritage.Id, heritage.Slug!, parts.Name(heritage.Name), parts.ShortDescription(heritage.ShortDescription), parts.Content(heritage.Content),
            heritage.HeritageType, heritage.RecognitionLevel, heritage.RecognizedYear,
            heritage.Province is null ? null : HeritageMapper.ToDto(heritage.Province),
            heritage.Address, heritage.Latitude, heritage.Longitude, heritage.ThumbnailUrl,
            timelines.Count > 0,
            media.Select(m => ToPublic(m, heritage.ThumbnailUrl, parts)).ToList(),
            references.Select(HeritageMapper.ToDto).ToList(),
            timelines.Select(t => new TimelinePhaseSummaryDto(
                t.Id, parts.TimelineName(t.Id, t.Name), t.StartYear, t.EndYear, HeritageTimeText.PhaseRange(t.StartYear, t.EndYear),
                eventCounts.GetValueOrDefault(t.Id))).ToList(),
            HeritageLocalizer.Describe(language, parts.Row),
            await localizer.HasSummaryAsync(heritage.Id, ct),
            await models.HasVisibleAsync(heritage.Id, ct));
    }

    // ---- Timeline ----

    /// <summary>
    /// Timeline đầy đủ của một di sản: giai đoạn theo thứ tự hiển thị, sự kiện trong từng giai đoạn xếp theo năm-tháng-ngày rồi thứ tự,
    /// kèm ảnh của sự kiện. Không có trục thời gian chung nhiều di sản.
    /// </summary>
    public async Task<PublicTimelineDto> GetTimelineAsync(string? slug, LanguageCode language, CancellationToken ct)
    {
        var heritage = await FindPublishedAsync(slug, "xem dòng thời gian", withProvince: true, ct);
        return await BuildTimelineAsync(heritage, language, ct);
    }

    /// <summary>Dựng timeline đầy đủ từ một di sản đã nạp kèm tỉnh (dùng chung với trang xem trước bản nháp); việc kiểm trạng thái là của người gọi.</summary>
    public async Task<PublicTimelineDto> BuildTimelineAsync(Heritage heritage, LanguageCode language, CancellationToken ct)
    {
        var parts = await localizer.LoadPartsAsync(heritage.Id, language, ct);
        var timelines = await db.Timelines.AsNoTracking().Include(t => t.Events)
            .Where(t => t.HeritageId == heritage.Id).ToListAsync(ct);
        var eventMedia = (await db.Media.AsNoTracking()
                .Where(m => m.EventId != null && m.Event!.Timeline!.HeritageId == heritage.Id).ToListAsync(ct))
            .ToLookup(m => m.EventId!.Value);

        var phases = HeritageMapper.OrderTimelines(timelines).Select(t => new PublicPhaseDto(
            t.Id, parts.TimelineName(t.Id, t.Name), parts.TimelineDescription(t.Id, t.Description), t.StartYear, t.EndYear,
            HeritageTimeText.PhaseRange(t.StartYear, t.EndYear),
            HeritageMapper.OrderEvents(t.Events).Select(e => new PublicEventDto(
                e.Id, parts.EventName(e.Id, e.Name), parts.EventDescription(e.Id, e.Description), e.StartYear, e.StartMonth, e.StartDay, e.EndYear, e.EndMonth, e.EndDay,
                HeritageTimeText.EventRange(e.StartYear, e.StartMonth, e.StartDay, e.EndYear, e.EndMonth, e.EndDay),
                HeritageMapper.OrderMedia(eventMedia[e.Id]).Select(m => ToPublic(m, null, parts)).ToList())).ToList())).ToList();

        return new PublicTimelineDto(heritage.Id, heritage.Slug!, parts.Name(heritage.Name), heritage.ThumbnailUrl, heritage.Province?.Name, phases,
            HeritageLocalizer.Describe(language, parts.Row));
    }

    // ---- Dụng cụ ----

    private async Task<Heritage> FindPublishedAsync(string? slug, string action, bool withProvince, CancellationToken ct)
    {
        var key = slug?.Trim().ToLowerInvariant();
        var query = Published();
        if (withProvince) query = query.Include(h => h.Province);
        var heritage = HeritageRules.IsValidSlug(key) ? await query.SingleOrDefaultAsync(h => h.Slug == key, ct) : null;
        return heritage ?? throw AppException.NotFound(Messages.NotFound(action, "di sản"));
    }

    private async Task<IReadOnlyList<HeritageCardDto>> LocalizeCardsAsync(IReadOnlyList<HeritageCardDto> cards, LanguageCode language, CancellationToken ct)
    {
        if (language == LanguageCode.vi || cards.Count == 0) return cards;
        var translated = await localizer.LoadVisibleAsync(cards.Select(c => c.Id).ToList(), language, ct);
        return cards.Select(c => translated.TryGetValue(c.Id, out var row)
            ? c with { Name = row.Name, ShortDescription = row.ShortDescription ?? c.ShortDescription, IsMachineTranslated = row.Status == TranslationStatus.MACHINE }
            : c with { IsFallback = true }).ToList();
    }

    private static PublicMediaDto ToPublic(Media m, string? thumbnailUrl, LocalizedParts localized)
    {
        var parts = HeritageRules.ParseCredit(m.Credit);
        return new PublicMediaDto(m.Id, m.MediaType, m.Url, localized.Caption(m.Id, m.Caption), m.Credit, parts?.Author, parts?.License, parts?.Source,
            HeritageMapper.IsThumbnail(m, thumbnailUrl), StoredFileRules.ThumbnailUrlOf(m.Url));
    }

    /// <summary>
    /// Bộ lọc đã kiểm tra: các từ của từ khóa (đã gấp), cụm từ khóa gấp để xếp hạng theo tên, vùng đã đưa về giá trị CSDL; <c>Words</c> và <c>RawPhrase</c> là từ khóa
    /// nguyên dạng (tách theo dấu cách) để so với bản dịch (chữ Hàn, Trung, Nhật không gấp được như tiếng Việt).
    /// </summary>
    private sealed record Prepared(IReadOnlyList<string> Tokens, string? Phrase, string? Region, IReadOnlyList<string> Words, string? RawPhrase);

    private static Prepared Prepare(HeritageFilter filter)
    {
        var tokens = (IReadOnlyList<string>)[];
        IReadOnlyList<string> words = [];
        string? phrase = null;
        string? rawPhrase = null;
        var keyword = filter.Keyword?.Trim();
        if (!string.IsNullOrEmpty(keyword))
        {
            if (keyword.Length is < SearchText.KeywordMinLength or > SearchText.KeywordMaxLength)
                throw AppException.Validation("keyword", Messages.Invalid("Từ khóa",
                    $"phải dài {SearchText.KeywordMinLength}-{SearchText.KeywordMaxLength} ký tự"));
            tokens = SearchText.Tokens(keyword);
            if (tokens.Count == 0)
                throw AppException.Validation("keyword", Messages.Invalid("Từ khóa", "phải có ít nhất một chữ cái hoặc chữ số"));
            phrase = SearchText.Fold(keyword);
            rawPhrase = keyword;
            words = keyword.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(SearchText.MaxKeywordTokens).ToList();
        }

        string? region = null;
        if (!string.IsNullOrWhiteSpace(filter.Region))
        {
            region = HeritageLabels.NormalizeRegion(filter.Region)
                     ?? throw AppException.Validation("region", Messages.Invalid("Vùng", "chỉ nhận Bắc, Trung hoặc Nam"));
        }

        if (filter.HeritageType is { } type && !Enum.IsDefined(type))
            throw AppException.Validation("heritageType",
                Messages.Invalid("Loại di sản", "chỉ nhận TANGIBLE, INTANGIBLE, NATURAL, MIXED hoặc DOCUMENTARY"));
        if (filter.RecognitionLevel is { } level && !Enum.IsDefined(level))
            throw AppException.Validation("recognitionLevel",
                Messages.Invalid("Cấp xếp hạng", "chỉ nhận UNESCO, NATIONAL_SPECIAL, NATIONAL, PROVINCIAL hoặc NONE"));
        if (filter.Period is { } period && !Enum.IsDefined(period))
            throw AppException.Validation("period",
                Messages.Invalid("Thời kỳ", "chỉ nhận PREHISTORIC, NORTHERN_RULE, INDEPENDENT_FEUDAL, MODERN hoặc CONTEMPORARY"));

        return new Prepared(tokens, phrase, region, words, rawPhrase);
    }

    private IQueryable<Heritage> Filtered(IQueryable<Heritage> heritages, HeritageFilter filter, Prepared prepared, LanguageCode language)
    {
        if (prepared.Tokens.Count > 0)
        {
            // Từ khóa khớp bản tiếng Việt (không dấu) và, khi khách chọn ngôn ngữ khác, cả bản dịch hiện hành của ngôn ngữ đó.
            if (language == LanguageCode.vi)
            {
                foreach (var token in prepared.Tokens)
                {
                    var pattern = SearchText.WordPrefixPattern(token);
                    heritages = heritages.Where(h => EF.Functions.Like(h.SearchText!, pattern));
                }
            }
            else
            {
                var translated = VisibleTranslations(language);
                foreach (var word in prepared.Words)
                {
                    var like = LikeContains(word);
                    translated = translated.Where(t => EF.Functions.Like(t.Name, like, LikeEscape)
                                                       || EF.Functions.Like(t.ShortDescription!, like, LikeEscape)
                                                       || EF.Functions.Like(t.Content!, like, LikeEscape));
                }
                var vietnamese = db.Heritages.AsNoTracking();
                foreach (var token in prepared.Tokens)
                {
                    var pattern = SearchText.WordPrefixPattern(token);
                    vietnamese = vietnamese.Where(h => EF.Functions.Like(h.SearchText!, pattern));
                }
                var vietnameseIds = vietnamese.Select(h => h.Id);
                var translatedIds = translated.Select(t => t.HeritageId);
                heritages = heritages.Where(h => vietnameseIds.Contains(h.Id) || translatedIds.Contains(h.Id));
            }
        }
        if (filter.ProvinceId is { } provinceId) heritages = heritages.Where(h => h.ProvinceId == provinceId);
        if (prepared.Region is { } region) heritages = heritages.Where(h => h.Province != null && h.Province.Region == region);
        if (filter.HeritageType is { } type) heritages = heritages.Where(h => h.HeritageType == type);
        if (filter.RecognitionLevel is { } level) heritages = heritages.Where(h => h.RecognitionLevel == level);
        if (filter.Period is { } period) heritages = InPeriod(heritages, HeritagePeriods.Of(period));
        return heritages;
    }

    /// <summary>
    /// Di sản thuộc thời kỳ khi có ít nhất một giai đoạn hoặc sự kiện giao với khoảng năm của thời kỳ; di sản chưa có timeline thì không hiện.
    /// Giai đoạn chưa có năm kết thúc là còn kéo dài đến nay; giai đoạn chỉ có năm kết thúc tính như một mốc; sự kiện chưa có
    /// thời điểm kết thúc là một mốc tại năm bắt đầu.
    /// </summary>
    private static IQueryable<Heritage> InPeriod(IQueryable<Heritage> heritages, PeriodRange range)
    {
        var from = range.FromYear ?? int.MinValue;
        var to = range.ToYear ?? int.MaxValue;
        return heritages.Where(h => h.Timelines.Any(t =>
            (t.StartYear != null && t.StartYear <= to && (t.EndYear == null || t.EndYear >= from))
            || (t.StartYear == null && t.EndYear != null && t.EndYear >= from && t.EndYear <= to)
            || t.Events.Any(e => e.StartYear <= to && (e.EndYear ?? e.StartYear) >= from)));
    }

    private IOrderedQueryable<Heritage> Ordered(IQueryable<Heritage> heritages, Prepared prepared, LanguageCode language)
    {
        if (prepared.Phrase is null) return heritages.OrderBy(LevelRank).ThenBy(h => h.Name).ThenBy(h => h.Id);
        var namePattern = SearchText.WordPrefixPattern(prepared.Phrase);
        if (language == LanguageCode.vi)
            return heritages.OrderByDescending(h => EF.Functions.Like(h.SearchName!, namePattern))
                .ThenBy(LevelRank).ThenBy(h => h.Name).ThenBy(h => h.Id);

        var phraseLike = LikeContains(prepared.RawPhrase!);
        var translatedNameIds = VisibleTranslations(language).Where(t => EF.Functions.Like(t.Name, phraseLike, LikeEscape)).Select(t => t.HeritageId);
        return heritages.OrderByDescending(h => EF.Functions.Like(h.SearchName!, namePattern) || translatedNameIds.Contains(h.Id))
            .ThenBy(LevelRank).ThenBy(h => h.Name).ThenBy(h => h.Id);
    }

    private const string LikeEscape = "\\";

    /// <summary>Bản dịch hiện hành của một ngôn ngữ: tiếng Anh đã duyệt, tiếng Hàn, Trung, Nhật là bản dịch máy.</summary>
    private IQueryable<HeritageTranslation> VisibleTranslations(LanguageCode language) =>
        db.HeritageTranslations.AsNoTracking().Where(t => t.LanguageCode == language
            && (t.Status == TranslationStatus.APPROVED || t.Status == TranslationStatus.MACHINE));

    /// <summary>Mẫu LIKE khớp bất cứ chỗ nào trong chữ, đã escape ký tự đại diện của người dùng (dấu phần trăm, gạch dưới, ngoặc vuông, gạch chéo ngược).</summary>
    private static string LikeContains(string text) =>
        "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%";
}
