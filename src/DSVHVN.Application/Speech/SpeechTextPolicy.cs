using System.Collections.Concurrent;
using DSVHVN.Application.Attempts;
using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DSVHVN.Application.Speech;

/// <summary>
/// Cấu hình quy tắc tạo giọng đọc của máy chủ (cùng mục <c>Tts</c> với nguồn giọng). <see cref="OnlyPublishedContent"/> bật (mặc định) thì máy chủ chỉ tạo giọng mới cho chữ
/// thuộc nội dung đã xuất bản mà người gọi được xem, cùng các câu cố định của khu học cấp 1; tắt thì như trước: mọi người đã đăng nhập, mọi chữ không quá 1000 ký tự.
/// </summary>
public sealed class SpeechAccessOptions
{
    public const string Section = "Tts";

    public bool OnlyPublishedContent { get; set; } = true;
}

/// <summary>
/// Bộ chữ một người được nhờ máy chủ tạo giọng. Chữ được nhận khi trùng đúng một chữ trong bộ (đã chuẩn hóa khoảng trắng), hoặc là một đoạn nằm trong một đoạn nội dung
/// của bài (câu kể chuyện, tóm tắt, chú thích hình, ý chính): web, app và máy chủ tách câu hơi khác nhau nên câu em bấm nghe chỉ cần nằm trong nội dung bài.
/// </summary>
public sealed class AllowedSpeechTexts
{
    public static readonly AllowedSpeechTexts Empty = new([], []);

    private readonly HashSet<string> _exact;
    private readonly string[] _passages;

    public AllowedSpeechTexts(IEnumerable<string?> exact, IEnumerable<string?> passages)
    {
        _exact = exact.Select(SpeechRules.Normalize).OfType<string>().ToHashSet(StringComparer.Ordinal);
        _passages = passages.Select(SpeechRules.Collapse).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Số chữ trùng đúng và số đoạn nội dung trong bộ (để kiểm thử và ghi nhật ký).</summary>
    public int Count => _exact.Count + _passages.Length;

    /// <summary><paramref name="normalized"/> phải đã qua <see cref="SpeechRules.Normalize"/>.</summary>
    public bool Contains(string normalized) =>
        _exact.Contains(normalized) || _passages.Any(passage => passage.Contains(normalized, StringComparison.Ordinal));
}

/// <summary>
/// Dựng bộ chữ được tạo giọng từ nội dung, là quy tắc thuần (không chạm CSDL): mỗi hàm thêm đúng các chữ mà khu học cấp 1 (web học tập và app) đọc to cho phần đó.
/// </summary>
public sealed class AllowedSpeechTextsBuilder
{
    private readonly List<string?> _exact = [];
    private readonly List<string?> _passages = [];

    /// <summary>
    /// Một bài học: các chữ tạo trước lúc xuất bản (<see cref="SpeechWarmUpTexts.ForLesson"/>), cộng các lời đọc nguyên khối của giao diện: tiêu đề phần Kể chuyện
    /// (chú thích hình), chú thích cả đoạn của phần Hình, các ý cần nhớ đọc liền, câu mang về nhà kèm lời nhắc, lời cổ vũ nhắc tên bài; và các đoạn nội dung để so
    /// câu (phần Kể chuyện, phần Hình, tóm tắt khi bài chưa có phần Kể chuyện, từng ý chính), cả bản gốc lẫn bản đã bỏ ký hiệu Markdown.
    /// </summary>
    public AllowedSpeechTextsBuilder AddLesson(
        string? title, string? summary, IReadOnlyCollection<LessonSection> sections, IReadOnlyList<string> keyPoints, string? takeHomeQuestion)
    {
        _exact.AddRange(SpeechWarmUpTexts.ForLesson(sections, keyPoints, takeHomeQuestion));
        foreach (var section in sections.Where(s => s.SectionType is LessonSectionType.STORY or LessonSectionType.IMAGE))
        {
            _exact.Add(section.Title);
            AddPassage(section.Body);
        }
        AddPassage(summary);
        foreach (var point in keyPoints) AddPassage(point);
        if (keyPoints.Count > 0) _exact.Add(LevelOneSpeech.KeyPoints(keyPoints));
        if (!string.IsNullOrWhiteSpace(takeHomeQuestion)) _exact.Add(LevelOneSpeech.TakeHome(takeHomeQuestion));
        if (!string.IsNullOrWhiteSpace(title))
            _exact.Add(LevelOneSpeech.GreetingWith(AttemptRules.LevelOneCheer(0, hasInProgress: false, title, hasLessons: true)));
        return this;
    }

    /// <summary>
    /// Một bài chơi: các chữ tạo trước lúc phát hành (<see cref="SpeechWarmUpTexts.ForQuiz"/>, lời khen theo mọi vị trí vì bước Nhớ lại đánh số câu khác), cộng nội dung
    /// câu nguyên văn (app và bước Nhớ lại đọc riêng lời câu), lời chỉ đáp án theo nhãn phương án khi câu chưa có giải thích, lời gợi ý khi ghép cặp hoặc sắp xếp
    /// đúng một phần, và lời đọc trang giới thiệu bài chơi.
    /// </summary>
    public AllowedSpeechTextsBuilder AddQuiz(IReadOnlyList<SpeechWarmUpTexts.QuestionSource> questions)
    {
        _exact.AddRange(SpeechWarmUpTexts.ForQuiz(questions.Select(q => q with { Position = null })));
        foreach (var question in questions)
        {
            var type = question.Type ?? QuestionType.SINGLE_CHOICE;
            _exact.Add(question.Content);
            if (string.IsNullOrWhiteSpace(question.Explanation)) _exact.AddRange(LevelOneSpeech.RevealWithoutExplanation(type, question.OptionLabels));
            _exact.AddRange(LevelOneSpeech.PartialRetry(type, question.OptionLabels.Count));
        }
        _exact.Add(LevelOneSpeech.QuizIntro(questions.Count, null));
        _exact.Add(LevelOneSpeech.QuizIntro(questions.Count, Messages.QuizPracticeNotice));
        return this;
    }

    /// <summary>Lượt đang làm của em: lời đọc cả câu theo đúng thứ tự câu, phương án, hình và bước đang hiện (<see cref="SpeechWarmUpTexts.ForAttempt"/>).</summary>
    public AllowedSpeechTextsBuilder AddAttempt(IEnumerable<StudentAttemptQuestionDto> questions)
    {
        _exact.AddRange(SpeechWarmUpTexts.ForAttempt(questions));
        return this;
    }

    /// <summary>Một thẻ di sản trong album của em.</summary>
    public AllowedSpeechTextsBuilder AddHeritageCard(string? name, string? shortDescription)
    {
        _exact.AddRange(LevelOneSpeech.AlbumCard(name, shortDescription));
        return this;
    }

    /// <summary>Khung "Lớp của em" và lời cổ vũ nhắc số sao (từ 1 tới <paramref name="maxStars"/>, mỗi bài chơi tối đa 3 sao).</summary>
    public AllowedSpeechTextsBuilder AddClass(string? className, string? teacherName, int maxStars)
    {
        _exact.Add(LevelOneSpeech.ClassCard(className, teacherName));
        _exact.Add(LevelOneSpeech.ClassCard(className, null));
        for (var stars = 1; stars <= maxStars; stars++)
            _exact.Add(LevelOneSpeech.GreetingWith(AttemptRules.LevelOneCheer(stars, hasInProgress: false, null, hasLessons: true)));
        return this;
    }

    public AllowedSpeechTexts Build() => new(_exact, _passages);

    private void AddPassage(string? text)
    {
        _passages.Add(text);
        _passages.Add(PlainText.FromMarkdown(text));
    }
}

/// <summary>
/// Quy tắc "chỉ tạo giọng cho nội dung đã xuất bản" của <c>POST /api/v1/tts</c> và <c>/tts/prefetch</c> (bật bằng <c>Tts:OnlyPublishedContent</c>, mặc định bật).
/// Chữ được tạo giọng mới khi là câu cố định của khu học cấp 1 (<see cref="LevelOneSpeech.FixedPhrases"/>), khi máy chủ vừa viết riêng chữ đó cho người gọi
/// (<see cref="Grant"/>: lời khen ở trang kết quả, lời đọc của lượt đang làm), hoặc khi thuộc bộ chữ của người gọi:
/// <list type="bullet">
/// <item>Học sinh của lớp cấp 1 (khối 1-5), còn hoạt động: các bài học Đã xuất bản, chưa xóa của lớp em (kể cả câu mang về nhà và thẻ album của di sản đã xuất bản),
/// các bài chơi đã phát hành hoặc đã đóng của các bài đó, các lượt em đang làm, khung lớp của em và lời cổ vũ.</item>
/// <item>Học sinh cấp 2, 3, giáo viên, quản trị trường, quản trị hệ thống: bộ rỗng. Giao diện của các vai trò này không xin giọng của máy chủ (xem trước của giáo viên
/// dùng giọng của thiết bị), nên chỉ còn câu cố định và tiếng đã có sẵn.</item>
/// </list>
/// Bộ chữ của mỗi người được nhớ <see cref="AllowedTtl"/> để không truy vấn lại ở mỗi lần bấm nghe. Tiếng đã có trong bộ nhớ đệm vẫn được trả cho mọi người
/// (việc đó ở <see cref="SpeechRequestService"/>), vì đã được tạo từ nội dung trước đó.
/// </summary>
public sealed class SpeechTextPolicy(IAppDbContext db, IMemoryCache cache, IOptions<SpeechAccessOptions> options)
{
    /// <summary>Bộ chữ của một người được nhớ chừng này; bài vừa xuất bản đã được xếp tạo trước nên trong lúc chờ em vẫn nghe được tiếng đã có.</summary>
    public static readonly TimeSpan AllowedTtl = TimeSpan.FromMinutes(3);

    /// <summary>Chữ máy chủ viết riêng cho một người được nhớ chừng này kể từ lần ghi nhận cuối.</summary>
    public static readonly TimeSpan GrantTtl = TimeSpan.FromHours(2);

    /// <summary>Mỗi người giữ tối đa chừng này chữ ghi nhận; đầy thì xóa hết rồi ghi tiếp (lần mở trang sau sẽ ghi lại).</summary>
    public const int GrantMax = 500;

    private static readonly HashSet<string> Fixed =
        LevelOneSpeech.FixedPhrases.Select(SpeechRules.Normalize).OfType<string>().ToHashSet(StringComparer.Ordinal);

    public bool Enabled => options.Value.OnlyPublishedContent;

    /// <summary><paramref name="normalized"/> phải đã qua <see cref="SpeechRules.Normalize"/>.</summary>
    public async Task<bool> IsAllowedAsync(Actor actor, string normalized, CancellationToken ct)
    {
        if (Fixed.Contains(normalized)) return true;
        if (cache.TryGetValue<ConcurrentDictionary<string, byte>>(GrantKey(actor.UserId), out var granted) && granted!.ContainsKey(normalized)) return true;
        return (await AllowedForAsync(actor, ct)).Contains(normalized);
    }

    /// <summary>Bộ chữ của người gọi (nhớ <see cref="AllowedTtl"/>).</summary>
    public async Task<AllowedSpeechTexts> AllowedForAsync(Actor actor, CancellationToken ct)
    {
        var key = AllowedKey(actor.UserId, actor.Role);
        if (cache.TryGetValue<AllowedSpeechTexts>(key, out var known)) return known!;
        var built = await BuildAsync(actor, ct);
        cache.Set(key, built, AllowedTtl);
        return built;
    }

    /// <summary>
    /// Ghi nhận chữ máy chủ vừa viết riêng cho người dùng này để giao diện đọc to (lời khen ở trang kết quả, lời đọc của lượt đang làm): được tạo giọng
    /// trong <see cref="GrantTtl"/> kể từ lần ghi nhận cuối.
    /// </summary>
    public void Grant(long userId, IEnumerable<string?> texts)
    {
        var granted = cache.GetOrCreate(GrantKey(userId), entry =>
        {
            entry.SlidingExpiration = GrantTtl;
            return new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        })!;
        foreach (var text in texts)
        {
            if (SpeechRules.Normalize(text) is not { } normalized) continue;
            if (granted.Count >= GrantMax) granted.Clear();
            granted.TryAdd(normalized, 0);
        }
    }

    private static string AllowedKey(long userId, RoleCode role) => $"speech-allowed:{userId}:{role}";

    private static string GrantKey(long userId) => $"speech-granted:{userId}";

    private async Task<AllowedSpeechTexts> BuildAsync(Actor actor, CancellationToken ct)
    {
        if (actor.Role != RoleCode.STUDENT) return AllowedSpeechTexts.Empty;

        var student = await db.Students.AsNoTracking()
            .Where(s => s.UserId == actor.UserId && s.DeletedAt == null && s.IsActive && s.Class!.DeletedAt == null)
            .Select(s => new { s.Id, s.ClassId, s.Class!.Grade, ClassName = s.Class.Name, TeacherName = s.Class.Teacher!.FullName })
            .SingleOrDefaultAsync(ct);
        if (student is null || SchoolClass.SchoolLevelOf(student.Grade) != 1) return AllowedSpeechTexts.Empty;

        var builder = new AllowedSpeechTextsBuilder();

        // Bài học Đã xuất bản, chưa xóa của lớp em: đúng những bài em mở được bằng mã.
        var lessons = await db.Lessons.AsNoTracking().Include(l => l.Sections)
            .Where(l => l.ClassId == student.ClassId && l.DeletedAt == null && l.Status == ContentStatus.PUBLISHED)
            .ToListAsync(ct);
        var lessonIds = lessons.Select(l => l.Id).ToList();

        // Di sản đã xuất bản của các bài: câu mang về nhà theo di sản đầu tiên của bài (như AlbumService.TakeHomeAsync) và thẻ trong album.
        var heritages = await db.LessonHeritages.AsNoTracking()
            .Where(lh => lessonIds.Contains(lh.LessonId) && lh.Heritage!.DeletedAt == null && lh.Heritage.Status == ContentStatus.PUBLISHED)
            .Select(lh => new { lh.LessonId, lh.HeritageId, lh.Heritage!.Name, lh.Heritage.ShortDescription, lh.Heritage.HeritageType })
            .ToListAsync(ct);
        foreach (var lesson in lessons)
        {
            var first = heritages.Where(h => h.LessonId == lesson.Id).OrderBy(h => h.HeritageId).FirstOrDefault();
            var takeHome = first is null ? null : AlbumRules.TakeHomeQuestion(first.HeritageType, first.Name);
            builder.AddLesson(lesson.Title, lesson.Summary, lesson.Sections, LessonJson.ParseKeyPoints(lesson.KeyPoints), takeHome);
        }
        foreach (var heritage in heritages.DistinctBy(h => h.HeritageId)) builder.AddHeritageCard(heritage.Name, heritage.ShortDescription);

        // Bài chơi em mở được: đã phát hành hoặc đã đóng, chưa xóa, của các bài trên. Thứ tự câu và phương án như lượt làm bài dựng.
        var quizIds = await db.Quizzes.AsNoTracking()
            .Where(q => lessonIds.Contains(q.LessonId) && q.DeletedAt == null && (q.Status == QuizStatus.PUBLISHED || q.Status == QuizStatus.CLOSED))
            .Select(q => q.Id)
            .ToListAsync(ct);
        var questions = await db.QuizQuestions.AsNoTracking()
            .Where(x => quizIds.Contains(x.QuizId))
            .OrderBy(x => x.QuizId).ThenBy(x => x.SortOrder).ThenBy(x => x.QuestionId)
            .Select(x => new
            {
                x.QuizId,
                x.Question!.Content,
                x.Question.Explanation,
                x.Question.QuestionType,
                Labels = x.Question.Answers.OrderBy(a => a.SortOrder ?? int.MaxValue).ThenBy(a => a.Id).Select(a => a.Content).ToList(),
            })
            .ToListAsync(ct);
        foreach (var quiz in questions.GroupBy(q => q.QuizId))
            builder.AddQuiz(quiz.Select(q => new SpeechWarmUpTexts.QuestionSource(q.Content, q.Explanation, q.QuestionType, q.Labels)).ToList());

        // Lượt em đang làm (kể cả của bài vừa lưu trữ): lời đọc cả câu theo thứ tự riêng của lượt.
        var attempts = await db.Attempts.AsNoTracking()
            .Where(a => a.StudentId == student.Id && a.Status == AttemptStatus.IN_PROGRESS)
            .Select(a => new { a.Id, a.QuizId, a.Quiz!.ShuffleQuestions })
            .ToListAsync(ct);
        foreach (var attempt in attempts)
        {
            var items = await AttemptFinisher.LoadQuizQuestionsAsync(db, attempt.QuizId, ct);
            var ordered = QuestionLayout.OrderQuestions(items, attempt.ShuffleQuestions, attempt.Id);
            builder.AddAttempt(ordered.Select((item, i) => AttemptViews.Question(QuestionLayout.Build(item.Question!, attempt.Id), i + 1, null, null)));
        }

        builder.AddClass(student.ClassName, student.TeacherName, maxStars: 3 * quizIds.Count);
        return builder.Build();
    }
}
