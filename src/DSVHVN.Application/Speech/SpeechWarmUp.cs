using DSVHVN.Application.Attempts;
using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DSVHVN.Application.Speech;

/// <summary>
/// Chọn các đoạn chữ sẽ đọc to ở cấp 1 để sinh giọng trước khi thầy cô xuất bản. Là quy tắc thuần, không chạm CSDL.
/// Chữ đi qua <see cref="PlainText.FromMarkdown"/> trước khi tách câu, giống cách giao diện chuẩn bị chữ để đọc, rồi chuẩn hóa bằng <see cref="SpeechRules.Normalize"/>;
/// đoạn rỗng hoặc dài quá giới hạn bị bỏ, đoạn trùng chỉ giữ một.
/// </summary>
public static class SpeechWarmUpTexts
{
    /// <summary>
    /// Bài học cấp 1: tiêu đề và từng câu của phần Kể chuyện, chú thích của phần Hình, các ý chính và câu mang về nhà.
    /// Các phần khác (bài đọc, mốc thời gian...) không có ở khung cấp 1 nên không đọc.
    /// </summary>
    public static IReadOnlyList<string> ForLesson(IEnumerable<LessonSection> sections, IEnumerable<string> keyPoints, string? takeHomeQuestion)
    {
        var texts = new List<string>();
        foreach (var section in sections.OrderBy(s => s.SortOrder).ThenBy(s => s.Id))
        {
            switch (section.SectionType)
            {
                case LessonSectionType.STORY:
                    texts.Add(PlainText.FromMarkdown(section.Title));
                    texts.AddRange(Sentences(section.Body));
                    break;
                case LessonSectionType.IMAGE:
                    texts.AddRange(Sentences(section.Body));
                    break;
            }
        }
        foreach (var point in keyPoints) texts.AddRange(Sentences(point));
        texts.Add(takeHomeQuestion ?? string.Empty);
        return Clean(texts);
    }

    /// <summary>Một câu hỏi của bài chơi để chọn chữ: nội dung, giải thích, dạng và nhãn chữ của các phương án.</summary>
    public sealed record QuestionSource(string? Content, string? Explanation, QuestionType? Type, IReadOnlyList<string?> OptionLabels);

    /// <summary>Bài chơi của bài cấp 1: nội dung câu hỏi, giải thích và chữ trên thẻ hình (phương án của câu Chọn hình đúng).</summary>
    public static IReadOnlyList<string> ForQuiz(IEnumerable<QuestionSource> questions)
    {
        var texts = new List<string>();
        foreach (var question in questions)
        {
            texts.Add(PlainText.FromMarkdown(question.Content));
            texts.Add(PlainText.FromMarkdown(question.Explanation));
            if (question.Type == QuestionType.IMAGE_CHOICE)
                texts.AddRange(question.OptionLabels.Select(PlainText.FromMarkdown));
        }
        return Clean(texts);
    }

    private static IEnumerable<string> Sentences(string? text) => SpeechRules.SplitSentences(PlainText.FromMarkdown(text));

    private static List<string> Clean(IEnumerable<string> texts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var text in texts)
        {
            if (SpeechRules.Normalize(text) is { } normalized && seen.Add(normalized)) result.Add(normalized);
        }
        return result;
    }
}

/// <summary>
/// Tạo trước giọng đọc lúc thầy cô xuất bản bài của lớp cấp 1 (các câu của phần Kể chuyện, tiêu đề, chú thích hình, ý chính, câu mang về nhà) và lúc phát hành bài chơi
/// của bài cấp 1 (nội dung câu hỏi, giải thích, chữ trên thẻ hình). Chỉ chạy khi giọng tự host đang bật. Việc này chạy sau khi lần xuất bản đã lưu xong: chỉ xếp chữ vào hàng đợi nền,
/// không chờ sinh âm thanh, và mọi lỗi chỉ ghi log để không bao giờ làm hỏng lần xuất bản.
/// </summary>
public sealed class SpeechWarmUp(IAppDbContext db, ISpeechPrefetcher prefetcher, AlbumService album, ILogger<SpeechWarmUp> logger)
{
    public async Task WarmLessonAsync(long lessonId, CancellationToken ct)
    {
        if (!prefetcher.Enabled) return;
        try
        {
            var lesson = await db.Lessons.AsNoTracking().Include(l => l.Sections).Include(l => l.Class)
                .SingleOrDefaultAsync(l => l.Id == lessonId && l.DeletedAt == null, ct);
            if (lesson?.Class is null || SchoolClass.SchoolLevelOf(lesson.Class.Grade) != 1) return;

            var takeHome = await album.TakeHomeAsync(lessonId, ct);
            var texts = SpeechWarmUpTexts.ForLesson(lesson.Sections, LessonJson.ParseKeyPoints(lesson.KeyPoints), takeHome);
            var queued = prefetcher.Enqueue(texts);
            logger.LogInformation("Đã xếp {Queued} đoạn chữ của bài học {LessonId} vào hàng đợi tạo giọng đọc.", queued, lessonId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Không xếp được giọng đọc tạo trước của bài học {LessonId}.", lessonId);
        }
    }

    public async Task WarmQuizAsync(long quizId, CancellationToken ct)
    {
        if (!prefetcher.Enabled) return;
        try
        {
            var quiz = await db.Quizzes.AsNoTracking().Include(q => q.Lesson).ThenInclude(l => l!.Class)
                .SingleOrDefaultAsync(q => q.Id == quizId && q.DeletedAt == null, ct);
            if (quiz?.Lesson?.Class is null || SchoolClass.SchoolLevelOf(quiz.Lesson.Class.Grade) != 1) return;

            var questions = await db.QuizQuestions.AsNoTracking()
                .Where(x => x.QuizId == quizId)
                .Select(x => new
                {
                    x.Question!.Content,
                    x.Question.Explanation,
                    x.Question.QuestionType,
                    Labels = x.Question.Answers.OrderBy(a => a.SortOrder).Select(a => a.Content).ToList(),
                })
                .ToListAsync(ct);
            var texts = SpeechWarmUpTexts.ForQuiz(questions.Select(q =>
                new SpeechWarmUpTexts.QuestionSource(q.Content, q.Explanation, q.QuestionType, q.Labels)));
            var queued = prefetcher.Enqueue(texts);
            logger.LogInformation("Đã xếp {Queued} đoạn chữ của bài kiểm tra {QuizId} vào hàng đợi tạo giọng đọc.", queued, quizId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Không xếp được giọng đọc tạo trước của bài kiểm tra {QuizId}.", quizId);
        }
    }
}
