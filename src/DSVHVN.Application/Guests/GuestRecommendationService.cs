using DSVHVN.Application.Common;
using DSVHVN.Application.Discovery;
using DSVHVN.Application.Recommendations;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Guests;

/// <summary>
/// "Gợi ý cho bạn" và "Gần bạn" của khách tham quan. Cả hai tính khi hiển thị, không lưu kết quả, không thêm bảng.
/// <list type="bullet">
/// <item><b>Gợi ý cho bạn</b>: tối đa 6 di sản đã xuất bản, content-based theo lịch sử của mã thiết bị trong 90 ngày, không gọi mô hình ngôn ngữ, không ghi <c>ai_generations</c>,
/// không tính lượt AI. Di sản khách quan tâm là di sản đã xem chi tiết, đọc tóm tắt, nghe hoặc xem 3D (từ khóa tìm không thành sở thích), chỉ xét 20 lượt gần nhất,
/// lượt đọc tóm tắt, nghe và xem 3D nặng gấp đôi lượt xem chi tiết. Ứng viên xếp theo độ tương đồng (cùng bộ tính với "di sản liên quan" của học sinh) qua <see cref="RelatedRanker"/>;
/// bỏ di sản khách đã xem chi tiết từ 3 lần trở lên và chính các di sản đang làm sở thích. Dịch vụ AI lỗi thì tự chọn di sản cùng loại hoặc cùng vùng với di sản xem gần nhất.
/// Chưa có lịch sử (hoặc vừa xóa hết) thì hiện di sản nổi bật như trang chủ. Dịch vụ AI chỉ nhận id và thuộc tính của di sản kèm trọng số: không có mã thiết bị, ngôn ngữ, vị trí hay từ khóa tìm.</item>
/// <item><b>Gần bạn</b>: tối đa 6 di sản đã xuất bản có tọa độ, gần vị trí khách nhất theo công thức khoảng cách trên mặt cầu. Tọa độ làm tròn 3 chữ số thập phân, chỉ dùng để tính trong bộ nhớ:
/// không lưu, không ghi vào lịch sử, không gửi sang Dịch vụ AI hay bên thứ ba. Vị trí ngoài khung tọa độ Việt Nam (nới 1 độ mỗi phía) thì không hiện danh sách.</item>
/// </list>
/// </summary>
public sealed class GuestRecommendationService(
    IAppDbContext db, DiscoveryService discovery, HeritageLocalizer localizer, RelatedRanker ranker, TimeProvider clock)
{
    private sealed record HistoryRow(GuestActivityType Type, long HeritageId);

    // ---- Gợi ý cho bạn ----

    /// <param name="deviceKey">Mã thiết bị đã kiểm định dạng; null (không gửi mã) hoặc mã chưa từng thấy thì như chưa có lịch sử.</param>
    public async Task<GuestRecommendationsDto> RecommendAsync(string? deviceKey, LanguageCode language, CancellationToken ct)
    {
        var history = deviceKey is null ? [] : await LoadHistoryAsync(deviceKey, ct);
        var interest = history.Take(GuestRules.InterestWindow)
            .GroupBy(h => h.HeritageId)
            .Select(g => (HeritageId: g.Key, Weight: g.Sum(h => GuestRules.Weight(h.Type))))
            .ToList();
        if (interest.Count == 0) return await FeaturedAsync(language, hasHistory: false, ct);

        var catalog = await HeritageCatalog.LoadAsync(db, ct);
        var references = interest
            .Select(i => (Features: catalog.Features(i.HeritageId), i.Weight))
            .Where(x => x.Features is not null)
            .Select(x => new WeightedHeritage(x.Features!, x.Weight))
            .ToList();
        if (references.Count == 0) return await FeaturedAsync(language, hasHistory: true, ct);

        // Bỏ di sản đã xem chi tiết từ 3 lần trở lên trong thời hạn lưu và chính các di sản đang làm sở thích.
        var excluded = history.Where(h => h.Type == GuestActivityType.VIEW)
            .GroupBy(h => h.HeritageId).Where(g => g.Count() >= GuestRules.ExcludeAfterViews).Select(g => g.Key)
            .Concat(references.Select(r => r.Heritage.Id))
            .ToHashSet();
        var candidates = catalog.Published.Where(h => !excluded.Contains(h.Id)).ToList();

        var ranked = await ranker.RankAsync(new RelatedRequest(null, null, null, references, candidates), ct);
        var weighted = references.Select(r => (r.Heritage, r.Weight)).ToList();

        List<(HeritageFeatures Candidate, IReadOnlyList<SimilarityFactor> Reasons, long? BecauseOf)> picked;
        if (ranked.UsedFallback)
        {
            // Dịch vụ AI lỗi: cùng loại hoặc cùng vùng với di sản xem gần nhất.
            var latest = catalog.Features(history[0].HeritageId);
            picked = latest is null
                ? []
                : candidates
                    .Select(c => (Candidate: c, Score: GuestRules.FallbackScore(c, latest)))
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score).ThenBy(x => x.Candidate.Id)
                    .Take(GuestRules.ResultLimit)
                    .Select(x => (x.Candidate, (IReadOnlyList<SimilarityFactor>)FallbackReasons(x.Candidate, latest), (long?)latest.Id))
                    .ToList();
        }
        else
        {
            picked = candidates
                .Where(c => ranked.Of(c.Id) is not null)
                .Select(c => (Candidate: c, Score: ranked.Of(c.Id)!.Score))
                .OrderByDescending(x => x.Score).ThenBy(x => x.Candidate.Id)
                .Take(GuestRules.ResultLimit)
                .Select(x =>
                {
                    var match = HeritageSimilarity.Best(x.Candidate, weighted);
                    return (x.Candidate, (IReadOnlyList<SimilarityFactor>)(match?.Factors ?? []), match?.ReferenceId);
                })
                .ToList();
        }
        if (picked.Count == 0) return await FeaturedAsync(language, hasHistory: true, ct);

        var cards = (await discovery.GetCardsAsync(picked.Select(p => p.Candidate.Id).ToList(), language, ct)).ToDictionary(c => c.Id);
        var becauseIds = picked.Where(p => p.BecauseOf is not null).Select(p => p.BecauseOf!.Value).Distinct().ToList();
        var translated = await localizer.LoadVisibleAsync(becauseIds, language, ct);
        var items = picked
            .Where(p => cards.ContainsKey(p.Candidate.Id))
            .Select(p => new GuestRecommendationDto(
                cards[p.Candidate.Id], p.Reasons, p.BecauseOf,
                p.BecauseOf is { } id ? translated.TryGetValue(id, out var row) ? row.Name : catalog.Features(id)?.Name : null))
            .ToList();
        return items.Count == 0
            ? await FeaturedAsync(language, hasHistory: true, ct)
            : new GuestRecommendationsDto(items, true, ranked.UsedFallback, null);
    }

    private static List<SimilarityFactor> FallbackReasons(HeritageFeatures candidate, HeritageFeatures reference)
    {
        var reasons = new List<SimilarityFactor>();
        if (candidate.Type is not null && candidate.Type == reference.Type) reasons.Add(SimilarityFactor.SAME_TYPE);
        if (!string.IsNullOrEmpty(candidate.Region) && candidate.Region == reference.Region) reasons.Add(SimilarityFactor.SAME_REGION);
        return reasons;
    }

    /// <summary>Lượt gắn với di sản trong thời hạn lưu của thiết bị, mới nhất trước (tối đa 500 lượt); mã chưa từng thấy thì rỗng.</summary>
    private async Task<List<HistoryRow>> LoadHistoryAsync(string deviceKey, CancellationToken ct)
    {
        var cutoff = GuestRules.RetentionCutoff(clock.GetUtcNow().UtcDateTime);
        return await db.GuestActivities.AsNoTracking()
            .Where(a => a.GuestDevice!.DeviceKey == deviceKey && a.CreatedAt >= cutoff && a.HeritageId != null)
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
            .Take(GuestRules.MaxActivitiesPerDevice)
            .Select(a => new HistoryRow(a.ActivityType, a.HeritageId!.Value))
            .ToListAsync(ct);
    }

    /// <summary>Khách chưa có lịch sử, vừa xóa hết hoặc chưa tìm được di sản giống: di sản nổi bật như trang chủ (tối đa 6), kèm lời mời xem thử khi chưa có lịch sử.</summary>
    private async Task<GuestRecommendationsDto> FeaturedAsync(LanguageCode language, bool hasHistory, CancellationToken ct)
    {
        var featured = await discovery.GetFeaturedAsync(GuestRules.ResultLimit, language, ct);
        return new GuestRecommendationsDto(
            featured.Select(h => new GuestRecommendationDto(h, [], null, null)).ToList(),
            false, false, hasHistory ? null : GuestMessages.NoHistoryYet);
    }

    // ---- Gần bạn ----

    /// <summary>
    /// Tối đa 6 di sản đã xuất bản có tọa độ, gần nhất trước (bằng nhau thì theo id). Tọa độ ngoài phạm vi địa lý hợp lệ thì 400; ngoài khung Việt Nam đã nới thì danh sách rỗng
    /// kèm lý do <c>OUTSIDE_VIETNAM</c>.
    /// </summary>
    public async Task<NearbyDto> NearbyAsync(NearbyRequest request, LanguageCode language, CancellationToken ct)
    {
        if (request.Latitude is not { } rawLatitude) throw AppException.Validation("latitude", Messages.Required("vĩ độ"));
        if (request.Longitude is not { } rawLongitude) throw AppException.Validation("longitude", Messages.Required("kinh độ"));
        if (!GuestRules.IsValidCoordinate(rawLatitude, rawLongitude))
        {
            var field = double.IsFinite(rawLatitude) && rawLatitude is >= -90 and <= 90 ? "longitude" : "latitude";
            throw AppException.Validation(field, Messages.Invalid(field == "latitude" ? "Vĩ độ" : "Kinh độ",
                field == "latitude" ? "phải từ -90 đến 90" : "phải từ -180 đến 180"));
        }

        var latitude = GuestRules.RoundCoordinate(rawLatitude);
        var longitude = GuestRules.RoundCoordinate(rawLongitude);
        if (!GuestRules.IsNearVietnam(latitude, longitude))
            return new NearbyDto([], NearbyUnavailable.OutsideVietnam, GuestMessages.NearbyOutsideVietnam);

        var located = await db.Heritages.AsNoTracking()
            .Where(h => h.DeletedAt == null && h.Status == ContentStatus.PUBLISHED && h.Latitude != null && h.Longitude != null)
            .Select(h => new { h.Id, Latitude = (double)h.Latitude!.Value, Longitude = (double)h.Longitude!.Value })
            .ToListAsync(ct);
        var nearest = located
            .Select(h => (h.Id, Meters: GuestRules.DistanceMeters(latitude, longitude, h.Latitude, h.Longitude)))
            .OrderBy(x => x.Meters).ThenBy(x => x.Id)
            .Take(GuestRules.ResultLimit)
            .ToList();

        var cards = (await discovery.GetCardsAsync(nearest.Select(x => x.Id).ToList(), language, ct)).ToDictionary(c => c.Id);
        var items = nearest
            .Where(x => cards.ContainsKey(x.Id))
            .Select(x => new NearbyHeritageDto(cards[x.Id], (int)Math.Round(x.Meters, MidpointRounding.AwayFromZero), GuestRules.FormatDistance(x.Meters, language)))
            .ToList();
        return new NearbyDto(items, null, null);
    }
}
