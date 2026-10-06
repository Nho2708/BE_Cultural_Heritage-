using DSVHVN.Application.Common;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Questions;
using DSVHVN.Domain.Quizzes;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Attempts;

/// <summary>
/// Bộ số ngẫu nhiên tất định (SplitMix64) cho việc xáo trong một lượt làm bài. Tự cài đặt, không dùng <see cref="Random"/> có seed,
/// để thứ tự xáo không đổi giữa các phiên bản .NET: tải lại đề, đổi thiết bị hay vào tiếp lượt đều ra đúng thứ tự cũ.
/// </summary>
internal sealed class SeededRandom
{
    private ulong _state;

    public SeededRandom(long a, long b, int salt)
    {
        unchecked
        {
            _state = (ulong)a * 0x9E3779B97F4A7C15UL ^ ((ulong)b + 0xBF58476D1CE4E5B9UL) * 0x94D049BB133111EBUL ^ (ulong)(salt + 1) * 0xD6E8FEB86659FD93UL;
        }
    }

    private ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    public int Next(int maxExclusive) => (int)(NextUInt64() % (ulong)maxExclusive);

    /// <summary>Hoán vị 0..count-1 (Fisher-Yates).</summary>
    public int[] Permutation(int count)
    {
        var order = Enumerable.Range(0, count).ToArray();
        for (var i = count - 1; i > 0; i--)
        {
            var j = Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        return order;
    }
}

/// <summary>
/// Thứ tự xáo của các thành phần trong một câu của một lượt làm bài (quy tắc Một lượt, hạn nộp và tự nộp): tính lại mỗi lần tải đề từ seed là mã
/// lượt và mã câu, không lưu. Các dạng cần xáo để không lộ đáp án luôn xáo, không phụ thuộc cờ xáo câu: hai cột của ghép cặp, các mục của sắp xếp
/// (không bao giờ trùng thứ tự đúng), kho từ của điền từ, thẻ của chọn hình đúng. Phương án của câu một đáp án đúng, Đúng/Sai, đọc tư liệu và mệnh đề
/// của câu nhiều mệnh đề giữ thứ tự lưu. Mỗi mảng thứ tự cho biết: vị trí đang hiện → chỉ số trong các dòng đáp án đã lưu.
/// </summary>
internal sealed class QuestionLayout
{
    private const int SaltOptions = 1;
    private const int SaltLeft = 2;
    private const int SaltRight = 3;
    private const int SaltItems = 4;
    private const int SaltWords = 5;

    public required Question Question { get; init; }

    public QuestionType Type { get; init; }

    /// <summary>Các dòng đáp án theo thứ tự lưu.</summary>
    public required List<Answer> Rows { get; init; }

    public required QuestionShape Shape { get; init; }

    public required int[] OptionOrder { get; init; }
    public required int[] LeftOrder { get; init; }
    public required int[] RightOrder { get; init; }
    public required int[] ItemOrder { get; init; }

    /// <summary>Kho từ đã xáo (rỗng nếu câu điền từ không có kho từ).</summary>
    public required IReadOnlyList<string> WordBank { get; init; }

    public int BlankCount { get; init; }

    public bool UsesOptions => Type is QuestionType.SINGLE_CHOICE or QuestionType.TRUE_FALSE or QuestionType.IMAGE_CHOICE or QuestionType.SOURCE_READING;

    /// <summary>Câu phải có đủ dòng <see cref="Question.Answers"/>.</summary>
    public static QuestionLayout Build(Question q, long attemptId)
    {
        var type = q.QuestionType ?? QuestionType.SINGLE_CHOICE;
        var rows = q.Answers.OrderBy(a => a.SortOrder ?? int.MaxValue).ThenBy(a => a.Id).ToList();
        var shape = QuestionShape.Of(q);
        var n = rows.Count;

        var option = Identity(n);
        if (type == QuestionType.IMAGE_CHOICE) option = new SeededRandom(attemptId, q.Id, SaltOptions).Permutation(n);

        var left = type == QuestionType.MATCHING ? new SeededRandom(attemptId, q.Id, SaltLeft).Permutation(n) : [];
        var right = type == QuestionType.MATCHING ? new SeededRandom(attemptId, q.Id, SaltRight).Permutation(n) : [];
        var items = type == QuestionType.ORDERING ? ItemPermutation(rows, attemptId, q.Id) : [];

        var bank = shape.WordBank.ToList();
        if (bank.Count > 1)
        {
            var order = new SeededRandom(attemptId, q.Id, SaltWords).Permutation(bank.Count);
            bank = order.Select(i => shape.WordBank[i]).ToList();
        }

        return new QuestionLayout
        {
            Question = q,
            Type = type,
            Rows = rows,
            Shape = shape,
            OptionOrder = type is QuestionType.SINGLE_CHOICE or QuestionType.TRUE_FALSE or QuestionType.IMAGE_CHOICE or QuestionType.SOURCE_READING ? option : [],
            LeftOrder = left,
            RightOrder = right,
            ItemOrder = items,
            WordBank = bank,
            BlankCount = type == QuestionType.FILL_BLANK ? shape.Blanks.Count : 0,
        };
    }

    /// <summary>Thứ tự ban đầu của câu sắp xếp: xáo, không bao giờ trùng thứ tự đúng (khi có từ hai mục trở lên).</summary>
    private static int[] ItemPermutation(List<Answer> rows, long attemptId, long questionId)
    {
        var n = rows.Count;
        if (n < 2) return Identity(n);
        for (var salt = 0; salt < 20; salt++)
        {
            var order = new SeededRandom(attemptId, questionId, SaltItems + salt * 17).Permutation(n);
            if (!IsCorrectOrder(rows, order)) return order;
        }
        var reversed = Identity(n);
        Array.Reverse(reversed);
        return reversed;
    }

    private static bool IsCorrectOrder(List<Answer> rows, int[] order)
    {
        for (var k = 0; k < order.Length; k++)
            if (rows[order[k]].CorrectPosition != k + 1) return false;
        return true;
    }

    private static int[] Identity(int n) => Enumerable.Range(0, n).ToArray();

    /// <summary>Thứ tự các câu của lượt: theo thứ tự trong bài kiểm tra, hoặc hoán vị tất định theo mã lượt khi bài bật xáo câu.</summary>
    public static List<T> OrderQuestions<T>(List<T> inQuizOrder, bool shuffle, long attemptId)
    {
        if (!shuffle || inQuizOrder.Count < 2) return inQuizOrder;
        var order = new SeededRandom(attemptId, 0, 0).Permutation(inQuizOrder.Count);
        return order.Select(i => inQuizOrder[i]).ToList();
    }
}

/// <summary>
/// Đổi câu trả lời giữa cách em thấy (vị trí trên màn hình) và cách lưu (chỉ số theo thứ tự lưu của các dòng đáp án), kiểm hợp lệ câu trả lời với đúng
/// câu đó, ghi các dòng <c>attempt_answers</c> và <c>attempt_answer_parts</c> kèm điểm chấm, và đọc ngược các dòng đã lưu thành câu trả lời.
/// </summary>
internal static class AttemptAnswerCodec
{
    // ---- Em thấy → lưu ----

    /// <summary>
    /// Kiểm câu trả lời của em với câu và đổi sang chỉ số theo thứ tự lưu để chấm. Câu trả lời sai cấu trúc bị từ chối 400:
    /// vị trí ngoài phạm vi, một vế phải nối hai lần, thứ tự sắp xếp không phải hoán vị đầy đủ, chữ quá dài, số mệnh đề vượt số mệnh đề của câu.
    /// Trả null khi em bỏ trống cả câu.
    /// </summary>
    public static QuestionSubmission? ToStored(QuestionLayout l, QuestionSubmission? shown)
    {
        if (shown is null) return null;
        switch (l.Type)
        {
            case QuestionType.SINGLE_CHOICE:
            case QuestionType.TRUE_FALSE:
            case QuestionType.IMAGE_CHOICE:
            case QuestionType.SOURCE_READING:
            {
                if (shown.SelectedIndex is not { } i) return null;
                if (i < 0 || i >= l.OptionOrder.Length) throw Invalid("phương án em chọn không có trong câu hỏi");
                return new QuestionSubmission(SelectedIndex: l.OptionOrder[i]);
            }

            case QuestionType.MATCHING:
            {
                var n = l.Rows.Count;
                var given = shown.Matches ?? [];
                if (given.Count > n) throw Invalid("số cặp nối nhiều hơn số hình của câu");
                var stored = new List<int?>(Enumerable.Repeat<int?>(null, n));
                var used = new HashSet<int>();
                for (var left = 0; left < given.Count; left++)
                {
                    if (given[left] is not { } right) continue;
                    if (right < 0 || right >= n) throw Invalid("vế nối không có trong câu hỏi");
                    if (!used.Add(right)) throw Invalid("mỗi vế chỉ được nối một lần");
                    stored[l.LeftOrder[left]] = l.RightOrder[right];
                }
                return stored.All(x => x is null) ? null : new QuestionSubmission(Matches: stored);
            }

            case QuestionType.ORDERING:
            {
                var order = shown.Order;
                if (order is null || order.Count == 0) return null;
                var n = l.Rows.Count;
                if (order.Count != n || order.Distinct().Count() != n || order.Any(x => x < 0 || x >= n))
                    throw Invalid("thứ tự sắp xếp phải gồm đủ mọi mục, mỗi mục một lần");
                return new QuestionSubmission(Order: order.Select(x => l.ItemOrder[x]).ToList());
            }

            case QuestionType.FILL_BLANK:
            {
                var given = shown.Blanks ?? [];
                if (given.Count > l.BlankCount) throw Invalid("số chỗ trống đã điền nhiều hơn số chỗ trống của câu");
                if (given.Any(t => t is { Length: > AttemptRules.ResponseTextMaxLength }))
                    throw Invalid($"mỗi chỗ trống tối đa {AttemptRules.ResponseTextMaxLength} ký tự");
                var blanks = new List<string?>(given.Select(t => string.IsNullOrWhiteSpace(t) ? null : t.Trim()));
                while (blanks.Count < l.BlankCount) blanks.Add(null);
                return blanks.All(x => x is null) ? null : new QuestionSubmission(Blanks: blanks);
            }

            case QuestionType.MULTI_TRUE_FALSE:
            {
                var given = shown.Statements ?? [];
                if (given.Count > l.Rows.Count) throw Invalid("số mệnh đề đã chọn nhiều hơn số mệnh đề của câu");
                var statements = new List<bool?>(given);
                while (statements.Count < l.Rows.Count) statements.Add(null);
                return statements.All(x => x is null) ? null : new QuestionSubmission(Statements: statements);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(l), l.Type, null);
        }
    }

    // ---- Lưu → em thấy ----

    /// <summary>Đổi câu trả lời (hoặc đáp án chuẩn) theo thứ tự lưu sang vị trí trên màn hình của em. Null khi bỏ trống.</summary>
    public static QuestionSubmission? ToShown(QuestionLayout l, QuestionSubmission? stored)
    {
        if (stored is null) return null;
        switch (l.Type)
        {
            case QuestionType.SINGLE_CHOICE:
            case QuestionType.TRUE_FALSE:
            case QuestionType.IMAGE_CHOICE:
            case QuestionType.SOURCE_READING:
                return stored.SelectedIndex is { } i && Array.IndexOf(l.OptionOrder, i) is var shownIndex and >= 0
                    ? new QuestionSubmission(SelectedIndex: shownIndex)
                    : null;

            case QuestionType.MATCHING:
            {
                if (stored.Matches is null) return null;
                var shown = new List<int?>();
                for (var left = 0; left < l.LeftOrder.Length; left++)
                {
                    var storedRight = l.LeftOrder[left] < stored.Matches.Count ? stored.Matches[l.LeftOrder[left]] : null;
                    shown.Add(storedRight is { } r ? Array.IndexOf(l.RightOrder, r) : null);
                }
                return shown.All(x => x is null) ? null : new QuestionSubmission(Matches: shown);
            }

            case QuestionType.ORDERING:
                return stored.Order is { Count: > 0 } order
                    ? new QuestionSubmission(Order: order.Select(x => Array.IndexOf(l.ItemOrder, x)).ToList())
                    : null;

            case QuestionType.FILL_BLANK:
                return stored.Blanks is { } b && b.Any(x => !string.IsNullOrWhiteSpace(x)) ? new QuestionSubmission(Blanks: b.ToList()) : null;

            case QuestionType.MULTI_TRUE_FALSE:
                return stored.Statements is { } s && s.Any(x => x is not null) ? new QuestionSubmission(Statements: s.ToList()) : null;

            default:
                return null;
        }
    }

    // ---- Ghi và đọc các dòng đã lưu ----

    /// <summary>
    /// Chấm câu trả lời (theo thứ tự lưu) và ghi vào dòng <paramref name="row"/>: <c>is_correct</c>, số phần, điểm, và với bốn dạng nhiều phần thì đủ một
    /// dòng <c>attempt_answer_parts</c> cho mỗi phần (kể cả phần bỏ trống). Dòng phần đã có được cập nhật tại chỗ để không chạm ràng buộc không trùng.
    /// Trả các dòng phần mới cần thêm vào CSDL.
    /// </summary>
    public static List<AttemptAnswerPart> Apply(AttemptAnswer row, QuestionLayout l, QuestionSubmission? stored, decimal? maxPoint)
    {
        var grade = QuestionGrader.Grade(l.Type, l.Rows, stored);
        row.IsCorrect = grade.IsCorrect;
        row.PartsTotal = (short)Math.Max(1, grade.PartsTotal);
        row.PartsCorrect = (short)grade.PartsCorrect;
        row.PointEarned = QuestionGrader.Score(maxPoint ?? 0m, grade);

        var added = new List<AttemptAnswerPart>();
        switch (l.Type)
        {
            case QuestionType.SINGLE_CHOICE:
            case QuestionType.TRUE_FALSE:
            case QuestionType.IMAGE_CHOICE:
            case QuestionType.SOURCE_READING:
                row.AnswerId = stored?.SelectedIndex is { } i && i >= 0 && i < l.Rows.Count ? l.Rows[i].Id : null;
                break;

            case QuestionType.MATCHING:
                row.AnswerId = null;
                for (var left = 0; left < l.Rows.Count; left++)
                {
                    var part = PartFor(row, added, l.Rows[left].Id, null);
                    part.SelectedAnswerId = stored?.Matches is { } m && left < m.Count && m[left] is { } r && r >= 0 && r < l.Rows.Count ? l.Rows[r].Id : null;
                    part.SelectedPosition = null;
                    part.SelectedBool = null;
                    part.ResponseText = null;
                    part.IsCorrect = grade.Parts[left];
                }
                break;

            case QuestionType.ORDERING:
                row.AnswerId = null;
                for (var item = 0; item < l.Rows.Count; item++)
                {
                    var part = PartFor(row, added, l.Rows[item].Id, null);
                    var position = stored?.Order is { } order ? order.IndexOf(item) : -1;
                    part.SelectedPosition = position >= 0 ? (byte)(position + 1) : null;
                    part.SelectedAnswerId = null;
                    part.SelectedBool = null;
                    part.ResponseText = null;
                    part.IsCorrect = grade.Parts[item];
                }
                break;

            case QuestionType.FILL_BLANK:
                row.AnswerId = null;
                for (var blank = 1; blank <= l.BlankCount; blank++)
                {
                    var part = PartFor(row, added, null, (byte)blank);
                    var text = stored?.Blanks is { } b && blank <= b.Count ? b[blank - 1] : null;
                    text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    part.ResponseText = text;
                    part.SelectedAnswerId = text is null || l.Shape.WordBank.Count == 0 ? null : WordBankRow(l, text)?.Id;
                    part.SelectedPosition = null;
                    part.SelectedBool = null;
                    part.IsCorrect = grade.Parts[blank - 1];
                }
                break;

            case QuestionType.MULTI_TRUE_FALSE:
                row.AnswerId = null;
                for (var statement = 0; statement < l.Rows.Count; statement++)
                {
                    var part = PartFor(row, added, l.Rows[statement].Id, null);
                    part.SelectedBool = stored?.Statements is { } s && statement < s.Count ? s[statement] : null;
                    part.SelectedAnswerId = null;
                    part.SelectedPosition = null;
                    part.ResponseText = null;
                    part.IsCorrect = grade.Parts[statement];
                }
                break;
        }
        return added;
    }

    private static AttemptAnswerPart PartFor(AttemptAnswer row, List<AttemptAnswerPart> added, long? answerId, byte? blankIndex)
    {
        var existing = row.Parts.FirstOrDefault(p => answerId is not null ? p.AnswerId == answerId : p.BlankIndex == blankIndex)
                       ?? added.FirstOrDefault(p => answerId is not null ? p.AnswerId == answerId : p.BlankIndex == blankIndex);
        if (existing is not null) return existing;
        var part = new AttemptAnswerPart { AnswerId = answerId, BlankIndex = blankIndex };
        row.Parts.Add(part);
        added.Add(part);
        return part;
    }

    /// <summary>Dòng đáp án của từ em chọn trong kho từ (khớp sau khi chuẩn hóa chữ); ưu tiên dòng là cách viết đúng.</summary>
    private static Answer? WordBankRow(QuestionLayout l, string text)
    {
        var normalized = QuestionGrader.NormalizeText(text);
        bool Matches(Answer a) => string.Equals(QuestionGrader.NormalizeText(a.Content), normalized, StringComparison.OrdinalIgnoreCase);
        return l.Rows.FirstOrDefault(a => a.IsCorrect && a.BlankIndex is not null && Matches(a)) ?? l.Rows.FirstOrDefault(a => a.BlankIndex is null && Matches(a));
    }

    /// <summary>Đọc câu trả lời đã lưu (theo thứ tự lưu) từ dòng <paramref name="row"/> và các dòng phần của nó. Null nếu bỏ trống.</summary>
    public static QuestionSubmission? ToStored(QuestionLayout l, AttemptAnswer? row)
    {
        if (row is null) return null;
        var rows = l.Rows;
        switch (l.Type)
        {
            case QuestionType.SINGLE_CHOICE:
            case QuestionType.TRUE_FALSE:
            case QuestionType.IMAGE_CHOICE:
            case QuestionType.SOURCE_READING:
            {
                var i = row.AnswerId is { } id ? rows.FindIndex(a => a.Id == id) : -1;
                return i >= 0 ? new QuestionSubmission(SelectedIndex: i) : null;
            }

            case QuestionType.MATCHING:
            {
                var matches = rows.Select(a =>
                {
                    var selected = row.Parts.FirstOrDefault(p => p.AnswerId == a.Id)?.SelectedAnswerId;
                    var index = selected is { } sid ? rows.FindIndex(x => x.Id == sid) : -1;
                    return index >= 0 ? (int?)index : null;
                }).ToList();
                return matches.All(x => x is null) ? null : new QuestionSubmission(Matches: matches);
            }

            case QuestionType.ORDERING:
            {
                var placed = row.Parts.Where(p => p.SelectedPosition is not null && p.AnswerId is not null)
                    .OrderBy(p => p.SelectedPosition)
                    .Select(p => rows.FindIndex(a => a.Id == p.AnswerId)).Where(i => i >= 0).ToList();
                return placed.Count == 0 ? null : new QuestionSubmission(Order: placed);
            }

            case QuestionType.FILL_BLANK:
            {
                var blanks = Enumerable.Range(1, l.BlankCount)
                    .Select(b => row.Parts.FirstOrDefault(p => p.BlankIndex == b)?.ResponseText).ToList();
                return blanks.All(string.IsNullOrWhiteSpace) ? null : new QuestionSubmission(Blanks: blanks);
            }

            case QuestionType.MULTI_TRUE_FALSE:
            {
                var statements = rows.Select(a => row.Parts.FirstOrDefault(p => p.AnswerId == a.Id)?.SelectedBool).ToList();
                return statements.All(x => x is null) ? null : new QuestionSubmission(Statements: statements);
            }

            default:
                return null;
        }
    }

    private static AppException Invalid(string rule) => AppException.Validation("answer", Messages.Invalid("Câu trả lời", rule));
}
