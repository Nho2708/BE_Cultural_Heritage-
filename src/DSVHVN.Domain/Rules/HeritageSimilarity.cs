using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Rules;

/// <summary>
/// Thuộc tính của một di sản dùng để so độ tương đồng (content-based): loại, tỉnh, vùng, cấp xếp hạng, khoảng năm của dòng thời gian và chữ
/// (tên cùng mô tả ngắn). Không có thông tin của học sinh nào.
/// </summary>
public sealed record HeritageFeatures(
    long Id,
    string? Name,
    HeritageType? Type,
    int? ProvinceId,
    string? Region,
    RecognitionLevel? Level,
    int? YearFrom,
    int? YearTo,
    string? Text);

/// <summary>Điểm của một di sản ứng viên so với các di sản tham chiếu: điểm, các điểm chung và di sản tham chiếu giống nhất.</summary>
public sealed record SimilarityMatch(double Score, IReadOnlyList<SimilarityFactor> Factors, long ReferenceId, string? ReferenceName);

/// <summary>Điểm chung giữa hai di sản.</summary>
public enum SimilarityFactor
{
    SAME_TYPE,
    SAME_PROVINCE,
    SAME_REGION,
    SAME_LEVEL,
    OVERLAPPING_ERA,
    SIMILAR_TEXT,
}

/// <summary>
/// Độ tương đồng giữa hai di sản cho phần "di sản liên quan" và "bài học mới nên học": cùng loại, cùng tỉnh (hoặc cùng vùng), cùng cấp xếp hạng,
/// khoảng năm của dòng thời gian giao nhau, và độ giống nhau của chữ trong tên và mô tả. Đổi trọng số ở đây là đổi quy tắc nghiệp vụ.
/// Cùng một bộ tính cho học sinh, cho giáo viên và (sau này) cho khách tham quan.
/// </summary>
public static class HeritageSimilarity
{
    public const double TypeWeight = 3d;
    public const double ProvinceWeight = 2d;
    public const double RegionWeight = 1d;
    public const double LevelWeight = 1d;
    public const double EraWeight = 1d;
    public const double TextWeight = 2d;

    /// <summary>Từ độ giống chữ (Jaccard) này trở lên mới tính là điểm chung.</summary>
    public const double TextThreshold = 0.12d;

    private static readonly HashSet<string> StopWords =
    [
        "cua", "va", "cac", "mot", "nhung", "trong", "duoc", "la", "co", "cho", "voi", "tai", "nay", "khi", "den", "tu", "da", "the", "nam",
    ];

    /// <summary>Điểm giữa hai di sản (đối xứng) và các điểm chung.</summary>
    public static (double Score, List<SimilarityFactor> Factors) Compare(HeritageFeatures a, HeritageFeatures b)
    {
        var score = 0d;
        var factors = new List<SimilarityFactor>();

        if (a.Type is not null && a.Type == b.Type)
        {
            score += TypeWeight;
            factors.Add(SimilarityFactor.SAME_TYPE);
        }
        if (a.ProvinceId is not null && a.ProvinceId == b.ProvinceId)
        {
            score += ProvinceWeight;
            factors.Add(SimilarityFactor.SAME_PROVINCE);
        }
        else if (!string.IsNullOrEmpty(a.Region) && a.Region == b.Region)
        {
            score += RegionWeight;
            factors.Add(SimilarityFactor.SAME_REGION);
        }
        if (a.Level is not null && a.Level != RecognitionLevel.NONE && a.Level == b.Level)
        {
            score += LevelWeight;
            factors.Add(SimilarityFactor.SAME_LEVEL);
        }
        if (EraOverlaps(a, b))
        {
            score += EraWeight;
            factors.Add(SimilarityFactor.OVERLAPPING_ERA);
        }
        var text = TextSimilarity(a.Text, b.Text);
        if (text >= TextThreshold)
        {
            score += TextWeight * Math.Min(1d, text * 2d);
            factors.Add(SimilarityFactor.SIMILAR_TEXT);
        }
        return (score, factors);
    }

    /// <summary>
    /// Điểm tốt nhất của một ứng viên so với các di sản tham chiếu (mỗi di sản có trọng số): điểm giữa hai di sản nhân trọng số, lấy lớn nhất.
    /// Null nếu không có điểm chung nào.
    /// </summary>
    public static SimilarityMatch? Best(HeritageFeatures candidate, IReadOnlyList<(HeritageFeatures Heritage, double Weight)> references)
    {
        SimilarityMatch? best = null;
        foreach (var (reference, weight) in references)
        {
            var (score, factors) = Compare(candidate, reference);
            if (factors.Count == 0) continue;
            var weighted = score * weight;
            if (best is null || weighted > best.Score) best = new SimilarityMatch(weighted, factors, reference.Id, reference.Name);
        }
        return best;
    }

    /// <summary>
    /// Phương án dự phòng khi Dịch vụ AI lỗi: chỉ xét cùng tỉnh hoặc cùng loại (tỉnh nặng hơn), không so chữ.
    /// Null nếu không cùng tỉnh cũng không cùng loại với di sản tham chiếu nào.
    /// </summary>
    public static SimilarityMatch? BestSameProvinceOrType(HeritageFeatures candidate, IReadOnlyList<(HeritageFeatures Heritage, double Weight)> references)
    {
        SimilarityMatch? best = null;
        foreach (var (reference, weight) in references)
        {
            var factors = new List<SimilarityFactor>();
            var score = 0d;
            if (candidate.ProvinceId is not null && candidate.ProvinceId == reference.ProvinceId)
            {
                score += ProvinceWeight;
                factors.Add(SimilarityFactor.SAME_PROVINCE);
            }
            if (candidate.Type is not null && candidate.Type == reference.Type)
            {
                score += 1d;
                factors.Add(SimilarityFactor.SAME_TYPE);
            }
            if (factors.Count == 0) continue;
            var weighted = score * weight;
            if (best is null || weighted > best.Score) best = new SimilarityMatch(weighted, factors, reference.Id, reference.Name);
        }
        return best;
    }

    /// <summary>Câu lý do ngắn: "Cùng loại, cùng tỉnh với {tên}", tối đa hai điểm chung.</summary>
    public static string Reason(SimilarityMatch match)
    {
        var parts = match.Factors.Take(2).Select(FactorText).ToList();
        var text = string.Join(", ", parts);
        var first = text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
        return string.IsNullOrWhiteSpace(match.ReferenceName) ? first : $"{first} với {match.ReferenceName}";
    }

    private static string FactorText(SimilarityFactor factor) => factor switch
    {
        SimilarityFactor.SAME_TYPE => "cùng loại",
        SimilarityFactor.SAME_PROVINCE => "cùng tỉnh",
        SimilarityFactor.SAME_REGION => "cùng vùng",
        SimilarityFactor.SAME_LEVEL => "cùng cấp xếp hạng",
        SimilarityFactor.OVERLAPPING_ERA => "cùng thời kỳ",
        _ => "nội dung gần nhau",
    };

    public static bool EraOverlaps(HeritageFeatures a, HeritageFeatures b)
    {
        if (a.YearFrom is not { } af || b.YearFrom is not { } bf) return false;
        var at = a.YearTo ?? af;
        var bt = b.YearTo ?? bf;
        return af <= bt && bf <= at;
    }

    /// <summary>Độ giống nhau của chữ: số từ chung chia số từ của hợp hai bên (Jaccard), sau khi bỏ dấu, bỏ từ quá ngắn và từ nối thường gặp.</summary>
    public static double TextSimilarity(string? left, string? right)
    {
        var a = Tokens(left);
        var b = Tokens(right);
        if (a.Count == 0 || b.Count == 0) return 0d;
        var common = a.Count(b.Contains);
        return common == 0 ? 0d : (double)common / (a.Count + b.Count - common);
    }

    private static HashSet<string> Tokens(string? text) =>
        SearchText.Fold(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3 && !StopWords.Contains(t)).ToHashSet(StringComparer.Ordinal);
}
