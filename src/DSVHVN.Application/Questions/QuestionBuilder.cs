using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Questions;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Questions;

/// <summary>
/// Kết quả dựng câu từ nội dung gửi lên: câu đã chuẩn hóa (thực thể chưa lưu, đã có các dòng đáp án) hoặc danh sách lỗi theo trường.
/// Tên trường dùng đường dẫn camelCase như JSON (<c>options[1].content</c>, <c>pairs[0].right</c>).
/// </summary>
public sealed class QuestionBuildResult
{
    public Question? Question { get; init; }

    public IReadOnlyList<KeyValuePair<string, AppMessage>> Errors { get; init; } = [];

    public bool IsValid => Question is not null;

    /// <summary>Lỗi theo trường (mỗi trường giữ lỗi đầu tiên) để trả 400 hoặc hiện dưới ô nhập.</summary>
    public IReadOnlyDictionary<string, string> ErrorTexts =>
        Errors.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.First().Value.Text);

    public AppException ToException() =>
        new(400, Errors.Count == 0 ? Messages.Invalid("Câu hỏi", "dữ liệu không đúng") : Errors[0].Value, ErrorTexts);
}

/// <summary>
/// Kiểm hợp lệ nội dung câu hỏi theo quy tắc Cấu trúc câu hỏi và Dạng câu hỏi và cấp phù hợp, rồi dựng thực thể <see cref="Question"/> và các dòng
/// <see cref="Answer"/> theo lược đồ CSDL v4. Thuần dữ liệu, không đọc CSDL: thư viện ảnh của di sản được truyền vào. Cuối cùng chấm thử đáp án
/// chuẩn (<see cref="QuestionGrader.SelfCheck"/>): đáp án chuẩn phải đúng đủ mọi phần, câu bỏ trống phải 0 điểm.
/// </summary>
public static class QuestionBuilder
{
    /// <param name="request">Nội dung gửi lên.</param>
    /// <param name="libraryImageUrls">Đường dẫn các ảnh trong thư viện của di sản được gắn; mọi ảnh dùng trong câu phải nằm trong tập này.</param>
    public static QuestionBuildResult Build(SaveQuestionRequest request, IReadOnlySet<string> libraryImageUrls)
    {
        var errors = new List<KeyValuePair<string, AppMessage>>();
        void Fail(string field, AppMessage message) => errors.Add(new(field, message));
        void Required(string field, string label) => Fail(field, Messages.Required(label));
        void Invalid(string field, string label, string rule) => Fail(field, Messages.Invalid(label, rule));

        // ---- Chung cho mọi dạng ----
        if (request.QuestionType is not { } type || !Enum.IsDefined(type))
        {
            Required("questionType", "dạng câu hỏi");
            return new QuestionBuildResult { Errors = errors };
        }
        var info = QuestionTypes.Of(type);

        if (request.HeritageId is null or <= 0) Required("heritageId", "di sản của câu hỏi");

        var content = Clean(request.Content);
        if (content is null) Required("content", "nội dung câu hỏi");
        else if (content.Length is < QuestionRules.ContentMinLength or > QuestionRules.ContentMaxLength)
            Invalid("content", "Nội dung câu hỏi", $"dài {QuestionRules.ContentMinLength}-{QuestionRules.ContentMaxLength} ký tự");

        var explanation = Clean(request.Explanation);
        if (explanation is null) Required("explanation", "giải thích đáp án");
        else if (explanation.Length > QuestionRules.ExplanationMaxLength)
            Invalid("explanation", "Giải thích", $"tối đa {QuestionRules.ExplanationMaxLength} ký tự");

        if (request.DifficultyLevel is not { } difficulty || !Enum.IsDefined(difficulty)) Required("difficultyLevel", "độ khó");

        // Cấp phù hợp: khoảng liền nhau nằm trong các cấp dạng câu cho phép; để trống thì lấy đủ các cấp đó.
        var levelMin = request.LevelMin ?? info.MinLevel;
        var levelMax = request.LevelMax ?? info.MaxLevel;
        if (levelMin is < 1 or > 3) Invalid("levelMin", "Cấp phù hợp", "cấp học từ 1 đến 3");
        else if (levelMax is < 1 or > 3) Invalid("levelMax", "Cấp phù hợp", "cấp học từ 1 đến 3");
        else if (levelMin > levelMax) Invalid("levelMin", "Cấp phù hợp", "cấp thấp nhất không lớn hơn cấp cao nhất");
        else if (levelMin < info.MinLevel || levelMax > info.MaxLevel)
            Invalid("levelMin", "Cấp phù hợp", $"dạng \"{info.Name}\" chỉ dùng được ở {LevelRange(info.MinLevel, info.MaxLevel)}");

        var q = new Question
        {
            QuestionType = type,
            HeritageId = request.HeritageId ?? 0,
            Content = content,
            Explanation = explanation,
            DifficultyLevel = request.DifficultyLevel,
            LevelMin = (byte)Math.Clamp(levelMin, 1, 3),
            LevelMax = (byte)Math.Clamp(levelMax, 1, 3),
        };
        var rows = new List<Answer>();

        // ---- Theo dạng ----
        switch (type)
        {
            case QuestionType.SINGLE_CHOICE:
                BuildChoice(request.Options, rows, Fail, Required, Invalid);
                break;

            case QuestionType.SOURCE_READING:
                BuildChoice(request.Options, rows, Fail, Required, Invalid);
                var passage = Clean(request.Passage);
                if (passage is null) Required("passage", "đoạn tư liệu");
                else if (LessonText.CountWords(passage) > QuestionRules.PassageMaxWords)
                    Invalid("passage", "Đoạn tư liệu", $"tối đa {QuestionRules.PassageMaxWords} từ");
                var source = Clean(request.PassageSource);
                if (source is null) Required("passageSource", "nguồn của đoạn tư liệu");
                else if (source.Length > QuestionRules.PassageSourceMaxLength)
                    Invalid("passageSource", "Nguồn của đoạn tư liệu", $"tối đa {QuestionRules.PassageSourceMaxLength} ký tự");
                q.Passage = passage;
                q.PassageSource = source;
                break;

            case QuestionType.TRUE_FALSE:
                if (request.AnswerIsTrue is not { } isTrue) Required("answerIsTrue", "đáp án Đúng hoặc Sai");
                else
                {
                    rows.Add(Row(QuestionRules.TrueFalseOptions[0], 1, isTrue));
                    rows.Add(Row(QuestionRules.TrueFalseOptions[1], 2, !isTrue));
                }
                var image = Clean(request.ImageUrl);
                if (image is not null) q.ImageUrl = CheckImage("imageUrl", image, libraryImageUrls, Fail);
                else if (q.LevelMin == 1) Required("imageUrl", "hình của câu Đúng/Sai (câu dùng được ở cấp 1 phải có hình)");
                break;

            case QuestionType.IMAGE_CHOICE:
                BuildImageChoice(request.Options, rows, libraryImageUrls, Fail, Required, Invalid);
                break;

            case QuestionType.MATCHING:
                BuildMatching(request.Pairs, rows, libraryImageUrls, Fail, Required, Invalid);
                break;

            case QuestionType.ORDERING:
                BuildOrdering(request.Steps, q.LevelMin == 1, rows, Fail, Required, Invalid);
                break;

            case QuestionType.FILL_BLANK:
                BuildFillBlank(content, request.Blanks, request.Distractors, rows, Fail, Required, Invalid);
                break;

            case QuestionType.MULTI_TRUE_FALSE:
                BuildStatements(request.Statements, rows, Fail, Required, Invalid);
                break;
        }

        if (errors.Count == 0)
        {
            foreach (var row in rows) q.Answers.Add(row);
            var check = QuestionGrader.SelfCheck(type, q.Answers);
            if (check is not null) Fail("question", Messages.Invalid("Câu hỏi", check));
        }

        return errors.Count == 0 ? new QuestionBuildResult { Question = q } : new QuestionBuildResult { Errors = errors };
    }

    // ---- Một đáp án đúng, đọc tư liệu: đúng 4 phương án, không trùng, đúng một phương án đúng ----

    private static void BuildChoice(List<QuestionOption>? options, List<Answer> rows,
        Action<string, AppMessage> fail, Action<string, string> required, Action<string, string, string> invalid)
    {
        if (options is null || options.Count != QuestionRules.ChoiceOptionCount)
        {
            invalid("options", "Phương án", $"phải có đúng {QuestionRules.ChoiceOptionCount} phương án");
            return;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ok = true;
        for (var i = 0; i < options.Count; i++)
        {
            var text = Clean(options[i].Content);
            if (text is null) { required($"options[{i}].content", $"phương án {Letter(i)}"); ok = false; }
            else if (text.Length > QuestionRules.OptionMaxLength)
            {
                invalid($"options[{i}].content", $"Phương án {Letter(i)}", $"tối đa {QuestionRules.OptionMaxLength} ký tự");
                ok = false;
            }
            else if (!seen.Add(Collapse(text)))
            {
                invalid($"options[{i}].content", $"Phương án {Letter(i)}", "không được trùng nội dung với phương án khác");
                ok = false;
            }
            else rows.Add(Row(text, i + 1, options[i].IsCorrect));
        }
        if (ok) ExactlyOneCorrect(rows, "options", invalid);
    }

    // ---- Chọn hình đúng: 3-4 thẻ hình khác nhau, nhãn 1-50 ký tự ----

    private static void BuildImageChoice(List<QuestionOption>? options, List<Answer> rows, IReadOnlySet<string> library,
        Action<string, AppMessage> fail, Action<string, string> required, Action<string, string, string> invalid)
    {
        if (options is null || options.Count is < QuestionRules.ImageChoiceMinCards or > QuestionRules.ImageChoiceMaxCards)
        {
            invalid("options", "Thẻ hình", $"phải có {QuestionRules.ImageChoiceMinCards}-{QuestionRules.ImageChoiceMaxCards} thẻ");
            return;
        }
        var images = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ok = true;
        for (var i = 0; i < options.Count; i++)
        {
            var label = Clean(options[i].Content);
            if (label is null) { required($"options[{i}].content", $"nhãn của thẻ {i + 1}"); ok = false; }
            else if (label.Length > QuestionRules.LabelMaxLength)
            {
                invalid($"options[{i}].content", $"Nhãn của thẻ {i + 1}", $"dài {QuestionRules.LabelMinLength}-{QuestionRules.LabelMaxLength} ký tự");
                ok = false;
            }
            else if (!labels.Add(Collapse(label)))
            {
                invalid($"options[{i}].content", $"Nhãn của thẻ {i + 1}", "không được trùng với nhãn thẻ khác");
                ok = false;
            }

            var url = Clean(options[i].ImageUrl);
            if (url is null) { required($"options[{i}].imageUrl", $"hình của thẻ {i + 1}"); ok = false; }
            else if (CheckImage($"options[{i}].imageUrl", url, library, fail) is null) ok = false;
            else if (!images.Add(url))
            {
                invalid($"options[{i}].imageUrl", $"Hình của thẻ {i + 1}", "mỗi thẻ phải dùng một hình khác nhau");
                ok = false;
            }
            if (ok) rows.Add(Row(label, i + 1, options[i].IsCorrect, image: url));
        }
        if (ok) ExactlyOneCorrect(rows, "options", invalid);
    }

    // ---- Ghép cặp: 3-5 cặp, vế trái là chữ hoặc hình, vế phải là tên 1-50 ký tự, các vế không trùng ----

    private static void BuildMatching(List<QuestionPair>? pairs, List<Answer> rows, IReadOnlySet<string> library,
        Action<string, AppMessage> fail, Action<string, string> required, Action<string, string, string> invalid)
    {
        if (pairs is null || pairs.Count is < QuestionRules.MatchingMinPairs or > QuestionRules.MatchingMaxPairs)
        {
            invalid("pairs", "Các cặp", $"phải có {QuestionRules.MatchingMinPairs}-{QuestionRules.MatchingMaxPairs} cặp");
            return;
        }
        var lefts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rights = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ok = true;
        for (var i = 0; i < pairs.Count; i++)
        {
            var left = Clean(pairs[i].Left);
            var image = Clean(pairs[i].LeftImageUrl);
            if (left is null && image is null) { required($"pairs[{i}].left", $"vế trái của cặp {i + 1} (chữ hoặc hình)"); ok = false; }
            else
            {
                if (left is { Length: > QuestionRules.MatchLeftMaxLength })
                {
                    invalid($"pairs[{i}].left", $"Vế trái của cặp {i + 1}", $"tối đa {QuestionRules.MatchLeftMaxLength} ký tự");
                    ok = false;
                }
                if (image is not null && CheckImage($"pairs[{i}].leftImageUrl", image, library, fail) is null) ok = false;
                else if (ok && !lefts.Add(Collapse(image ?? left!)))
                {
                    invalid($"pairs[{i}].left", $"Vế trái của cặp {i + 1}", "các vế bên trái không được trùng nhau");
                    ok = false;
                }
            }

            var right = Clean(pairs[i].Right);
            if (right is null) { required($"pairs[{i}].right", $"vế phải (tên) của cặp {i + 1}"); ok = false; }
            else if (right.Length > QuestionRules.LabelMaxLength)
            {
                invalid($"pairs[{i}].right", $"Vế phải của cặp {i + 1}", $"dài {QuestionRules.LabelMinLength}-{QuestionRules.LabelMaxLength} ký tự");
                ok = false;
            }
            else if (!rights.Add(Collapse(right)))
            {
                invalid($"pairs[{i}].right", $"Vế phải của cặp {i + 1}", "các vế bên phải không được trùng nhau");
                ok = false;
            }
            if (ok) rows.Add(Row(left, i + 1, true, image: image, match: right));
        }
    }

    // ---- Sắp xếp: 3-6 mục (tối đa 4 nếu dùng được ở cấp 1), liệt kê theo thứ tự đúng ----

    private static void BuildOrdering(List<string>? steps, bool usableAtLevelOne, List<Answer> rows,
        Action<string, AppMessage> fail, Action<string, string> required, Action<string, string, string> invalid)
    {
        var max = usableAtLevelOne ? QuestionRules.OrderingMaxItemsLevel1 : QuestionRules.OrderingMaxItems;
        if (steps is null || steps.Count < QuestionRules.OrderingMinItems || steps.Count > max)
        {
            invalid("steps", "Các mục cần sắp xếp",
                usableAtLevelOne
                    ? $"câu dùng được ở cấp 1 có {QuestionRules.OrderingMinItems}-{max} mục; câu nhiều hơn {max} mục chỉ dùng ở cấp 2"
                    : $"phải có {QuestionRules.OrderingMinItems}-{max} mục");
            return;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < steps.Count; i++)
        {
            var text = Clean(steps[i]);
            if (text is null) required($"steps[{i}]", $"mục {i + 1}");
            else if (text.Length > QuestionRules.OrderingItemMaxLength)
            {
                invalid($"steps[{i}]", $"Mục {i + 1}", $"tối đa {QuestionRules.OrderingItemMaxLength} ký tự");
            }
            else if (!seen.Add(Collapse(text)))
            {
                invalid($"steps[{i}]", $"Mục {i + 1}", "các mục không được trùng nội dung");
            }
            else rows.Add(Row(text, i + 1, true, position: i + 1));
        }
    }

    // ---- Điền từ: 1-3 chỗ trống (___), mỗi chỗ 1-5 cách viết đúng; kho từ 3-8 từ khi có từ gây nhiễu ----

    private static void BuildFillBlank(string? content, List<QuestionBlank>? blanks, List<string>? distractors, List<Answer> rows,
        Action<string, AppMessage> fail, Action<string, string> required, Action<string, string, string> invalid)
    {
        var markers = content is null ? 0 : CountMarkers(content);
        if (markers is < QuestionRules.FillMinBlanks or > QuestionRules.FillMaxBlanks)
        {
            invalid("content", "Nội dung câu hỏi",
                $"câu điền từ có {QuestionRules.FillMinBlanks}-{QuestionRules.FillMaxBlanks} chỗ trống, mỗi chỗ trống đánh dấu bằng ba dấu gạch dưới liền nhau");
            return;
        }
        if (blanks is null || blanks.Count != markers)
        {
            invalid("blanks", "Đáp án các chỗ trống", $"câu có {markers} chỗ trống nên cần đúng {markers} nhóm đáp án");
            return;
        }

        var ok = true;
        var order = 0;
        var allAccepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var firstAccepted = new List<string>();
        for (var b = 0; b < blanks.Count; b++)
        {
            var accepted = blanks[b].Accepted ?? [];
            if (accepted.Count is < QuestionRules.AcceptedMinCount or > QuestionRules.AcceptedMaxCount)
            {
                invalid($"blanks[{b}].accepted", $"Đáp án của chỗ trống {b + 1}",
                    $"có {QuestionRules.AcceptedMinCount}-{QuestionRules.AcceptedMaxCount} cách viết đúng");
                ok = false;
                continue;
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var a = 0; a < accepted.Count; a++)
            {
                var text = Clean(accepted[a]);
                if (text is null) { required($"blanks[{b}].accepted[{a}]", $"đáp án của chỗ trống {b + 1}"); ok = false; }
                else if (text.Length > QuestionRules.AcceptedMaxLength)
                {
                    invalid($"blanks[{b}].accepted[{a}]", $"Đáp án của chỗ trống {b + 1}", $"tối đa {QuestionRules.AcceptedMaxLength} ký tự");
                    ok = false;
                }
                else if (!seen.Add(Collapse(text)))
                {
                    invalid($"blanks[{b}].accepted[{a}]", $"Đáp án của chỗ trống {b + 1}", "các cách viết đúng không được trùng nhau");
                    ok = false;
                }
                else
                {
                    allAccepted.Add(Collapse(text));
                    if (a == 0) firstAccepted.Add(text);
                    rows.Add(Row(text, ++order, true, blank: b + 1));
                }
            }
        }

        var extra = (distractors ?? []).Select(Clean).ToList();
        if (ok && extra.Count > 0)
        {
            var bank = new HashSet<string>(firstAccepted.Select(Collapse), StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < extra.Count; i++)
            {
                var text = extra[i];
                if (text is null) { required($"distractors[{i}]", $"từ gây nhiễu {i + 1}"); ok = false; }
                else if (text.Length > QuestionRules.AcceptedMaxLength)
                {
                    invalid($"distractors[{i}]", $"Từ gây nhiễu {i + 1}", $"tối đa {QuestionRules.AcceptedMaxLength} ký tự");
                    ok = false;
                }
                else if (allAccepted.Contains(Collapse(text)) || !bank.Add(Collapse(text)))
                {
                    invalid($"distractors[{i}]", $"Từ gây nhiễu {i + 1}", "không được trùng với đáp án đúng hoặc từ khác trong kho từ");
                    ok = false;
                }
                else rows.Add(Row(text, ++order, false));
            }
            if (ok && bank.Count is < QuestionRules.WordBankMinWords or > QuestionRules.WordBankMaxWords)
                invalid("distractors", "Kho từ", $"gồm các từ đúng và từ gây nhiễu, phải có {QuestionRules.WordBankMinWords}-{QuestionRules.WordBankMaxWords} từ");
        }
    }

    // ---- Nhiều mệnh đề Đúng/Sai: 3-5 mệnh đề 10-500 ký tự, không cùng Đúng hoặc cùng Sai ----

    private static void BuildStatements(List<QuestionStatement>? statements, List<Answer> rows,
        Action<string, AppMessage> fail, Action<string, string> required, Action<string, string, string> invalid)
    {
        if (statements is null || statements.Count is < QuestionRules.StatementMinCount or > QuestionRules.StatementMaxCount)
        {
            invalid("statements", "Các mệnh đề", $"phải có {QuestionRules.StatementMinCount}-{QuestionRules.StatementMaxCount} mệnh đề");
            return;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ok = true;
        for (var i = 0; i < statements.Count; i++)
        {
            var text = Clean(statements[i].Text);
            if (text is null) { required($"statements[{i}].text", $"mệnh đề {i + 1}"); ok = false; }
            else if (text.Length is < QuestionRules.StatementMinLength or > QuestionRules.StatementMaxLength)
            {
                invalid($"statements[{i}].text", $"Mệnh đề {i + 1}", $"dài {QuestionRules.StatementMinLength}-{QuestionRules.StatementMaxLength} ký tự");
                ok = false;
            }
            else if (!seen.Add(Collapse(text)))
            {
                invalid($"statements[{i}].text", $"Mệnh đề {i + 1}", "các mệnh đề không được trùng nội dung");
                ok = false;
            }
            else rows.Add(Row(text, i + 1, statements[i].IsTrue));
        }
        if (ok && (rows.All(r => r.IsCorrect) || rows.All(r => !r.IsCorrect)))
            invalid("statements", "Các mệnh đề", "không được để mọi mệnh đề cùng Đúng hoặc cùng Sai");
    }

    // ---- Dùng chung ----

    private static void ExactlyOneCorrect(List<Answer> rows, string field, Action<string, string, string> invalid)
    {
        if (rows.Count(r => r.IsCorrect) != 1) invalid(field, "Đáp án đúng", "phải đánh dấu đúng một đáp án đúng");
    }

    private static string? CheckImage(string field, string url, IReadOnlySet<string> library, Action<string, AppMessage> fail)
    {
        if (url.Length > QuestionRules.UrlMaxLength)
        {
            fail(field, Messages.Invalid("Hình", $"đường dẫn tối đa {QuestionRules.UrlMaxLength} ký tự"));
            return null;
        }
        if (!library.Contains(url))
        {
            fail(field, Messages.Invalid("Hình", "phải là ảnh trong thư viện ảnh của di sản được gắn"));
            return null;
        }
        return url;
    }

    private static Answer Row(string? content, int order, bool correct, string? image = null, string? match = null, int? position = null, int? blank = null) =>
        new()
        {
            Content = content,
            ImageUrl = image,
            MatchContent = match,
            CorrectPosition = position is { } p ? (byte)p : null,
            BlankIndex = blank is { } b ? (byte)b : null,
            IsCorrect = correct,
            SortOrder = order,
        };

    /// <summary>Số chỗ trống: các cụm ba dấu gạch dưới liền nhau (cụm dài hơn ba vẫn tính một chỗ trống).</summary>
    public static int CountMarkers(string content)
    {
        var count = 0;
        var run = 0;
        foreach (var c in content)
        {
            if (c == '_') { run++; continue; }
            if (run >= 3) count++;
            run = 0;
        }
        if (run >= 3) count++;
        return count;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Khóa so sánh trùng: gộp khoảng trắng liên tiếp, không phân biệt hoa thường.</summary>
    private static string Collapse(string value) => System.Text.RegularExpressions.Regex.Replace(value.Trim(), @"\s+", " ").ToLowerInvariant();

    private static string Letter(int i) => ((char)('A' + i)).ToString();

    private static string LevelRange(int min, int max) => min == max ? $"cấp {min}" : $"cấp {min}-{max}";
}
