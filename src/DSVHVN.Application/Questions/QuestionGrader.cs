using System.Text;
using System.Text.RegularExpressions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Questions;

namespace DSVHVN.Application.Questions;

/// <summary>Kết quả chấm một câu: từng phần đúng hay sai. Điểm của câu = điểm tối đa × số phần đúng ÷ tổng số phần.</summary>
public sealed record GradeResult(IReadOnlyList<bool> Parts)
{
    public int PartsTotal => Parts.Count;

    public int PartsCorrect => Parts.Count(p => p);

    /// <summary>Đúng hết mọi phần (câu không có phần nào thì không bao giờ đúng).</summary>
    public bool IsCorrect => PartsTotal > 0 && PartsCorrect == PartsTotal;

    public decimal Fraction => PartsTotal == 0 ? 0m : (decimal)PartsCorrect / PartsTotal;

    public GradeResultDto ToDto() => new(PartsCorrect, PartsTotal, Parts, IsCorrect, Fraction);
}

/// <summary>
/// Chấm câu hỏi theo từng dạng, chạy hoàn toàn từ các dòng <c>answers</c> đã lưu (quy tắc Cách chấm từng dạng câu): không trừ điểm khi sai,
/// câu bỏ trống 0 điểm, điểm một câu tính một lần cho cả câu. Dùng để chấm thử khi soạn câu, để kiểm hợp lệ (đáp án chuẩn phải đúng đủ)
/// và là phép chấm mà luồng làm bài dùng lại.
/// <list type="bullet">
/// <item>Một đáp án đúng, Đúng/Sai, chọn hình đúng, đọc tư liệu: một phần.</item>
/// <item>Ghép cặp: mỗi cặp nối đúng là một phần. Sắp xếp: mỗi mục ở đúng vị trí tuyệt đối là một phần; chưa sắp xếp lần nào thì bỏ trống.</item>
/// <item>Điền từ: mỗi chỗ trống là một phần; khớp một cách viết đúng sau khi bỏ khoảng trắng đầu cuối, gộp khoảng trắng liên tiếp,
/// không phân biệt hoa thường nhưng phân biệt dấu.</item>
/// <item>Nhiều mệnh đề Đúng/Sai: mỗi mệnh đề chọn đúng là một phần; mệnh đề chưa chọn không được.</item>
/// </list>
/// </summary>
public static class QuestionGrader
{
    public static GradeResult Grade(QuestionType type, IEnumerable<Answer> answers, QuestionSubmission? submission)
    {
        var rows = Ordered(answers);
        switch (type)
        {
            case QuestionType.SINGLE_CHOICE:
            case QuestionType.TRUE_FALSE:
            case QuestionType.IMAGE_CHOICE:
            case QuestionType.SOURCE_READING:
            {
                var i = submission?.SelectedIndex;
                return new GradeResult([i is { } k && k >= 0 && k < rows.Count && rows[k].IsCorrect]);
            }

            case QuestionType.MATCHING:
            {
                var parts = new bool[rows.Count];
                var chosen = submission?.Matches;
                var used = new HashSet<int>();
                for (var left = 0; left < parts.Length; left++)
                {
                    // Mỗi vế phải chỉ nối một lần: cặp nối trùng vế phải đã dùng không được tính.
                    if (chosen is not null && left < chosen.Count && chosen[left] is { } right && used.Add(right))
                        parts[left] = right == left;
                }
                return new GradeResult(parts);
            }

            case QuestionType.ORDERING:
            {
                var parts = new bool[rows.Count];
                var order = submission?.Order;
                if (order is { Count: > 0 })
                {
                    var placed = new HashSet<int>();
                    for (var position = 0; position < order.Count; position++)
                    {
                        var item = order[position];
                        if (item < 0 || item >= rows.Count || !placed.Add(item)) continue;
                        parts[item] = rows[item].CorrectPosition == position + 1;
                    }
                }
                return new GradeResult(parts);
            }

            case QuestionType.FILL_BLANK:
            {
                var blanks = BlankCount(rows);
                var parts = new bool[blanks];
                var typed = submission?.Blanks;
                for (var b = 0; b < blanks; b++)
                {
                    var text = typed is not null && b < typed.Count ? typed[b] : null;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var normalized = NormalizeText(text);
                    parts[b] = rows.Any(r => r.BlankIndex == b + 1 && r.IsCorrect
                                             && string.Equals(NormalizeText(r.Content), normalized, StringComparison.OrdinalIgnoreCase));
                }
                return new GradeResult(parts);
            }

            case QuestionType.MULTI_TRUE_FALSE:
            {
                var parts = new bool[rows.Count];
                var chosen = submission?.Statements;
                for (var i = 0; i < parts.Length; i++)
                    parts[i] = chosen is not null && i < chosen.Count && chosen[i] is { } value && value == rows[i].IsCorrect;
                return new GradeResult(parts);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }

    /// <summary>Điểm của câu: điểm tối đa × số phần đúng ÷ tổng số phần, làm tròn 2 chữ số thập phân (nửa lên).</summary>
    public static decimal Score(decimal maxPoint, GradeResult result) =>
        result.PartsTotal == 0 ? 0m : Math.Round(maxPoint * result.PartsCorrect / result.PartsTotal, 2, MidpointRounding.AwayFromZero);

    /// <summary>Cách làm đúng hết của câu, dựng từ các dòng đáp án (đáp án chuẩn).</summary>
    public static QuestionSubmission AnswerKey(QuestionType type, IEnumerable<Answer> answers)
    {
        var rows = Ordered(answers);
        switch (type)
        {
            case QuestionType.MATCHING:
                return new QuestionSubmission(Matches: Enumerable.Range(0, rows.Count).Select(i => (int?)i).ToList());
            case QuestionType.ORDERING:
                return new QuestionSubmission(Order: Enumerable.Range(0, rows.Count)
                    .OrderBy(i => rows[i].CorrectPosition ?? int.MaxValue).ToList());
            case QuestionType.FILL_BLANK:
                return new QuestionSubmission(Blanks: Enumerable.Range(1, BlankCount(rows))
                    .Select(b => rows.FirstOrDefault(r => r.BlankIndex == b && r.IsCorrect)?.Content).ToList());
            case QuestionType.MULTI_TRUE_FALSE:
                return new QuestionSubmission(Statements: rows.Select(r => (bool?)r.IsCorrect).ToList());
            default:
                return new QuestionSubmission(SelectedIndex: rows.FindIndex(r => r.IsCorrect));
        }
    }

    /// <summary>
    /// Chấm thử để kiểm câu hợp lệ: câu phải có ít nhất một phần, đáp án chuẩn phải đúng đủ mọi phần, và câu bỏ trống phải 0 phần đúng.
    /// Trả mô tả lỗi, hoặc null nếu đạt.
    /// </summary>
    public static string? SelfCheck(QuestionType type, IEnumerable<Answer> answers)
    {
        var rows = answers.ToList();
        var key = Grade(type, rows, AnswerKey(type, rows));
        if (key.PartsTotal == 0) return "câu không có phần nào để chấm";
        if (!key.IsCorrect) return "chấm thử bằng đáp án chuẩn không ra đúng hết các phần";
        var blank = Grade(type, rows, null);
        return blank.PartsCorrect != 0 ? "câu bỏ trống vẫn được điểm" : null;
    }

    /// <summary>Số phần để chấm của câu, tính từ cấu trúc đáp án.</summary>
    public static int PartsTotal(QuestionType type, IEnumerable<Answer> answers) =>
        Grade(type, answers, null).PartsTotal;

    /// <summary>Gộp khoảng trắng liên tiếp, bỏ khoảng trắng đầu cuối, đưa về dạng dựng sẵn (Unicode NFC) để chữ có dấu so sánh đúng.</summary>
    public static string NormalizeText(string? value) =>
        Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ").Normalize(NormalizationForm.FormC);

    private static List<Answer> Ordered(IEnumerable<Answer> answers) =>
        answers.OrderBy(a => a.SortOrder ?? int.MaxValue).ThenBy(a => a.Id).ToList();

    private static int BlankCount(List<Answer> rows) =>
        rows.Where(r => r.BlankIndex is not null && r.IsCorrect).Select(r => (int)r.BlankIndex!.Value).DefaultIfEmpty(0).Max();
}
