using DSVHVN.Application.Common;
using DSVHVN.Application.Speech;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Questions;
using DSVHVN.Domain.Quizzes;
using DSVHVN.Domain.Rules;
using DSVHVN.Tests.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Quy tắc chỉ tạo giọng mới cho nội dung đã xuất bản, chạy trên SQLite trong bộ nhớ: học sinh lớp cấp 1 được tạo giọng cho chữ của bài Đã xuất bản và bài chơi
/// của lớp em, cho các câu cố định của khu học cấp 1 và cho chữ máy chủ vừa viết riêng cho em; chữ lạ, bài nháp, bài của lớp khác thì không. Tiếng đã có sẵn vẫn được trả;
/// tắt quy tắc thì như trước.
/// </summary>
public sealed class SpeechTextPolicyTests : IDisposable
{
    private const string StoryBody = "Ngày xưa ở làng Phù Đổng có cậu bé Gióng. Gióng lớn nhanh như thổi! Gióng cưỡi ngựa sắt đánh giặc.";
    private const string ImageCaption = "Tượng Gióng ở đền Sóc. Tượng rất to.";
    private const string Question = "Thánh Gióng cưỡi ngựa sắt.";
    private const string Explanation = "Gióng cưỡi ngựa sắt đánh đuổi giặc Ân.";
    private const string OtherClassSentence = "Chùa Một Cột có hình bông sen.";
    private const string DraftSentence = "Đây là bài nháp chưa xuất bản.";
    private const string Foreign = "Một câu bất kỳ không có trong bài học nào.";

    private readonly ServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private sealed record Fixture(Actor Student, Actor Teacher, Actor OtherStudent, string HeritageName);

    /// <summary>Lớp 3A (cấp 1) có một bài Đã xuất bản kèm bài chơi đã phát hành và một bài nháp; lớp 3B của giáo viên khác có bài riêng.</summary>
    private async Task<Fixture> SetupAsync()
    {
        var org = await _h.AddOrganizationAsync();
        var teacher = await _h.AddUserAsync("gv.lan", RoleCode.TEACHER, org.Id);
        var otherTeacher = await _h.AddUserAsync("gv.minh", RoleCode.TEACHER, org.Id);
        var (classId, otherClassId, heritageId) = await _h.QueryAsync(async db =>
        {
            var mine = new SchoolClass { OrganizationId = org.Id, TeacherId = teacher.Id, Name = "3A", Grade = 3, SchoolYear = "2026-2027" };
            var other = new SchoolClass { OrganizationId = org.Id, TeacherId = otherTeacher.Id, Name = "3B", Grade = 3, SchoolYear = "2026-2027" };
            var heritage = new Heritage
            {
                Name = "Hội Gióng", Slug = "hoi-giong-" + Guid.NewGuid().ToString("N")[..6], Status = ContentStatus.PUBLISHED,
                HeritageType = HeritageType.INTANGIBLE, ShortDescription = "Lễ hội nhớ ơn Thánh Gióng.",
            };
            db.Classes.AddRange(mine, other);
            db.Heritages.Add(heritage);
            await db.SaveChangesAsync();
            return (mine.Id, other.Id, heritage.Id);
        });
        var (account, _) = await _h.AddStudentAsync(org.Id, teacher.Id, "HS3A0001", "Lê Văn Nam");
        var (otherAccount, _) = await _h.AddStudentAsync(org.Id, otherTeacher.Id, "HS3B0001", "Trần Thị Hoa");

        await _h.QueryAsync(async db =>
        {
            var lesson = new Lesson
            {
                TeacherId = teacher.Id, ClassId = classId, Title = "Thánh Gióng", Status = ContentStatus.PUBLISHED, AccessCode = "BH111111",
                KeyPoints = "[\"Gióng là cậu bé làng Phù Đổng\",\"Hội Gióng mở vào mùa xuân\"]",
                Sections =
                [
                    new LessonSection { SectionType = LessonSectionType.STORY, Title = "Cậu bé Gióng", Body = StoryBody, SortOrder = 1 },
                    new LessonSection { SectionType = LessonSectionType.IMAGE, Body = ImageCaption, ImageUrl = "https://upload.wikimedia.org/g.jpg", SortOrder = 2 },
                ],
            };
            var draft = new Lesson
            {
                TeacherId = teacher.Id, ClassId = classId, Title = "Bài nháp", Status = ContentStatus.DRAFT,
                Sections = [new LessonSection { SectionType = LessonSectionType.STORY, Title = "Nháp", Body = DraftSentence, SortOrder = 1 }],
            };
            var otherLesson = new Lesson
            {
                TeacherId = otherTeacher.Id, ClassId = otherClassId, Title = "Chùa Một Cột", Status = ContentStatus.PUBLISHED, AccessCode = "BH222222",
                Sections = [new LessonSection { SectionType = LessonSectionType.STORY, Title = "Chùa", Body = OtherClassSentence, SortOrder = 1 }],
            };
            db.Lessons.AddRange(lesson, draft, otherLesson);
            await db.SaveChangesAsync();
            db.LessonHeritages.Add(new LessonHeritage { LessonId = lesson.Id, HeritageId = heritageId });

            var question = new Question
            {
                HeritageId = heritageId, CreatedBy = teacher.Id, Content = Question, QuestionType = QuestionType.TRUE_FALSE, Explanation = Explanation,
                Answers = [new Answer { Content = "Đúng", IsCorrect = true, SortOrder = 1 }, new Answer { Content = "Sai", SortOrder = 2 }],
            };
            db.Questions.Add(question);
            var quiz = new Quiz { LessonId = lesson.Id, CreatedBy = teacher.Id, Name = "Bài chơi Gióng", Status = QuizStatus.PUBLISHED, AccessCode = "KT111111" };
            db.Quizzes.Add(quiz);
            await db.SaveChangesAsync();
            db.QuizQuestions.Add(new QuizQuestion { QuizId = quiz.Id, QuestionId = question.Id, SortOrder = 1, Point = 1 });
            await db.SaveChangesAsync();
            return 0;
        });

        return new Fixture(
            new Actor(account.Id, RoleCode.STUDENT, org.Id, "127.0.0.1"),
            new Actor(teacher.Id, RoleCode.TEACHER, org.Id, "127.0.0.1"),
            new Actor(otherAccount.Id, RoleCode.STUDENT, org.Id, "127.0.0.1"),
            "Hội Gióng");
    }

    private Task<bool> AllowedAsync(Actor actor, string text) =>
        _h.RunAsync(sp => sp.GetRequiredService<SpeechTextPolicy>().IsAllowedAsync(actor, SpeechRules.Normalize(text)!, CancellationToken.None));

    // ---- Bộ chữ của học sinh ----

    [Fact]
    public async Task A_level_one_student_may_have_voice_made_for_the_published_lesson_of_the_class_as_the_screens_read_it()
    {
        var f = await SetupAsync();

        // Từng câu như máy chủ tách, một đoạn web hoặc app tách khác, tiêu đề, chú thích cả đoạn, các ý cần nhớ đọc liền, câu mang về nhà kèm lời nhắc.
        Assert.True(await AllowedAsync(f.Student, "Ngày xưa ở làng Phù Đổng có cậu bé Gióng."));
        Assert.True(await AllowedAsync(f.Student, "Gióng lớn nhanh như thổi!"));
        Assert.True(await AllowedAsync(f.Student, "  Gióng cưỡi ngựa   sắt đánh giặc. "));
        Assert.True(await AllowedAsync(f.Student, "Cậu bé Gióng"));
        Assert.True(await AllowedAsync(f.Student, ImageCaption));
        Assert.True(await AllowedAsync(f.Student, "Gióng là cậu bé làng Phù Đổng. Hội Gióng mở vào mùa xuân"));
        var takeHome = AlbumRules.TakeHomeQuestion(HeritageType.INTANGIBLE, f.HeritageName);
        Assert.True(await AllowedAsync(f.Student, LevelOneSpeech.TakeHome(takeHome)));
        // Lời chào nhắc tên bài và thẻ album của di sản của bài.
        Assert.True(await AllowedAsync(f.Student, "Chào em! Hôm nay mình học bài “Thánh Gióng” nhé!"));
        Assert.True(await AllowedAsync(f.Student, "Hội Gióng. Lễ hội nhớ ơn Thánh Gióng."));
    }

    [Fact]
    public async Task A_level_one_student_may_have_voice_made_for_the_quiz_question_feedback_and_intro()
    {
        var f = await SetupAsync();

        Assert.True(await AllowedAsync(f.Student, Question));
        Assert.True(await AllowedAsync(f.Student, LevelOneSpeech.Question(QuestionType.TRUE_FALSE, Question, ["Đúng", "Sai"], [], [])));
        foreach (var feedback in LevelOneSpeech.Feedback(Explanation, null)) Assert.True(await AllowedAsync(f.Student, feedback));
        Assert.True(await AllowedAsync(f.Student, "Bài chơi có 1 câu. " + Messages.QuizPracticeNotice));
    }

    [Fact]
    public async Task Text_that_is_not_in_the_content_of_the_class_is_refused()
    {
        var f = await SetupAsync();

        Assert.False(await AllowedAsync(f.Student, Foreign));
        Assert.False(await AllowedAsync(f.Student, DraftSentence));
        Assert.False(await AllowedAsync(f.Student, OtherClassSentence));
        // Ghép hai câu không liền nhau trong bài thì không còn là một đoạn của bài.
        Assert.False(await AllowedAsync(f.Student, "Ngày xưa ở làng Phù Đổng có cậu bé Gióng. Gióng cưỡi ngựa sắt đánh giặc."));
    }

    [Fact]
    public async Task A_student_of_another_class_cannot_have_voice_made_for_this_class_lesson()
    {
        var f = await SetupAsync();

        Assert.False(await AllowedAsync(f.OtherStudent, "Gióng lớn nhanh như thổi!"));
        Assert.True(await AllowedAsync(f.OtherStudent, OtherClassSentence));
    }

    [Fact]
    public async Task Teachers_only_get_the_fixed_level_one_phrases()
    {
        var f = await SetupAsync();

        Assert.False(await AllowedAsync(f.Teacher, "Gióng lớn nhanh như thổi!"));
        Assert.True(await AllowedAsync(f.Teacher, "Em chạm Đúng hoặc Sai."));
        Assert.True(await AllowedAsync(f.Student, "Đúng rồi! Em nhớ bài giỏi lắm!"));
    }

    [Fact]
    public async Task Text_the_server_wrote_for_the_student_is_allowed_after_it_is_granted()
    {
        var f = await SetupAsync();
        var praise = LevelOneSpeech.ResultSpeech(2, AttemptRules.Praise(2, [new("Hội Gióng", true), new("Hội Gióng", false)]));
        Assert.False(await AllowedAsync(f.Student, praise));

        await _h.RunAsync(sp =>
        {
            sp.GetRequiredService<SpeechTextPolicy>().Grant(f.Student.UserId, [praise]);
            return Task.CompletedTask;
        });

        Assert.True(await AllowedAsync(f.Student, praise));
        Assert.False(await AllowedAsync(f.OtherStudent, praise));
    }

    // ---- Luồng một yêu cầu nghe ----

    /// <summary>Nguồn giọng giả: biết sẵn vài chữ đã có tiếng và đếm số lần được nhờ tạo giọng mới.</summary>
    private sealed class FakeTts(params string[] cached) : ITtsService
    {
        public List<string> Synthesized { get; } = [];

        public Task<string?> FindCachedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(cached.Contains(SpeechRules.Normalize(text)) ? "/files/da-co.mp3" : null);

        public Task<string?> SynthesizeAsync(string text, CancellationToken ct = default)
        {
            Synthesized.Add(text);
            return Task.FromResult<string?>("/files/moi.mp3");
        }
    }

    private sealed class FakePrefetcher : ISpeechPrefetcher
    {
        public List<string> Queued { get; } = [];

        public bool Enabled => true;

        public int Enqueue(IEnumerable<string?> texts)
        {
            var list = texts.OfType<string>().ToList();
            Queued.AddRange(list);
            return list.Count;
        }
    }

    private Task<T> WithServiceAsync<T>(FakeTts tts, FakePrefetcher prefetcher, bool onlyPublished, Func<SpeechRequestService, Task<T>> action) =>
        _h.RunAsync(sp =>
        {
            var policy = new SpeechTextPolicy(
                sp.GetRequiredService<IAppDbContext>(), sp.GetRequiredService<IMemoryCache>(),
                Options.Create(new SpeechAccessOptions { OnlyPublishedContent = onlyPublished }));
            return action(new SpeechRequestService(tts, prefetcher, policy, NullLogger<SpeechRequestService>.Instance));
        });

    [Fact]
    public async Task Lesson_text_is_generated_foreign_text_gets_a_null_link_and_cached_text_is_served_to_anyone()
    {
        var f = await SetupAsync();
        var tts = new FakeTts(Foreign);
        var prefetcher = new FakePrefetcher();

        var lesson = await WithServiceAsync(tts, prefetcher, true, s => s.SynthesizeAsync(f.Student, "Gióng lớn nhanh như thổi!", CancellationToken.None));
        var refused = await WithServiceAsync(tts, prefetcher, true, s => s.SynthesizeAsync(f.Student, DraftSentence, CancellationToken.None));
        var cached = await WithServiceAsync(tts, prefetcher, true, s => s.SynthesizeAsync(f.Teacher, Foreign, CancellationToken.None));

        Assert.Equal("/files/moi.mp3", lesson);
        Assert.Null(refused);
        Assert.Equal("/files/da-co.mp3", cached);
        Assert.Equal(["Gióng lớn nhanh như thổi!"], tts.Synthesized);
    }

    [Fact]
    public async Task Prefetch_queues_only_the_allowed_texts()
    {
        var f = await SetupAsync();
        var prefetcher = new FakePrefetcher();

        var queued = await WithServiceAsync(new FakeTts(), prefetcher, true,
            s => s.PrefetchAsync(f.Student, [Question, Foreign, " ", null, OtherClassSentence, "Em chạm Đúng hoặc Sai."], CancellationToken.None));

        Assert.Equal(2, queued);
        Assert.Equal([Question, "Em chạm Đúng hoặc Sai."], prefetcher.Queued);
    }

    [Fact]
    public async Task With_the_rule_off_any_signed_in_user_gets_voice_for_any_text_as_before()
    {
        var f = await SetupAsync();
        var tts = new FakeTts();
        var prefetcher = new FakePrefetcher();

        var url = await WithServiceAsync(tts, prefetcher, false, s => s.SynthesizeAsync(f.Teacher, Foreign, CancellationToken.None));
        var queued = await WithServiceAsync(tts, prefetcher, false, s => s.PrefetchAsync(f.OtherStudent, [Foreign, DraftSentence], CancellationToken.None));

        Assert.Equal("/files/moi.mp3", url);
        Assert.Equal([Foreign], tts.Synthesized);
        Assert.Equal(2, queued);
    }
}
