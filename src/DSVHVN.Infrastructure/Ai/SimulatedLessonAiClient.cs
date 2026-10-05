using System.Text.RegularExpressions;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Infrastructure.Ai;

/// <summary>
/// BẢN GIẢ LẬP của Dịch vụ AI soạn nội dung bài học (chọn bằng <c>Ai:Mode = Simulated</c>, là mặc định). Dịch vụ AI thật chưa có; bản này
/// thực hiện đúng giao diện <see cref="ILessonAiClient"/> của hợp đồng để toàn bộ luồng (giữ lượt, ghi nhật ký, kiểm kết quả, bản nháp,
/// giáo viên duyệt, xuất bản) chạy được. Không gọi mạng, không gọi nhà cung cấp mô hình nào, không dùng khóa API nào.
/// <para>
/// Cách làm: ghép câu từ chính dữ liệu di sản đã gửi (mô tả ngắn, nội dung, dòng thời gian, nguồn, ảnh) vào khung của cấp, tất định
/// (cùng đầu vào cho cùng đầu ra). Cấp 1: câu chuyện câu ngắn (tối đa 10 từ mỗi câu từ nội dung) chia thành bước, mỗi bước một hình, thêm vài hình
/// kèm câu ngắn. Cấp 2: bài đọc có mô tả, mốc thời gian lấy từ dòng thời gian của di sản, hình có chú thích. Cấp 3: bài phân tích bối cảnh và ý nghĩa,
/// 1-3 đoạn trích nguyên văn từ nội dung di sản kèm nguồn, 3 câu hỏi gợi suy nghĩ. Số từ của nội dung chính nằm trong khoảng của cấp theo độ dài chọn.
/// Chất lượng chữ chỉ ở mức mẫu để thử luồng; bản thật dùng mô hình ngôn ngữ và giáo viên vẫn phải duyệt.
/// </para>
/// </summary>
public sealed partial class SimulatedLessonAiClient : ILessonAiClient
{
    public const string Name = "simulated-lesson-composer";

    // Nhắm tới số từ trong cửa sổ [aim, aim + WindowWords) nằm gọn trong khoảng của cấp, kể cả mức Chi tiết (đã chừa lề so với trần).
    private const int WindowWords = 10;
    private const int Level1SentenceMaxWords = 10;

    public string ModelName => Name;

    public Task<AiComposeResult> ComposeLessonAsync(AiComposeRequest request, string correlationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = request.GradeLevel switch
        {
            1 => ComposeLevel1(request),
            2 => ComposeLevel2(request),
            3 => ComposeLevel3(request),
            _ => throw new AiServiceException("Cấp học phải là 1, 2 hoặc 3.", retryable: false),
        };
        return Task.FromResult(result);
    }

    // ---- Cấp 1: chuyện kể từng bước, mỗi bước một hình ----

    private static AiComposeResult ComposeLevel1(AiComposeRequest request)
    {
        var frame = LessonFrames.Level1;
        var aim = AimWords(frame, request.Length);
        var names = request.Heritages.Select(h => h.Name).ToList();
        var pool = new SentencePool(Facts(request, Level1SentenceMaxWords), Level1Fillers(names), Level1Pad(names[0]));

        var sentences = new List<string>
        {
            names.Count == 1 ? $"Hôm nay em cùng khám phá {names[0]} nhé." : $"Hôm nay em cùng khám phá {JoinNames(names)} nhé.",
        };
        var total = LessonText.CountWords(sentences[0]);
        total = Fill(sentences, total, aim, pool);

        var stepCount = request.Length switch { LessonLength.SHORT => 4, LessonLength.MEDIUM => 5, _ => 6 };
        var steps = Partition(sentences, stepCount);
        var images = request.Heritages.SelectMany(h => h.Images.Select(i => (Heritage: h, Image: i))).ToList();

        var composed = new List<AiComposedStep>();
        for (var i = 0; i < steps.Count; i++)
            composed.Add(new AiComposedStep(LessonSectionType.STORY, steps[i], images.Count == 0 ? null : images[i % images.Count].Image.MediaId, null, null,
                $"Bước {i + 1}"));
        foreach (var (heritage, image) in images.DistinctBy(x => x.Image.MediaId).Take(3))
            composed.Add(new AiComposedStep(LessonSectionType.IMAGE, ImageSentence(heritage, image), image.MediaId, null, null, null));

        var content = string.Join(' ', sentences);
        return Result(request, content, composed, [], KeyPoints(request, 3, Level1SentenceMaxWords));
    }

    // ---- Cấp 2: bài đọc, hình có chú thích, mốc thời gian ----

    private static AiComposeResult ComposeLevel2(AiComposeRequest request)
    {
        var frame = LessonFrames.Level2;
        var aim = AimWords(frame, request.Length);
        var names = request.Heritages.Select(h => h.Name).ToList();

        var sentences = new List<string>
        {
            $"Bài học hôm nay giới thiệu {JoinNames(names)}, những di sản văn hóa tiêu biểu của đất nước ta.",
        };
        var facts = Facts(request, maxWords: 38).ToList();
        var timelineSentences = request.Heritages
            .SelectMany(h => h.Timeline.Select(t => $"Vào {t.DateLabel}, {LowerFirst(t.Name)}.")).ToList();
        var pool = new SentencePool(Interleave(facts, timelineSentences), Level2Fillers(names), Level2Pad(names[0]));
        Fill(sentences, LessonText.CountWords(sentences[0]), aim, pool);

        var composed = new List<AiComposedStep>();
        foreach (var (heritage, image) in request.Heritages.SelectMany(h => h.Images.Select(i => (Heritage: h, Image: i)))
                     .DistinctBy(x => x.Image.MediaId).Take(2))
            composed.Add(new AiComposedStep(LessonSectionType.IMAGE, image.Caption ?? ImageSentence(heritage, image), image.MediaId, null, null, null));

        foreach (var t in PickMilestones(request.Heritages.SelectMany(h => h.Timeline).ToList(), LessonRules.Level2MaxMilestones))
            composed.Add(new AiComposedStep(LessonSectionType.MILESTONE, MilestoneText(t), null, t.StartYear, t.EventId, t.DateLabel));

        return Result(request, string.Join(' ', sentences), composed, [], KeyPoints(request, 5, 30));
    }

    // ---- Cấp 3: phân tích, trích tư liệu gốc, câu hỏi gợi suy nghĩ ----

    private static AiComposeResult ComposeLevel3(AiComposeRequest request)
    {
        var frame = LessonFrames.Level3;
        var aim = AimWords(frame, request.Length);
        var names = request.Heritages.Select(h => h.Name).ToList();

        var sentences = new List<string>
        {
            $"Về bối cảnh, {JoinNames(names)} gắn với đời sống văn hóa, lịch sử và tín ngưỡng của cộng đồng đã sáng tạo và gìn giữ di sản.",
        };
        var pool = new SentencePool(Facts(request, maxWords: 45), Level3Fillers(names), Level3Pad(names[0]));
        Fill(sentences, LessonText.CountWords(sentences[0]), aim, pool);

        var composed = new List<AiComposedStep>();
        var questions = Reflections(names);
        foreach (var q in questions) composed.Add(new AiComposedStep(LessonSectionType.REFLECTION, q, null, null, null, null));

        return Result(request, string.Join(' ', sentences), composed, Quotes(request), KeyPoints(request, 5, 45));
    }

    // ---- Dùng chung ----

    private static int AimWords(LevelFrame frame, LessonLength length) =>
        Math.Min(frame.TargetWords(length), frame.MaxWords - WindowWords);

    private static AiComposeResult Result(AiComposeRequest request, string content, List<AiComposedStep> steps, List<AiSourceQuote> quotes,
        List<string> keyPoints)
    {
        var input = request.Heritages.Sum(h => (h.Content?.Length ?? 0) + (h.ShortDescription?.Length ?? 0)) + request.CurrentContent.Length;
        var output = content.Length + steps.Sum(s => s.Text?.Length ?? 0) + quotes.Sum(q => q.Quote.Length);
        return new AiComposeResult(content, steps, quotes, keyPoints, request.GradeLevel, request.Length, Name,
            new AiUsage(input / 4, output / 4));
    }

    /// <summary>
    /// Thêm câu cho tới khi đủ <paramref name="aim"/> từ nhưng không quá aim + cửa sổ: trước hết câu lấy từ nội dung di sản, rồi câu đệm
    /// ngắn (không bao giờ hết nhờ câu đánh số). Trả tổng số từ.
    /// </summary>
    private static int Fill(List<string> sentences, int total, int aim, SentencePool pool)
    {
        var ceiling = aim + WindowWords;
        foreach (var fact in pool.Facts)
        {
            if (total >= aim) break;
            var w = LessonText.CountWords(fact);
            if (total + w >= ceiling) continue;
            sentences.Add(fact);
            total += w;
        }
        foreach (var filler in pool.Fillers)
        {
            if (total >= aim) break;
            var w = LessonText.CountWords(filler);
            if (total + w >= ceiling) continue;
            sentences.Add(filler);
            total += w;
        }
        for (var n = 1; total < aim; n++)
        {
            var padding = pool.Pad(n);
            var w = LessonText.CountWords(padding);
            if (total + w >= ceiling) padding = "Em hãy ghi nhớ.";
            sentences.Add(padding);
            total += LessonText.CountWords(padding);
        }
        return total;
    }

    /// <summary>
    /// Chia câu thành nhiều nhất <paramref name="parts"/> nhóm liên tiếp, đều nhau nhất có thể: tìm sức chứa (số từ) nhỏ nhất đủ để xếp
    /// vào chừng ấy nhóm rồi xếp tham lam.
    /// </summary>
    private static List<string> Partition(List<string> sentences, int parts)
    {
        var sizes = sentences.Select(LessonText.CountWords).ToList();
        var total = sizes.Sum();
        var capacity = Math.Max((int)Math.Ceiling(total / (double)parts), sizes.Max());
        List<List<int>> groups;
        while (true)
        {
            groups = [];
            var current = new List<int>();
            var words = 0;
            for (var i = 0; i < sizes.Count; i++)
            {
                if (words + sizes[i] > capacity && current.Count > 0)
                {
                    groups.Add(current);
                    current = [];
                    words = 0;
                }
                current.Add(i);
                words += sizes[i];
            }
            if (current.Count > 0) groups.Add(current);
            if (groups.Count <= parts || capacity >= total) break;
            capacity++;
        }
        return groups.Select(g => string.Join(' ', g.Select(i => sentences[i]))).ToList();
    }

    /// <summary>Câu từ mô tả ngắn và nội dung của từng di sản, xen kẽ giữa các di sản, bỏ dấu đánh dấu Markdown; mỗi câu cắt tối đa <paramref name="maxWords"/> từ.</summary>
    private static List<string> Facts(AiComposeRequest request, int maxWords)
    {
        var perHeritage = request.Heritages
            .Select(h => SplitSentences(h.ShortDescription).Concat(SplitSentences(h.Content)).Select(s => Shorten(s, maxWords)).ToList())
            .ToList();
        return Interleave(perHeritage.ToArray());
    }

    private static List<string> Interleave(params List<string>[] lists)
    {
        var result = new List<string>();
        for (var i = 0; lists.Any(l => i < l.Count); i++)
            foreach (var l in lists)
                if (i < l.Count) result.Add(l[i]);
        return result;
    }

    private static IEnumerable<string> SplitSentences(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (Match m in SentencePattern().Matches(text))
        {
            var cleaned = MarkdownMarks().Replace(m.Value, " ");
            cleaned = Spaces().Replace(cleaned, " ").Trim().TrimEnd('.', '!', '?', ';', ':', ' ');
            if (LessonText.CountWords(cleaned) >= 4) yield return cleaned + ".";
        }
    }

    private static string Shorten(string sentence, int maxWords)
    {
        var words = sentence.TrimEnd('.').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= maxWords ? sentence : string.Join(' ', words.Take(maxWords)).TrimEnd(',', ';', ':') + ".";
    }

    private static List<string> KeyPoints(AiComposeRequest request, int count, int maxWords)
    {
        var points = new List<string>();
        foreach (var h in request.Heritages)
            points.Add($"{h.Name} là di sản văn hóa của Việt Nam, cần được gìn giữ.");
        foreach (var fact in Facts(request, maxWords))
        {
            if (points.Count >= count) break;
            points.Add(fact);
        }
        var extras = new[]
        {
            "Gìn giữ di sản là trách nhiệm của mỗi người.",
            "Di sản kể cho chúng ta nghe về lịch sử của quê hương.",
            "Em có thể góp phần bảo vệ di sản bằng những việc làm nhỏ.",
        };
        foreach (var e in extras)
        {
            if (points.Count >= LessonRules.AiMinKeyPoints) break;
            points.Add(e);
        }
        return points.Take(Math.Min(LessonRules.AiMaxKeyPoints, Math.Max(count, LessonRules.AiMinKeyPoints)))
            .Select(p => p.Length > LessonRules.KeyPointMaxLength ? p[..LessonRules.KeyPointMaxLength] : p).ToList();
    }

    /// <summary>Chọn tối đa <paramref name="max"/> mốc trải đều trên dòng thời gian (sắp theo năm); di sản có ít mốc thì lấy hết.</summary>
    private static List<AiTimelineInput> PickMilestones(List<AiTimelineInput> all, int max)
    {
        var sorted = all.OrderBy(t => t.StartYear).ThenBy(t => t.EventId).ToList();
        if (sorted.Count <= max) return sorted;
        return Enumerable.Range(0, max).Select(i => sorted[(int)Math.Round(i * (sorted.Count - 1) / (double)(max - 1))]).Distinct().ToList();
    }

    private static string MilestoneText(AiTimelineInput t)
    {
        var description = SplitSentences(t.Description).FirstOrDefault();
        return description is null ? $"{t.Name}." : $"{t.Name}. {description}";
    }

    private static string ImageSentence(AiHeritageInput heritage, AiImageInput image) =>
        string.IsNullOrWhiteSpace(image.Caption) ? $"Đây là hình ảnh của {heritage.Name}." : $"{image.Caption.Trim().TrimEnd('.')}.";

    /// <summary>Đoạn trích nguyên văn (chuỗi con của nội dung di sản) kèm nguồn: tối đa 3 đoạn, mỗi đoạn một câu từ 8 đến 60 từ.</summary>
    private static List<AiSourceQuote> Quotes(AiComposeRequest request)
    {
        var quotes = new List<AiSourceQuote>();
        var candidates = request.Heritages.Select(h => (Heritage: h, Sentences: QuoteSentences(h.Content).ToList())).ToList();
        for (var round = 0; quotes.Count < LessonRules.Level3MaxExcerpts && round < 3; round++)
            foreach (var (heritage, sentences) in candidates)
            {
                if (quotes.Count >= LessonRules.Level3MaxExcerpts || round >= sentences.Count) continue;
                quotes.Add(new AiSourceQuote(heritage.HeritageId, sentences[round], SourceOf(heritage)));
            }
        return quotes;
    }

    private static IEnumerable<string> QuoteSentences(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) yield break;
        foreach (Match m in SentencePattern().Matches(content))
        {
            var text = m.Value.Trim();
            var words = LessonText.CountWords(text);
            if (words is >= 8 and <= 60 && !text.Contains('#') && !text.Contains('*')) yield return text;
        }
    }

    private static string SourceOf(AiHeritageInput heritage)
    {
        var source = heritage.Sources.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Title));
        if (source is null) return "Cục Di sản văn hóa, dsvh.gov.vn";
        var text = string.IsNullOrWhiteSpace(source.Url) ? source.Title.Trim() : $"{source.Title.Trim()} ({source.Url.Trim()})";
        return text.Length > LessonRules.SourceCitationMaxLength ? text[..LessonRules.SourceCitationMaxLength] : text;
    }

    private static List<string> Reflections(IReadOnlyList<string> names)
    {
        var name = names[0];
        return
        [
            $"Theo em, vì sao {name} cần được gìn giữ cho các thế hệ mai sau?",
            $"Hãy so sánh cách người xưa và người nay gìn giữ {name}. Điều gì giống và khác nhau?",
            "Em có thể làm gì để lan tỏa giá trị của di sản này tới bạn bè và cộng đồng?",
        ];
    }

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} và {names[1]}",
        _ => string.Join(", ", names.Take(names.Count - 1)) + " và " + names[^1],
    };

    private static string LowerFirst(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToLowerInvariant(text[0]) + text[1..].TrimEnd('.');

    // ---- Câu đệm theo cấp (ngắn, không phụ thuộc nội dung) ----

    private static List<string> Level1Fillers(IReadOnlyList<string> names) =>
    [
        $"{names[0]} là báu vật của đất nước ta.",
        "Ông cha ta đã gìn giữ di sản này qua nhiều đời.",
        "Mỗi lần ngắm nhìn, em thấy thật tự hào.",
        "Em cùng các bạn hãy giữ gìn di sản nhé.",
        "Di sản kể cho em nghe chuyện ngày xưa.",
        "Nhiều người đến thăm để học điều hay.",
    ];

    private static Func<int, string> Level1Pad(string name) =>
        n => $"Em nhớ điều thứ {n} về {name}: thật đáng quý.";

    private static List<string> Level2Fillers(IReadOnlyList<string> names) =>
    [
        $"Tìm hiểu {names[0]} giúp em hiểu hơn về lịch sử và văn hóa của dân tộc.",
        "Mỗi di sản đều chứa đựng giá trị lịch sử, văn hóa và tinh thần riêng.",
        "Các em hãy đọc kỹ bài, quan sát hình và chú ý các mốc thời gian.",
        "Việc bảo tồn di sản cần sự chung tay của nhà nước và của cộng đồng.",
        "Hiểu về quá khứ giúp chúng ta trân trọng hiện tại và định hướng tương lai.",
    ];

    private static Func<int, string> Level2Pad(string name) =>
        n => $"Điều cần nhớ thứ {n} về {name}: di sản là tài sản chung của mọi người.";

    private static List<string> Level3Fillers(IReadOnlyList<string> names) =>
    [
        $"Về ý nghĩa, {names[0]} là chứng tích của quá trình hình thành bản sắc văn hóa Việt Nam qua các thời kỳ lịch sử.",
        "Việc nghiên cứu di sản đòi hỏi đối chiếu nhiều nguồn tư liệu và phân tích trong bối cảnh lịch sử cụ thể của từng giai đoạn.",
        "Giá trị của di sản không chỉ nằm ở vật thể hay nghi lễ mà còn ở ký ức, tri thức và cách ứng xử mà cộng đồng truyền lại.",
        "Bảo tồn di sản vì vậy là sự cân bằng giữa gìn giữ giá trị gốc và đáp ứng nhu cầu phát triển của đời sống hiện đại.",
        "Người học cần phân biệt tư liệu gốc với cách diễn giải của các thế hệ sau để rút ra nhận định có căn cứ.",
        "Từ đó, mỗi người có thể xác định trách nhiệm của mình trong việc gìn giữ và lan tỏa các giá trị văn hóa của dân tộc.",
    ];

    private static Func<int, string> Level3Pad(string name) =>
        n => $"Luận điểm thứ {n} về {name} cần được kiểm chứng bằng tư liệu.";

    private sealed record SentencePool(List<string> Facts, List<string> Fillers, Func<int, string> Pad);

    [GeneratedRegex(@"[^.!?\n]+[.!?]?", RegexOptions.CultureInvariant)]
    private static partial Regex SentencePattern();

    [GeneratedRegex(@"[#*_`>\[\]|]+")]
    private static partial Regex MarkdownMarks();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
