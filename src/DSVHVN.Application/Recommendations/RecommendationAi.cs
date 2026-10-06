using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.Extensions.Logging;

namespace DSVHVN.Application.Recommendations;

// Hợp đồng gọi Dịch vụ AI để xếp hạng di sản liên quan (content-based, không gọi mô hình ngôn ngữ, không tính lượt AI, không ghi gì vào CSDL).
// Dữ liệu gửi đi chỉ gồm id học sinh dạng số nội bộ, cấp học, mức khó mục tiêu, id và thuộc tính của di sản tham chiếu và di sản ứng viên;
// không có họ tên, mã học sinh, tên lớp.

/// <summary>Một di sản tham chiếu kèm trọng số (di sản em còn yếu nặng hơn di sản em đã học).</summary>
public sealed record WeightedHeritage(HeritageFeatures Heritage, double Weight);

/// <summary>Yêu cầu xếp hạng: các di sản tham chiếu và các di sản ứng viên. Cho lớp thì <see cref="StudentId"/> và <see cref="Level"/> là null.</summary>
public sealed record RelatedRequest(
    long? StudentId,
    int? Level,
    DifficultyLevel? TargetDifficulty,
    IReadOnlyList<WeightedHeritage> References,
    IReadOnlyList<HeritageFeatures> Candidates);

/// <summary>Điểm tương đồng của một di sản ứng viên và lý do ngắn. Di sản không có điểm chung nào thì không có trong kết quả.</summary>
public sealed record RelatedHeritage(long HeritageId, double Score, string Reason);

/// <summary>Cổng gọi Dịch vụ AI xếp hạng di sản liên quan. Người gọi đặt thời gian chờ qua <see cref="CancellationToken"/>.</summary>
public interface IRecommendationAiClient
{
    Task<IReadOnlyList<RelatedHeritage>> RankRelatedAsync(RelatedRequest request, CancellationToken ct);
}

/// <summary>Kết quả xếp hạng theo id di sản; <see cref="UsedFallback"/> cho biết máy chủ đã tự tính thay Dịch vụ AI.</summary>
public sealed record RankedRelated(IReadOnlyDictionary<long, RelatedHeritage> Scores, bool UsedFallback)
{
    public static readonly RankedRelated Empty = new(new Dictionary<long, RelatedHeritage>(), false);

    public RelatedHeritage? Of(long heritageId) => Scores.GetValueOrDefault(heritageId);
}

/// <summary>
/// Gọi Dịch vụ AI xếp hạng di sản liên quan với thời gian chờ 2 giây; AI lỗi hoặc quá giờ thì máy chủ tự tính phương án dự phòng
/// (di sản cùng tỉnh hoặc cùng loại với di sản tham chiếu). Hủy từ người gọi (ngắt kết nối) vẫn được báo lên.
/// </summary>
public sealed class RelatedRanker(IRecommendationAiClient client, ILogger<RelatedRanker> logger)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<RankedRelated> RankAsync(RelatedRequest request, CancellationToken ct)
    {
        if (request.References.Count == 0 || request.Candidates.Count == 0) return RankedRelated.Empty;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var ranked = await client.RankRelatedAsync(request, timeout.Token);
            var candidates = request.Candidates.Select(c => c.Id).ToHashSet();
            var scores = ranked.Where(r => candidates.Contains(r.HeritageId))
                .GroupBy(r => r.HeritageId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Score).First());
            return new RankedRelated(scores, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Dịch vụ AI xếp hạng di sản liên quan lỗi hoặc quá giờ, máy chủ tự tính phương án dự phòng.");
            return Fallback(request);
        }
    }

    /// <summary>Phương án dự phòng: cùng tỉnh hoặc cùng loại với di sản tham chiếu.</summary>
    public static RankedRelated Fallback(RelatedRequest request)
    {
        var references = request.References.Select(r => (r.Heritage, r.Weight)).ToList();
        var scores = new Dictionary<long, RelatedHeritage>();
        foreach (var candidate in request.Candidates)
        {
            var match = HeritageSimilarity.BestSameProvinceOrType(candidate, references);
            if (match is not null) scores[candidate.Id] = new RelatedHeritage(candidate.Id, match.Score, HeritageSimilarity.Reason(match));
        }
        return new RankedRelated(scores, true);
    }

    /// <summary>Tính cục bộ bằng bộ độ tương đồng đầy đủ (cùng bộ với bản giả lập của Dịch vụ AI).</summary>
    public static IReadOnlyList<RelatedHeritage> Compute(RelatedRequest request)
    {
        var references = request.References.Select(r => (r.Heritage, r.Weight)).ToList();
        var result = new List<RelatedHeritage>();
        foreach (var candidate in request.Candidates)
        {
            var match = HeritageSimilarity.Best(candidate, references);
            if (match is not null) result.Add(new RelatedHeritage(candidate.Id, match.Score, HeritageSimilarity.Reason(match)));
        }
        return result.OrderByDescending(r => r.Score).ThenBy(r => r.HeritageId).ToList();
    }
}
