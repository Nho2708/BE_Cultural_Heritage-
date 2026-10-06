using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Statistics;

/// <summary>Các phép tính nhỏ dùng chung của thống kê: mức thành thạo, dòng theo chủ đề, nhãn trạng thái và phổ điểm.</summary>
internal static class StatsMath
{
    public const int BinCount = 10;

    public static MasteryRowDto Mastery(long? id, string? name, int answered, int correct)
    {
        var level = AttemptRules.MasteryOf(correct, answered);
        return new MasteryRowDto(id, name, answered, correct, AttemptRules.RatePercent(correct, answered), level, AttemptRules.MasteryLabel(level), AttemptRules.MasteryStars(level));
    }

    public static TopicStatDto Topic(long? heritageId, QuestionType? type, string name, int answered, int correct)
    {
        var level = AttemptRules.MasteryOf(correct, answered);
        var rate = AttemptRules.RatePercent(correct, answered);
        return new TopicStatDto(heritageId, type, name, answered, correct, rate, level, AttemptRules.MasteryLabel(level),
            answered > 0 && rate < AttemptRules.MidRatio * 100m);
    }

    public static string StudentStatusLabel(QuizStudentStatus status) => status switch
    {
        QuizStudentStatus.SUBMITTED => "Đã nộp",
        QuizStudentStatus.EXPIRED => "Hết giờ, tự nộp",
        QuizStudentStatus.IN_PROGRESS => "Đang làm",
        _ => "Chưa làm",
    };

    public static QuizStudentStatus StatusOf(AttemptStatus? status) => status switch
    {
        AttemptStatus.SUBMITTED => QuizStudentStatus.SUBMITTED,
        AttemptStatus.EXPIRED => QuizStudentStatus.EXPIRED,
        AttemptStatus.IN_PROGRESS => QuizStudentStatus.IN_PROGRESS,
        _ => QuizStudentStatus.NOT_STARTED,
    };

    /// <summary>Phổ điểm 10 khoảng theo phần trăm tổng điểm; 100% thuộc khoảng cuối.</summary>
    public static List<ScoreBinDto> Distribution(IEnumerable<decimal> percents)
    {
        var counts = new int[BinCount];
        foreach (var p in percents) counts[Math.Clamp((int)Math.Floor(p / 10m), 0, BinCount - 1)]++;
        return Enumerable.Range(0, BinCount).Select(i => new ScoreBinDto(i * 10, (i + 1) * 10, counts[i])).ToList();
    }

    public static decimal? Round2(decimal? value) => value is { } v ? Math.Round(v, 2, MidpointRounding.AwayFromZero) : null;
}
