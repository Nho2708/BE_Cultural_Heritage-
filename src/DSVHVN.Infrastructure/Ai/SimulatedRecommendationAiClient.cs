using DSVHVN.Application.Recommendations;

namespace DSVHVN.Infrastructure.Ai;

/// <summary>
/// Bản giả lập của Dịch vụ AI xếp hạng di sản liên quan (<c>Ai:Mode = Simulated</c>, mặc định): không gọi mạng, tính ngay bằng bộ độ tương đồng
/// content-based của nền tảng (cùng loại, cùng tỉnh hoặc vùng, cùng cấp xếp hạng, khoảng năm giao nhau, chữ giống nhau).
/// </summary>
public sealed class SimulatedRecommendationAiClient : IRecommendationAiClient
{
    public Task<IReadOnlyList<RelatedHeritage>> RankRelatedAsync(RelatedRequest request, CancellationToken ct) =>
        Task.FromResult(RelatedRanker.Compute(request));
}
