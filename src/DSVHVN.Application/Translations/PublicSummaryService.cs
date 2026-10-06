using DSVHVN.Application.Common;
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
/// (nghe bản lùi thì đọc bằng giọng tiếng Việt). Bản "cần dịch lại" vẫn hiện bản cũ.</item>
/// <item>Tên di sản theo ngôn ngữ yêu cầu như trang chi tiết.</item>
/// </list>
/// </summary>
public sealed class PublicSummaryService(IAppDbContext db, HeritageLocalizer localizer)
{
    public async Task<PublicSummaryDto> GetAsync(string? slug, LanguageCode language, CancellationToken ct)
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

        var chosen = language == LanguageCode.vi
            ? vietnamese
            : rows.SingleOrDefault(s => s.LanguageCode == language && Languages.IsVisible(s.Status));
        var fallback = chosen is null;
        chosen ??= vietnamese;

        var names = await localizer.LoadVisibleAsync([heritage.Id], language, ct);
        var localization = new LocalizationDto(
            Languages.Tag(language), Languages.Tag(chosen.LanguageCode), fallback, chosen.Status == TranslationStatus.MACHINE);
        return new PublicSummaryDto(heritage.Id, heritage.Slug!, names.TryGetValue(heritage.Id, out var translated) ? translated.Name : heritage.Name!,
            localization, chosen.ShortText, chosen.DetailedText);
    }

    /// <summary>Danh sách năm ngôn ngữ cho bộ chọn ngôn ngữ (mã, tên viết bằng chính ngôn ngữ đó, mã giọng đọc).</summary>
    public static IReadOnlyList<LanguageOptionDto> ListLanguages() => Languages.All
        .Select(l => new LanguageOptionDto(l.Tag, l.NativeName, l.SpeechTag, l.Code == LanguageCode.vi, Languages.MachineOnly.Contains(l.Code)))
        .ToList();
}
