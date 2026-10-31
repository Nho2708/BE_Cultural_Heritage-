using System.Security.Cryptography;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Rules;

/// <summary>Độ dài nội dung bài do AI soạn: ngắn, vừa, chi tiết. Số từ cụ thể nằm trong khoảng của cấp (<see cref="LevelFrame"/>).</summary>
public enum LessonLength
{
    SHORT,
    MEDIUM,
    DETAILED,
}

/// <summary>Một bước trong luồng học của cấp: khóa cho giao diện và nhãn tiếng Việt.</summary>
public sealed record FlowStep(string Key, string Label);

/// <summary>
/// Khung nội dung bài học của một cấp: khoảng số từ của nội dung chính, ba mức số từ AI nhắm tới, loại phần dùng được
/// và luồng học (thứ tự bước, có nhảy tự do hay đi lần lượt).
/// </summary>
public sealed record LevelFrame(
    int Level,
    string Name,
    int MinWords,
    int MaxWords,
    int ShortWords,
    int MediumWords,
    int DetailedWords,
    IReadOnlyList<LessonSectionType> AllowedTypes,
    string Layout,
    bool SequentialFlow,
    IReadOnlyList<FlowStep> Flow)
{
    public bool Allows(LessonSectionType type) => AllowedTypes.Contains(type);

    public int TargetWords(LessonLength length) => length switch
    {
        LessonLength.SHORT => ShortWords,
        LessonLength.MEDIUM => MediumWords,
        _ => DetailedWords,
    };
}

/// <summary>
/// Hằng số nghiệp vụ của bài học: độ dài theo cột CSDL, số di sản, ý chính, mã bài học, giới hạn nhập sai mã, gọi AI và các
/// giới hạn mềm (chỉ cảnh báo) của khung nội dung theo cấp. Đổi giá trị ở đây là đổi quy tắc nghiệp vụ.
/// </summary>
public static class LessonRules
{
    public const int TitleMaxLength = 255;
    public const int SectionTitleMaxLength = 255;
    public const int ImageUrlMaxLength = 500;
    public const int SourceCitationMaxLength = 500;
    public const int SectionBodyMaxLength = 20_000;
    public const int SummaryMaxLength = 40_000;

    /// <summary>Bài học dựng từ 1-10 di sản đã xuất bản.</summary>
    public const int MinHeritages = 1;
    public const int MaxHeritages = 10;

    /// <summary>1-10 ý chính, mỗi ý tối đa 300 ký tự.</summary>
    public const int MinKeyPoints = 1;
    public const int MaxKeyPoints = 10;
    public const int KeyPointMaxLength = 300;

    /// <summary>Số phần nội dung tối đa của một bài (chặn dữ liệu quá lớn, không phải quy tắc của khung).</summary>
    public const int MaxSections = 60;

    /// <summary>Năm của mốc thời gian: cận dưới như timeline của di sản, cận trên là năm hiện tại.</summary>
    public const int MinMilestoneYear = HeritageRules.MinYear;

    // ---- Giới hạn mềm của khung theo cấp: ngoài khoảng chỉ cảnh báo, không chặn ----
    public const int Level1MinSteps = 3;
    public const int Level1MaxSteps = 6;
    public const int Level1StepMaxWords = 50;
    public const int Level2MinMilestones = 3;
    public const int Level2MaxMilestones = 8;
    public const int Level3MaxExcerpts = 3;
    public const int Level3ExcerptMaxWords = 150;
    public const int Level3MinReflections = 1;
    public const int Level3MaxReflections = 5;

    // ---- Giới hạn nhập sai mã bài học (bộ đếm trong bộ nhớ máy chủ, không có bảng) ----

    /// <summary>Mỗi tài khoản học sinh nhập sai tối đa 10 lần trong 10 phút, vượt thì bị chặn nhập mã 10 phút.</summary>
    public const int MaxWrongCodesPerStudent = 10;
    public static readonly TimeSpan WrongCodeWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan StudentCodeBlock = TimeSpan.FromMinutes(10);

    /// <summary>Mỗi địa chỉ IP nhập sai tối đa 30 lần trong 10 phút (cả lớp thường chung một IP), vượt thì chặn IP đó 15 phút.</summary>
    public const int MaxWrongCodesPerIp = 30;
    public static readonly TimeSpan IpCodeBlock = TimeSpan.FromMinutes(15);

    // ---- Gọi dịch vụ AI ----

    /// <summary>Mỗi lần gọi chờ tối đa 45 giây, thử lại tối đa 2 lần trong cùng một dòng nhật ký AI.</summary>
    public static readonly TimeSpan AiAttemptTimeout = TimeSpan.FromSeconds(45);
    public const int AiMaxRetries = 2;

    /// <summary>
    /// Dòng "đang xử lý" quá lâu (lớn hơn ba lần chờ cộng dự phòng) coi là bị bỏ dở và không tính vào lượt đã dùng,
    /// để một lần máy chủ khởi động lại giữa chừng không làm mất lượt AI của trường mãi mãi.
    /// </summary>
    public static readonly TimeSpan AiStaleProcessing = TimeSpan.FromMinutes(5);

    /// <summary>Ý chính do AI soạn: 3-7 ý.</summary>
    public const int AiMinKeyPoints = 3;
    public const int AiMaxKeyPoints = 7;
}

/// <summary>Khung nội dung và luồng học của ba cấp (khối 1-5, 6-9, 10-12).</summary>
public static class LessonFrames
{
    public static readonly LevelFrame Level1 = new(
        1, "Cấp 1", MinWords: 150, MaxWords: 250, ShortWords: 150, MediumWords: 200, DetailedWords: 250,
        AllowedTypes: [LessonSectionType.STORY, LessonSectionType.IMAGE],
        Layout: "STEP_BY_STEP", SequentialFlow: true,
        Flow:
        [
            new FlowStep("RECALL", "Nhớ lại"),
            new FlowStep("LISTEN_STORY", "Nghe chuyện"),
            new FlowStep("VIEW_IMAGES", "Xem hình"),
            new FlowStep("PLAY", "Chơi"),
            new FlowStep("EARN_CARD", "Nhận thẻ"),
        ]);

    public static readonly LevelFrame Level2 = new(
        2, "Cấp 2", MinWords: 300, MaxWords: 500, ShortWords: 300, MediumWords: 400, DetailedWords: 500,
        AllowedTypes: [LessonSectionType.READING, LessonSectionType.IMAGE, LessonSectionType.MILESTONE],
        Layout: "FREE_STEPS", SequentialFlow: false,
        Flow:
        [
            new FlowStep("READ", "Đọc bài"),
            new FlowStep("TIMELINE", "Xem dòng thời gian"),
            new FlowStep("QUIZ", "Làm bài kiểm tra"),
            new FlowStep("EXPLANATION", "Xem giải thích"),
        ]);

    public static readonly LevelFrame Level3 = new(
        3, "Cấp 3", MinWords: 600, MaxWords: 900, ShortWords: 600, MediumWords: 750, DetailedWords: 900,
        AllowedTypes: [LessonSectionType.READING, LessonSectionType.SOURCE_EXCERPT, LessonSectionType.MILESTONE, LessonSectionType.REFLECTION],
        Layout: "DOCUMENT", SequentialFlow: false,
        Flow:
        [
            new FlowStep("DOCUMENT", "Đọc tài liệu"),
            new FlowStep("TIMED_QUIZ", "Làm bài kiểm tra có giờ"),
            new FlowStep("RESULT_ANALYSIS", "Xem phân tích kết quả"),
        ]);

    public static LevelFrame For(int level) => level switch
    {
        1 => Level1,
        2 => Level2,
        3 => Level3,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Cấp học phải là 1, 2 hoặc 3."),
    };

    public static string LabelOf(LessonLength length) => length switch
    {
        LessonLength.SHORT => "Ngắn",
        LessonLength.MEDIUM => "Vừa",
        _ => "Chi tiết",
    };

    public static string LabelOf(LessonSectionType type) => type switch
    {
        LessonSectionType.STORY => "Bước câu chuyện",
        LessonSectionType.IMAGE => "Hình",
        LessonSectionType.READING => "Đoạn bài đọc",
        LessonSectionType.MILESTONE => "Mốc thời gian",
        LessonSectionType.SOURCE_EXCERPT => "Đoạn trích tư liệu gốc",
        LessonSectionType.REFLECTION => "Câu hỏi gợi suy nghĩ",
        _ => type.ToString(),
    };
}

/// <summary>Số từ của nội dung bài: tách theo khoảng trắng, không phân biệt dấu câu.</summary>
public static class LessonText
{
    public static int CountWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}

/// <summary>
/// Một phần nội dung như quy tắc kiểm xuất bản nhìn thấy (không phụ thuộc thực thể): loại, chữ, hình, năm mốc, nguồn.
/// </summary>
public sealed record LessonSectionView(LessonSectionType Type, string? Body, string? ImageUrl, int? MilestoneYear, string? SourceCitation);

/// <summary>Nội dung bài để kiểm điều kiện xuất bản: tiêu đề, số di sản còn đã xuất bản, tóm tắt, ý chính, các phần.</summary>
public sealed record LessonContent(
    string? Title,
    int PublishedHeritageCount,
    string? Summary,
    IReadOnlyList<string> KeyPoints,
    IReadOnlyList<LessonSectionView> Sections);

/// <summary>Kết quả kiểm: <see cref="Missing"/> chặn xuất bản; <see cref="Warnings"/> chỉ nhắc (độ dài, số bước, số mốc... ngoài khoảng của cấp).</summary>
public sealed record LessonCheck(IReadOnlyList<string> Missing, IReadOnlyList<string> Warnings)
{
    public bool IsReady => Missing.Count == 0;
}

/// <summary>
/// Điều kiện xuất bản bài học và cảnh báo theo khung nội dung của cấp. Thiếu hẳn phần bắt buộc của khung thì chặn xuất bản
/// (cấp 1 không có bước câu chuyện nào hoặc có bước thiếu hình, cấp 2 không có mốc thời gian nào, cấp 3 không có đoạn trích nào
/// hoặc có đoạn trích thiếu nguồn); độ dài, số bước, số mốc, số đoạn trích ngoài khoảng của cấp chỉ cảnh báo để giáo viên quyết định.
/// </summary>
public static class LessonChecks
{
    /// <summary>
    /// Số từ của nội dung chính = số từ của tóm tắt (cả bài kể liền) cộng các đoạn bài đọc thêm. Tiêu đề, ý chính, chú thích,
    /// bước câu chuyện (đã nằm trong cả bài kể liền), mốc, đoạn trích và câu hỏi gợi suy nghĩ không tính.
    /// </summary>
    public static int MainWordCount(string? summary, IEnumerable<LessonSectionView> sections) =>
        LessonText.CountWords(summary) + sections.Where(s => s.Type == LessonSectionType.READING).Sum(s => LessonText.CountWords(s.Body));

    public static LessonCheck Evaluate(int level, LessonContent content)
    {
        var frame = LessonFrames.For(level);
        var missing = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(content.Title)) missing.Add("tiêu đề bài học");
        if (content.PublishedHeritageCount < LessonRules.MinHeritages) missing.Add("ít nhất một di sản đã xuất bản");
        if (string.IsNullOrWhiteSpace(content.Summary)) missing.Add("tóm tắt");
        if (content.KeyPoints.Count is < LessonRules.MinKeyPoints or > LessonRules.MaxKeyPoints
            || content.KeyPoints.Any(k => string.IsNullOrWhiteSpace(k) || k.Trim().Length > LessonRules.KeyPointMaxLength))
            missing.Add($"{LessonRules.MinKeyPoints}-{LessonRules.MaxKeyPoints} ý chính (mỗi ý tối đa {LessonRules.KeyPointMaxLength} ký tự)");

        var inFrame = content.Sections.Where(s => frame.Allows(s.Type)).ToList();
        var outside = content.Sections.Count - inFrame.Count;
        if (outside > 0)
            warnings.Add($"Có {outside} phần thuộc khung của cấp khác nên học sinh lớp cấp {level} sẽ không thấy; hãy xóa hoặc đổi bằng phần của cấp {level}.");

        var words = MainWordCount(content.Summary, inFrame);
        if (words > 0 && (words < frame.MinWords || words > frame.MaxWords))
            warnings.Add($"Nội dung hiện có {words} từ; bài của lớp cấp {level} thường dài {frame.MinWords}-{frame.MaxWords} từ. Bạn vẫn có thể lưu hoặc xuất bản.");

        switch (level)
        {
            case 1:
                CheckLevel1(inFrame, missing, warnings);
                break;
            case 2:
                CheckLevel2(inFrame, missing, warnings);
                break;
            default:
                CheckLevel3(inFrame, missing, warnings);
                break;
        }
        return new LessonCheck(missing, warnings);
    }

    private static void CheckLevel1(List<LessonSectionView> sections, List<string> missing, List<string> warnings)
    {
        var steps = sections.Where(s => s.Type == LessonSectionType.STORY).ToList();
        if (steps.Count == 0) missing.Add("ít nhất một bước câu chuyện");
        else if (steps.Any(s => string.IsNullOrWhiteSpace(s.ImageUrl))) missing.Add("hình cho mỗi bước câu chuyện");

        if (steps.Count > 0 && steps.Count is < LessonRules.Level1MinSteps or > LessonRules.Level1MaxSteps)
            warnings.Add($"Câu chuyện có {steps.Count} bước; cấp 1 thường có {LessonRules.Level1MinSteps}-{LessonRules.Level1MaxSteps} bước.");
        for (var i = 0; i < steps.Count; i++)
        {
            var n = LessonText.CountWords(steps[i].Body);
            if (n > LessonRules.Level1StepMaxWords)
                warnings.Add($"Bước {i + 1} dài {n} từ; mỗi bước nên tối đa {LessonRules.Level1StepMaxWords} từ.");
        }
    }

    private static void CheckLevel2(List<LessonSectionView> sections, List<string> missing, List<string> warnings)
    {
        var milestones = sections.Count(s => s.Type == LessonSectionType.MILESTONE);
        if (milestones == 0) missing.Add("ít nhất một mốc thời gian");
        else if (milestones is < LessonRules.Level2MinMilestones or > LessonRules.Level2MaxMilestones)
            warnings.Add($"Bài có {milestones} mốc thời gian; cấp 2 thường có {LessonRules.Level2MinMilestones}-{LessonRules.Level2MaxMilestones} mốc.");

        if (!sections.Any(s => s.Type == LessonSectionType.IMAGE && !string.IsNullOrWhiteSpace(s.Body)))
            warnings.Add("Bài cấp 2 nên có ít nhất một hình kèm chú thích.");
    }

    private static void CheckLevel3(List<LessonSectionView> sections, List<string> missing, List<string> warnings)
    {
        var excerpts = sections.Where(s => s.Type == LessonSectionType.SOURCE_EXCERPT).ToList();
        if (excerpts.Count == 0) missing.Add("ít nhất một đoạn trích tư liệu gốc");
        else if (excerpts.Any(s => string.IsNullOrWhiteSpace(s.SourceCitation))) missing.Add("nguồn của mỗi đoạn trích");

        if (excerpts.Count > LessonRules.Level3MaxExcerpts)
            warnings.Add($"Bài có {excerpts.Count} đoạn trích; cấp 3 thường có 1-{LessonRules.Level3MaxExcerpts} đoạn.");
        for (var i = 0; i < excerpts.Count; i++)
        {
            var n = LessonText.CountWords(excerpts[i].Body);
            if (n > LessonRules.Level3ExcerptMaxWords)
                warnings.Add($"Đoạn trích {i + 1} dài {n} từ; mỗi đoạn nên tối đa {LessonRules.Level3ExcerptMaxWords} từ.");
        }

        var reflections = sections.Count(s => s.Type == LessonSectionType.REFLECTION);
        if (reflections is < LessonRules.Level3MinReflections or > LessonRules.Level3MaxReflections)
            warnings.Add($"Bài có {reflections} câu hỏi gợi suy nghĩ; cấp 3 thường có {LessonRules.Level3MinReflections}-{LessonRules.Level3MaxReflections} câu.");
    }
}

/// <summary>
/// Mã bài học: tiền tố <c>BH</c> và 6 ký tự lấy ngẫu nhiên bằng bộ sinh số an toàn từ bảng 32 ký tự không có 0, O, 1, I
/// (cùng bảng với mã học sinh). Khi nhận mã người dùng gõ thì bỏ khoảng trắng và dấu gạch, đổi chữ hoa rồi mới so khớp.
/// </summary>
public static class LessonCodes
{
    public const string Prefix = "BH";
    public const int SuffixLength = 6;
    public const int Length = 2 + SuffixLength;

    /// <summary>Mã bị trùng thì sinh mã khác và thử lại tối đa 5 lần.</summary>
    public const int MaxGenerateAttempts = 5;

    public static string NewCode() => NewCode(Prefix);

    /// <summary>Sinh mã 8 ký tự với hai chữ đầu <paramref name="prefix"/> (mã bài học và mã bài kiểm tra dùng chung cách sinh).</summary>
    public static string NewCode(string prefix)
    {
        Span<char> chars = stackalloc char[Length];
        prefix.CopyTo(chars);
        for (var i = prefix.Length; i < Length; i++)
            chars[i] = StudentCredentials.CodeAlphabet[RandomNumberGenerator.GetInt32(StudentCredentials.CodeAlphabet.Length)];
        return new string(chars);
    }

    public static string Normalize(string? input) => StudentCredentials.NormalizeStudentCode(input);

    /// <summary>Mã đã chuẩn hóa có đúng hình dạng <c>BH</c> + 6 ký tự của bảng 32 ký tự hay không.</summary>
    public static bool IsWellFormed(string? normalized) => HasShape(normalized, Prefix);

    /// <summary>Mã đã chuẩn hóa dài 8 ký tự, bắt đầu bằng <paramref name="prefix"/>, phần còn lại thuộc bảng 32 ký tự.</summary>
    public static bool HasShape(string? normalized, string prefix) =>
        normalized is { Length: Length } && normalized.StartsWith(prefix, StringComparison.Ordinal)
        && normalized.AsSpan(prefix.Length).IndexOfAnyExcept(StudentCredentials.CodeAlphabet) < 0;
}
