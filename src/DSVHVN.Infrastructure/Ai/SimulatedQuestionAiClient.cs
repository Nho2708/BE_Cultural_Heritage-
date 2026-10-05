using System.Text.RegularExpressions;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Infrastructure.Ai;

/// <summary>
/// BẢN GIẢ LẬP của Dịch vụ AI sinh câu hỏi (chọn bằng <c>Ai:Mode = Simulated</c>, là mặc định). Dịch vụ AI thật chưa có; bản này thực hiện đúng
/// giao diện <see cref="IQuestionAiClient"/> của hợp đồng để toàn bộ luồng (giữ lượt, ghi nhật ký, kiểm kết quả, câu chờ duyệt, giáo viên duyệt)
/// chạy được. Không gọi mạng, không gọi nhà cung cấp mô hình nào, không dùng khóa API nào.
/// <para>
/// Cách làm: ghép câu từ chính dữ liệu di sản đã gửi (nội dung, dòng thời gian, nguồn, ảnh có sẵn), tất định (cùng đầu vào cho cùng đầu ra), đúng
/// cấu trúc của từng dạng. Các dạng được sinh lần lượt theo vòng và di sản theo vòng; câu thứ hai của cùng dạng và di sản lấy ý khác. Dạng thiếu
/// dữ liệu (chọn hình đúng khi di sản có dưới 3 ảnh, sắp xếp sự kiện khi dòng thời gian có dưới 3 mốc...) thì bị bỏ qua, nên có thể nhận ít câu
/// hơn số yêu cầu. Chất lượng chữ chỉ ở mức mẫu để thử luồng; bản thật dùng mô hình ngôn ngữ và giáo viên vẫn phải duyệt từng câu.
/// </para>
/// </summary>
public sealed partial class SimulatedQuestionAiClient : IQuestionAiClient
{
    public const string Name = "simulated-question-generator";

    private const int Level1MaxWords = 12;
    private const int MaxFactLength = 250;

    public string ModelName => Name;

    public Task<AiQuestionResult> GenerateQuestionsAsync(AiQuestionRequest request, string correlationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.GradeLevel is < 1 or > 3) throw new AiServiceException("Cấp học phải là 1, 2 hoặc 3.", retryable: false);
        if (request.Types.Count == 0 || request.Heritages.Count == 0)
            throw new AiServiceException("Yêu cầu thiếu dạng câu hoặc di sản.", retryable: false);

        var made = new List<AiGeneratedQuestion>();
        var used = new Dictionary<(QuestionType, long), int>();
        var seen = new HashSet<string>();
        var misses = 0;
        // Mỗi vòng thử một cặp (dạng, di sản); dừng khi đủ số câu hoặc đã thử đủ nhiều mà không ra thêm.
        for (var i = 0; made.Count < request.Count && misses < request.Types.Count * request.Heritages.Count * 2 + 2; i++)
        {
            var type = request.Types[i % request.Types.Count];
            var heritage = request.Heritages[(i / request.Types.Count + i) % request.Heritages.Count];
            used.TryGetValue((type, heritage.HeritageId), out var variant);
            var question = Make(type, heritage, variant + (int)request.Difficulty, request.GradeLevel);
            used[(type, heritage.HeritageId)] = variant + 1;
            if (question is null) { misses++; continue; }
            if (!seen.Add(Signature(question))) { misses++; continue; }
            made.Add(question);
        }

        if (made.Count == 0)
            throw new AiServiceException("Di sản chưa đủ dữ liệu (nội dung, ảnh, dòng thời gian) để sinh các dạng câu đã chọn.", retryable: false);

        var input = request.Heritages.Sum(h => (h.Content?.Length ?? 0) + (h.ShortDescription?.Length ?? 0));
        var output = made.Sum(q => (q.Content?.Length ?? 0) + (q.Explanation?.Length ?? 0));
        return Task.FromResult(new AiQuestionResult(made, Name, new AiUsage(input / 4, output / 4)));
    }

    private static AiGeneratedQuestion? Make(QuestionType type, AiHeritageInput h, int variant, int level) => type switch
    {
        QuestionType.SINGLE_CHOICE => SingleChoice(h, variant),
        QuestionType.TRUE_FALSE => TrueFalse(h, variant, level),
        QuestionType.IMAGE_CHOICE => ImageChoice(h, variant),
        QuestionType.MATCHING => Matching(h, variant),
        QuestionType.ORDERING => Ordering(h, variant, level),
        QuestionType.FILL_BLANK => FillBlank(h, variant),
        QuestionType.MULTI_TRUE_FALSE => MultiTrueFalse(h, variant),
        QuestionType.SOURCE_READING => SourceReading(h, variant),
        _ => null,
    };

    // ---- Một đáp án đúng ----

    private static AiGeneratedQuestion? SingleChoice(AiHeritageInput h, int variant)
    {
        if (h.Timeline.Count > 0)
        {
            var e = h.Timeline[variant % h.Timeline.Count];
            var shifts = new[] { 37, -53, 121, -89 };
            var wrong = shifts.Select(s => HeritageTimeText.Year(e.StartYear + s)).Where(l => l != e.DateLabel).Distinct().Take(3).ToList();
            if (wrong.Count == 3)
            {
                var options = Rotate([Option(e.DateLabel, true), .. wrong.Select(w => Option(w, false))], variant);
                return new AiGeneratedQuestion(h.HeritageId, QuestionType.SINGLE_CHOICE,
                    $"Sự kiện \"{e.Name}\" của {h.Name} diễn ra vào thời điểm nào?",
                    $"Theo dòng thời gian của {h.Name}, sự kiện \"{e.Name}\" diễn ra vào {e.DateLabel}.", Options: options);
            }
        }

        var fact = Fact(h, variant);
        if (fact is null) return null;
        var falses = FalseStatements(h.Name);
        return new AiGeneratedQuestion(h.HeritageId, QuestionType.SINGLE_CHOICE,
            $"Câu nào sau đây nêu đúng về {h.Name}?",
            $"Câu đúng được lấy từ nội dung di sản {h.Name}; ba câu còn lại không đúng sự thật.",
            Options: Rotate([Option(fact, true), .. falses.Take(3).Select(f => Option(f, false))], variant));
    }

    // ---- Đúng/Sai (cấp 1 bắt buộc có hình) ----

    private static AiGeneratedQuestion? TrueFalse(AiHeritageInput h, int variant, int level)
    {
        var isTrue = variant % 2 == 0;
        string? statement;
        if (isTrue) statement = level == 1 ? ShortFact(h, variant) : Fact(h, variant);
        else statement = FalseStatements(h.Name)[variant % 4];
        if (statement is null) return null;

        string? image = null;
        if (level == 1)
        {
            if (h.Images.Count == 0) return null;
            image = h.Images[variant % h.Images.Count].Url;
        }
        var content = level == 1
            ? $"Em hãy xem hình và cho biết câu sau đúng hay sai: {statement}"
            : $"Câu sau đúng hay sai? {statement}";
        return new AiGeneratedQuestion(h.HeritageId, QuestionType.TRUE_FALSE, content,
            isTrue ? $"Câu này đúng, lấy từ nội dung di sản {h.Name}." : $"Câu này sai, không đúng với nội dung di sản {h.Name}.",
            ImageUrl: image, AnswerIsTrue: isTrue);
    }

    // ---- Chọn hình đúng ----

    private static AiGeneratedQuestion? ImageChoice(AiHeritageInput h, int variant)
    {
        var images = DistinctImages(h);
        if (images.Count < QuestionRules.ImageChoiceMinCards) return null;
        var cards = Rotate(images, variant).Take(QuestionRules.ImageChoiceMaxCards).ToList();
        var labels = Labels(h, cards);
        var correct = variant % cards.Count;
        return new AiGeneratedQuestion(h.HeritageId, QuestionType.IMAGE_CHOICE,
            $"Em hãy chọn hình có tên: {labels[correct]}.",
            $"Hình đúng là hình có tên \"{labels[correct]}\" trong thư viện ảnh của di sản {h.Name}.",
            Options: cards.Select((c, i) => new QuestionOption(labels[i], c.Url, i == correct)).ToList());
    }

    // ---- Ghép cặp hình - tên ----

    private static AiGeneratedQuestion? Matching(AiHeritageInput h, int variant)
    {
        var images = DistinctImages(h);
        if (images.Count < QuestionRules.MatchingMinPairs) return null;
        var cards = Rotate(images, variant).Take(QuestionRules.MatchingMaxPairs).ToList();
        var labels = Labels(h, cards);
        return new AiGeneratedQuestion(h.HeritageId, QuestionType.MATCHING,
            "Em hãy ghép mỗi hình với tên của hình đó.",
            $"Mỗi tên là chú thích của hình tương ứng trong thư viện ảnh của di sản {h.Name}.",
            Pairs: cards.Select((c, i) => new QuestionPair(null, c.Url, labels[i])).ToList());
    }

    // ---- Sắp xếp: các bước của câu chuyện (cấp 1) hoặc sự kiện theo thời gian (cấp 2) ----

    private static AiGeneratedQuestion? Ordering(AiHeritageInput h, int variant, int level)
    {
        if (level == 1)
        {
            var facts = Facts(h, level1: true);
            if (facts.Count < QuestionRules.OrderingMinItems) return null;
            var size = Math.Min(QuestionRules.OrderingMaxItemsLevel1, facts.Count);
            var start = variant % (facts.Count - size + 1);
            return new AiGeneratedQuestion(h.HeritageId, QuestionType.ORDERING,
                $"Em hãy sắp xếp các bước kể về {h.Name} theo đúng thứ tự.",
                $"Thứ tự đúng là thứ tự các câu trong câu chuyện về {h.Name}.",
                Steps: facts.Skip(start).Take(size).Select(f => Cut(f, QuestionRules.OrderingItemMaxLength)).ToList());
        }

        var events = h.Timeline.OrderBy(t => t.StartYear).ThenBy(t => t.EventId).GroupBy(t => t.StartYear).Select(g => g.First()).ToList();
        if (events.Count < QuestionRules.OrderingMinItems) return null;
        var take = Math.Min(QuestionRules.OrderingMaxItems, events.Count);
        var skip = variant % (events.Count - take + 1);
        return new AiGeneratedQuestion(h.HeritageId, QuestionType.ORDERING,
            $"Hãy sắp xếp các sự kiện của {h.Name} theo thứ tự thời gian.",
            $"Các sự kiện được xếp theo năm diễn ra trên dòng thời gian của {h.Name}.",
            Steps: events.Skip(skip).Take(take).Select(e => Cut($"{e.DateLabel}: {e.Name}", QuestionRules.OrderingItemMaxLength)).ToList());
    }

    // ---- Điền từ: bỏ một từ khóa của một câu trong nội dung ----

    private static AiGeneratedQuestion? FillBlank(AiHeritageInput h, int variant)
    {
        var facts = Facts(h, level1: false).Where(f => f.Length <= 200).ToList();
        for (var offset = 0; offset < facts.Count; offset++)
        {
            var sentence = facts[(variant + offset) % facts.Count];
            var word = KeyWord(sentence);
            if (word is null) continue;

            var blanked = ReplaceFirst(sentence, word, QuestionRules.BlankMarker);
            var distractors = Words(string.Join(' ', facts.Where(f => f != sentence)))
                .Where(w => w.Length >= 4 && !string.Equals(w, word, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
            foreach (var filler in new[] { "triều đại", "lễ hội", "kiến trúc", "truyền thống" })
            {
                if (distractors.Count >= 2) break;
                if (!string.Equals(filler, word, StringComparison.OrdinalIgnoreCase) && !distractors.Contains(filler)) distractors.Add(filler);
            }
            return new AiGeneratedQuestion(h.HeritageId, QuestionType.FILL_BLANK,
                $"Điền từ thích hợp vào chỗ trống: {blanked}",
                $"Từ cần điền là \"{word}\", lấy từ nội dung di sản {h.Name}.",
                Blanks: [new QuestionBlank([word])], Distractors: distractors);
        }
        return null;
    }

    // ---- Nhiều mệnh đề Đúng/Sai ----

    private static AiGeneratedQuestion? MultiTrueFalse(AiHeritageInput h, int variant)
    {
        var facts = Facts(h, level1: false);
        if (facts.Count == 0) return null;
        var falses = FalseStatements(h.Name);
        var first = facts[variant % facts.Count];
        var second = facts.Count >= 2 ? facts[(variant + 1) % facts.Count] : null;
        var statements = new List<QuestionStatement>
        {
            new(first, true),
            new(falses[variant % 4], false),
            second is null ? new QuestionStatement(falses[(variant + 1) % 4], false) : new QuestionStatement(second, true),
        };
        var lead = Cut(string.IsNullOrWhiteSpace(h.ShortDescription) ? first : h.ShortDescription!.Trim(), 600);
        return new AiGeneratedQuestion(h.HeritageId, QuestionType.MULTI_TRUE_FALSE,
            $"Đọc đoạn mô tả về {h.Name}: {lead} Hãy cho biết mỗi nhận định sau đúng hay sai.",
            "Các nhận định đúng được lấy từ nội dung di sản; nhận định sai nói điều không đúng sự thật.",
            Statements: statements);
    }

    // ---- Đọc tư liệu rồi trả lời ----

    private static AiGeneratedQuestion? SourceReading(AiHeritageInput h, int variant)
    {
        var facts = Facts(h, level1: false);
        if (facts.Count == 0) return null;

        var passage = new List<string>();
        var words = 0;
        foreach (var f in facts)
        {
            var w = LessonText.CountWords(f);
            if (words + w > 120 && passage.Count > 0) break;
            passage.Add(f);
            words += w;
            if (words >= 60) break;
        }
        var correct = passage[variant % passage.Count];
        var source = h.Sources.FirstOrDefault();
        var citation = source is null ? "Cục Di sản văn hóa (dsvh.gov.vn)" : Cut(string.IsNullOrWhiteSpace(source.Url) ? source.Title : $"{source.Title} - {source.Url}", 480);
        return new AiGeneratedQuestion(h.HeritageId, QuestionType.SOURCE_READING,
            $"Theo đoạn tư liệu, câu nào sau đây nêu đúng về {h.Name}?",
            "Câu đúng được trích từ chính đoạn tư liệu; ba câu còn lại không có trong tư liệu và không đúng sự thật.",
            Passage: string.Join(' ', passage), PassageSource: citation,
            Options: Rotate([Option(correct, true), .. FalseStatements(h.Name).Take(3).Select(f => Option(f, false))], variant));
    }

    // ---- Dùng chung ----

    /// <summary>Dấu vân của câu (dạng, di sản, lời câu, hình và toàn bộ đáp án) để không sinh hai câu giống hệt nhau.</summary>
    private static string Signature(AiGeneratedQuestion q) => string.Join('|',
        q.QuestionType, q.HeritageId, q.Content, q.ImageUrl, q.Passage, q.AnswerIsTrue,
        string.Join('~', (q.Options ?? []).Select(o => $"{o.Content}/{o.ImageUrl}/{o.IsCorrect}")),
        string.Join('~', (q.Pairs ?? []).Select(p => $"{p.Left}/{p.LeftImageUrl}/{p.Right}")),
        string.Join('~', q.Steps ?? []),
        string.Join('~', (q.Blanks ?? []).Select(b => string.Join('/', b.Accepted ?? []))),
        string.Join('~', q.Distractors ?? []),
        string.Join('~', (q.Statements ?? []).Select(x => $"{x.Text}/{x.IsTrue}")));

    private static QuestionOption Option(string text, bool correct) => new(Cut(text, QuestionRules.OptionMaxLength), null, correct);

    private static List<T> Rotate<T>(IReadOnlyList<T> items, int by)
    {
        if (items.Count == 0) return [];
        var shift = by % items.Count;
        return items.Skip(shift).Concat(items.Take(shift)).ToList();
    }

    /// <summary>Câu nói sai về di sản (dùng làm phương án gây nhiễu và mệnh đề sai).</summary>
    private static string[] FalseStatements(string name) =>
    [
        $"{name} không có giá trị lịch sử và văn hóa.",
        $"{name} đã bị lãng quên hoàn toàn và không còn được bảo tồn ngày nay.",
        $"{name} chỉ được biết đến ở nước ngoài, không gắn với Việt Nam.",
        $"{name} mới được tạo ra trong những năm gần đây.",
    ];

    /// <summary>Các câu của nội dung di sản đủ dài để làm dữ kiện (25-250 ký tự), theo thứ tự trong bài, không trùng.</summary>
    private static List<string> Facts(AiHeritageInput h, bool level1)
    {
        var text = string.Join(' ', new[] { h.Content, h.ShortDescription }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var sentences = SentenceSplit().Split(text)
            .Select(s => Regex.Replace(s.Trim(), @"\s+", " "))
            .Where(s => s.Length is >= 25 and <= MaxFactLength)
            .Distinct().ToList();
        return level1 ? sentences.Select(s => ShortenWords(s, Level1MaxWords)).Distinct().ToList() : sentences;
    }

    private static string? Fact(AiHeritageInput h, int variant)
    {
        var facts = Facts(h, level1: false);
        return facts.Count == 0 ? null : facts[variant % facts.Count];
    }

    private static string? ShortFact(AiHeritageInput h, int variant)
    {
        var facts = Facts(h, level1: true);
        return facts.Count == 0 ? null : facts[variant % facts.Count];
    }

    private static string ShortenWords(string sentence, int maxWords)
    {
        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= maxWords) return sentence;
        return string.Join(' ', words.Take(maxWords)).TrimEnd(',', ';', ':', '.') + ".";
    }

    private static List<AiImageInput> DistinctImages(AiHeritageInput h) => h.Images.DistinctBy(i => i.Url).ToList();

    /// <summary>Nhãn của thẻ hình: chú thích của ảnh, không có hoặc trùng thì đặt "Hình k"; cắt tối đa 50 ký tự, không trùng nhau.</summary>
    private static List<string> Labels(AiHeritageInput h, List<AiImageInput> cards)
    {
        var labels = new List<string>();
        for (var i = 0; i < cards.Count; i++)
        {
            var label = string.IsNullOrWhiteSpace(cards[i].Caption) ? $"Hình {i + 1} của {h.Name}" : cards[i].Caption!.Trim();
            label = Cut(label, QuestionRules.LabelMaxLength);
            if (labels.Contains(label, StringComparer.OrdinalIgnoreCase)) label = Cut($"Hình {i + 1}: {label}", QuestionRules.LabelMaxLength);
            if (labels.Contains(label, StringComparer.OrdinalIgnoreCase)) label = $"Hình số {i + 1}";
            labels.Add(label);
        }
        return labels;
    }

    private static IEnumerable<string> Words(string text) =>
        text.Split([' ', ',', ';', ':', '.', '!', '?', '(', ')', '"', '\n'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Từ khóa để bỏ trống: tên riêng viết hoa không đứng đầu câu, không có thì từ dài nhất (từ 4 ký tự).</summary>
    private static string? KeyWord(string sentence)
    {
        var words = Words(sentence).ToList();
        var proper = words.Skip(1).FirstOrDefault(w => w.Length >= 4 && char.IsUpper(w[0]) && w.All(char.IsLetter));
        if (proper is not null) return proper;
        return words.Where(w => w.Length >= 4 && w.All(char.IsLetter)).OrderByDescending(w => w.Length).FirstOrDefault();
    }

    private static string ReplaceFirst(string text, string word, string replacement)
    {
        var index = text.IndexOf(word, StringComparison.Ordinal);
        return index < 0 ? text : string.Concat(text.AsSpan(0, index), replacement, text.AsSpan(index + word.Length));
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd();

    [GeneratedRegex(@"(?<=[.!?])\s+|\r?\n+")]
    private static partial Regex SentenceSplit();
}
