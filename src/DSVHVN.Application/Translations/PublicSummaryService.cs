using System.Text.Json;
using DSVHVN.Application.Ads;
using DSVHVN.Application.Common;
using DSVHVN.Application.Narrations;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Translations;

/// <summary>
/// Đọc tóm tắt di sản cho khách và học sinh (nút "Tóm tắt" và "Nghe", không cần đăng nhập): lấy từ bản đã lưu, không gọi AI. Chỉ di sản đã xuất bản.
/// <list type="bullet">
/// <item><b>Không có tóm tắt tiếng Việt đã duyệt</b> thì di sản chưa có tóm tắt ở bất kỳ ngôn ngữ nào: 404 kèm mã <c>SUMMARY_UNAVAILABLE</c> (giao diện báo "chưa có tóm tắt",
/// không hiện cổng quảng cáo, không mở khóa, không tính lượt). Bản Hàn, Trung, Nhật dịch từ bản tiếng Việt nên chỉ hiện sau khi bản tiếng Việt được duyệt.</item>
/// <item>Ngôn ngữ yêu cầu có bản hiện hành (tiếng Anh đã duyệt, Hàn, Trung, Nhật bản dịch máy) thì trả bản đó; không có thì trả tóm tắt tiếng Việt đã duyệt kèm cờ lùi
/// (nghe bản lùi thì đọc bằng giọng tiếng Việt). Bản "cần dịch lại" vẫn hiện bản cũ. Quy tắc chọn này nằm ở <see cref="ShownLanguage"/> và
/// <see cref="ListShownAsync"/> dùng chung cho danh sách thuyết minh còn thiếu.</item>
/// <item>Thuyết minh tạo sẵn trả kèm khi có bản cho đúng ngôn ngữ của tóm tắt được trả và băm còn khớp chữ; không thì null (giao diện đọc bằng giọng thiết bị).</item>
/// <item>Tên di sản theo ngôn ngữ yêu cầu như trang chi tiết.</item>
/// <item><b>Cổng quảng cáo:</b> khách chưa mở khóa di sản này trên thiết bị của mình (xem quảng cáo có thưởng của Google, 24 giờ) thì 403 <c>AD_UNLOCK_REQUIRED</c>; học sinh đã đăng nhập,
/// cổng tắt và thiết bị khai báo không có quảng cáo (khi môi trường cho phép) thì mở thẳng. Cổng kiểm sau khi biết di sản có tóm tắt, nên di sản chưa có tóm tắt không hiện cổng.</item>
/// </list>
/// </summary>
public sealed class PublicSummaryService(IAppDbContext db, HeritageLocalizer localizer, AdUnlockService unlocks)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PublicSummaryDto> GetAsync(string? slug, LanguageCode language, PublicViewer viewer, CancellationToken ct)
    {
        var key = slug?.Trim().ToLowerInvariant();
        var heritage = HeritageRules.IsValidSlug(key)
            ? await db.Heritages.AsNoTracking()
                .Where(h => h.Slug == key && h.DeletedAt == null && h.Status == ContentStatus.PUBLISHED)
                .Select(h => new { h.Id, h.Slug, h.Name }).SingleOrDefaultAsync(ct)
            : null;
        if (heritage is null) throw AppException.NotFound(Messages.NotFound("xem tóm tắt", "di sản"));

        var rows = await db.HeritageSummaries.AsNoTracking()
            .Where(s => s.HeritageId == heritage.Id && (s.LanguageCode == language || s.LanguageCode == LanguageCode.vi)).ToListAsync(ct);
        var vietnamese = rows.SingleOrDefault(s => s.LanguageCode == LanguageCode.vi && s.Status == TranslationStatus.APPROVED)
                         ?? throw AppException.NotFound(TranslationMessages.NoSummary);

        await unlocks.EnsureUnlockedAsync(viewer, heritage.Id, heritage.Name!, ct);

        var shownLanguage = ShownLanguage(language, rows.SingleOrDefault(s => s.LanguageCode == language)?.Status);
        var chosen = shownLanguage == LanguageCode.vi ? vietnamese : rows.Single(s => s.LanguageCode == shownLanguage);
        var fallback = shownLanguage != language;

        var names = await localizer.LoadVisibleAsync([heritage.Id], language, ct);
        var localization = new LocalizationDto(
            Languages.Tag(language), Languages.Tag(chosen.LanguageCode), fallback, chosen.Status == TranslationStatus.MACHINE);
        var narration = await NarrationOfAsync(heritage.Id, chosen.LanguageCode, chosen.ShortText, ct);
        return new PublicSummaryDto(heritage.Id, heritage.Slug!, names.TryGetValue(heritage.Id, out var translated) ? translated.Name : heritage.Name!,
            localization, chosen.ShortText, chosen.DetailedText, narration);
    }

    /// <summary>
    /// Ngôn ngữ của bản tóm tắt sẽ hiện khi khách yêu cầu <paramref name="requested"/>, biết trạng thái bản tóm tắt của ngôn ngữ đó (null khi chưa có) và đã có
    /// tóm tắt tiếng Việt đã duyệt: tiếng Việt luôn là bản tiếng Việt; ngôn ngữ khác hiện khi bản đó đang hiện (đã duyệt hoặc bản dịch máy, kể cả đang "cần dịch lại"),
    /// không thì lùi về tiếng Việt.
    /// </summary>
    public static LanguageCode ShownLanguage(LanguageCode requested, TranslationStatus? requestedStatus) =>
        requested == LanguageCode.vi || (requestedStatus is { } status && Languages.IsVisible(status)) ? requested : LanguageCode.vi;

    /// <summary>Một bản tóm tắt ngắn đang hiện công khai ở một ngôn ngữ (không tính bản lùi về tiếng Việt).</summary>
    public sealed record ShownSummary(long HeritageId, string Slug, string Name, string ShortText);

    /// <summary>
    /// Mọi bản tóm tắt ngắn đang hiện công khai ở <paramref name="language"/>, cùng quy tắc với <see cref="GetAsync"/>: di sản đã xuất bản và chưa xóa, có tóm tắt tiếng Việt
    /// đã duyệt, và bản của ngôn ngữ đó được <see cref="ShownLanguage"/> chọn (bản lùi về tiếng Việt không tính vì thuyết minh của nó là bản tiếng Việt). Theo mã di sản tăng dần.
    /// </summary>
    public async Task<IReadOnlyList<ShownSummary>> ListShownAsync(LanguageCode language, CancellationToken ct)
    {
        var candidates = await db.HeritageSummaries.AsNoTracking()
            .Where(s => s.LanguageCode == language
                        && s.Heritage!.DeletedAt == null && s.Heritage.Status == ContentStatus.PUBLISHED
                        && db.HeritageSummaries.Any(v => v.HeritageId == s.HeritageId && v.LanguageCode == LanguageCode.vi && v.Status == TranslationStatus.APPROVED))
            .OrderBy(s => s.HeritageId)
            .Select(s => new { s.HeritageId, s.Heritage!.Slug, s.Heritage.Name, s.Status, s.ShortText })
            .ToListAsync(ct);
        return candidates
            .Where(s => ShownLanguage(language, s.Status) == language)
            .Select(s => new ShownSummary(s.HeritageId, s.Slug ?? string.Empty, s.Name ?? string.Empty, s.ShortText))
            .ToList();
    }

    /// <summary>Thuyết minh tạo sẵn của bản tóm tắt đang trả; null khi chưa có bản cho ngôn ngữ đó hoặc băm không còn khớp chữ (tóm tắt đã sửa hoặc dịch lại).</summary>
    private async Task<PublicNarrationDto?> NarrationOfAsync(long heritageId, LanguageCode language, string shortText, CancellationToken ct)
    {
        var row = await db.HeritageNarrations.AsNoTracking()
            .Where(n => n.HeritageId == heritageId && n.LanguageCode == language)
            .Select(n => new { n.TextHash, n.AudioUrl, n.DurationMs, n.Voice, n.Sentences })
            .SingleOrDefaultAsync(ct);
        if (row is null || row.TextHash != NarrationRules.TextHash(shortText)) return null;
        var sentences = JsonSerializer.Deserialize<List<NarrationSentenceDto>>(row.Sentences, Json) ?? [];
        return new PublicNarrationDto(row.AudioUrl, row.DurationMs, row.Voice, sentences);
    }

    /// <summary>Danh sách năm ngôn ngữ cho bộ chọn ngôn ngữ (mã, tên viết bằng chính ngôn ngữ đó, mã giọng đọc).</summary>
    public static IReadOnlyList<LanguageOptionDto> ListLanguages() => Languages.All
        .Select(l => new LanguageOptionDto(l.Tag, l.NativeName, l.SpeechTag, l.Code == LanguageCode.vi, Languages.MachineOnly.Contains(l.Code)))
        .ToList();
}
