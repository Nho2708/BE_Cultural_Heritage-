using System.Net;
using DSVHVN.Application.Attempts;
using DSVHVN.Application.Common;
using DSVHVN.Application.Speech;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>Giọng tự host với quy tắc chỉ tạo giọng mới cho nội dung đã xuất bản đang bật (như cấu hình mặc định của sản phẩm).</summary>
public sealed class ContentOnlySpeechFactory : SelfHostedSpeechFactory
{
    protected override bool OnlyPublishedContent => true;
}

/// <summary>
/// Quy tắc chỉ tạo giọng mới cho nội dung đã xuất bản qua HTTP: học sinh lớp cấp 1 được tạo giọng cho chữ của bài chơi lớp em (kể cả lời đọc của lượt đang làm);
/// chữ lạ trả liên kết null với mã 200 và không gọi Dịch vụ AI; tiếng đã có sẵn vẫn trả cho mọi người; tạo trước chỉ xếp chữ được phép.
/// </summary>
public sealed class SpeechContentRuleApiTests(ContentOnlySpeechFactory factory) : IClassFixture<ContentOnlySpeechFactory>
{
    private const string Tts = "/api/v1/tts";

    private readonly PlatformClient _api = new(factory);
    private HttpClient Http => _api.Http;
    private FakeTtsAiHandler Ai => factory.TtsAi;

    private sealed record TtsData(string? AudioUrl);

    private sealed record PrefetchData(int Queued);

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];

    private async Task<string?> SpeakAsync(string text, string token)
    {
        var (res, body) = await Http.PostAsync<TtsData>(Tts, new { text }, token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(body.Success);
        return body.Data!.AudioUrl;
    }

    private Task<bool> IsCachedAsync(string text) =>
        factory.WithDbAsync(db => db.SpeechClips.AnyAsync(c => c.TextHash == SpeechRules.Hash(SpeechRules.Normalize(text)!, SpeechRules.DefaultVoice)));

    private async Task DrainAsync()
    {
        await Eventually.WaitAsync(() => factory.Speech.PendingPrefetch == 0 && factory.Speech.WaitingForAi == 0, "hàng đợi tạo trước trống");
        await Task.Delay(50);
    }

    [Fact]
    public async Task A_level_one_student_gets_voice_for_the_class_quiz_but_not_for_foreign_text_and_cached_voice_is_served_to_anyone()
    {
        var quizzes = new AttemptTestKit(factory);
        var quiz = await quizzes.OpenQuizAsync(1);
        await DrainAsync();
        var adminToken = await _api.AdminTokenAsync();

        // Lời đọc trang giới thiệu bài chơi: thuộc bài chơi của lớp em, chưa tạo trước lúc phát hành, nên được tạo khi em bấm nghe.
        var intro = LevelOneSpeech.QuizIntro(quiz.Questions.Count, Messages.QuizPracticeNotice);
        Assert.NotNull(await SpeakAsync(intro, quiz.StudentToken));
        Assert.True(await IsCachedAsync(intro));

        // Chữ lạ: 200 với liên kết null, không gọi Dịch vụ AI, không lưu gì.
        var foreign = "Một câu không thuộc bài nào " + Unique() + ".";
        Assert.Null(await SpeakAsync(foreign, quiz.StudentToken));
        Assert.Null(await SpeakAsync(foreign, adminToken));
        Assert.Equal(0, Ai.CallsFor(foreign));
        Assert.False(await IsCachedAsync(foreign));

        // Nội dung câu hỏi đã có tiếng từ lúc phát hành: quản trị (không có bộ chữ nào) vẫn nghe được tiếng có sẵn.
        var content = PlainText.FromMarkdown(quiz.Questions[0].Content);
        await Eventually.WaitAsync(() => IsCachedAsync(content), "nội dung câu hỏi đã có tiếng");
        Assert.NotNull(await SpeakAsync(content, adminToken));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_level_two_or_three_student_gets_voice_for_the_main_content_of_the_class_lessons_but_not_for_foreign_text(int level)
    {
        var kit = new LessonTestKit(factory);
        var (school, schoolClass) = await kit.SchoolWithClassAsync(level);
        var heritage = await kit.HeritageAsync("Phố cổ Hội An", events: 3);
        var lesson = await kit.PublishedLessonAsync(school.TeacherToken, schoolClass.Id, [heritage.Id]);
        var student = await kit.StudentTokenAsync(school.TeacherToken, schoolClass.Id);
        await DrainAsync();
        var adminToken = await _api.AdminTokenAsync();

        // Một đoạn nằm trong nội dung chính (web và app tách câu hơi khác máy chủ) chưa được tạo trước, nên được tạo khi em bấm nghe.
        var sentence = SpeechRules.SplitSentences(PlainText.FromMarkdown(lesson.Summary))[0];
        var fragment = string.Join(' ', sentence.Split(' ').Take(6)) + " " + Unique();
        Assert.Null(await SpeakAsync(fragment, student));   // có thêm đuôi lạ: không nằm trong nội dung bài
        var inside = string.Join(' ', sentence.Split(' ').Take(6));
        Assert.False(await IsCachedAsync(inside));
        Assert.NotNull(await SpeakAsync(inside, student));
        Assert.True(await IsCachedAsync(inside));

        // Chữ lạ và học sinh lớp khác cấp: không. Quản trị hệ thống và giáo viên không có bộ chữ nào.
        var foreign = "Một câu không thuộc bài nào " + Unique() + ".";
        Assert.Null(await SpeakAsync(foreign, student));
        Assert.Equal(0, Ai.CallsFor(foreign));
        var other = string.Join(' ', sentence.Split(' ').Skip(1).Take(5));
        Assert.Null(await SpeakAsync(other + " " + Unique(), adminToken));
        Assert.Null(await SpeakAsync(other + " " + Unique(), school.TeacherToken));
    }

    [Fact]
    public async Task The_read_aloud_line_of_a_matching_question_follows_the_order_of_the_student_attempt()
    {
        var quizzes = new AttemptTestKit(factory);
        var quiz = await quizzes.OpenQuizAsync(1, [QuestionType.MATCHING]);
        await DrainAsync();

        // Em đã bấm nghe trước khi bắt đầu lượt: bộ chữ của em đã được nhớ khi chưa có lượt này.
        Assert.Null(await SpeakAsync("Chữ lạ trước lượt " + Unique() + ".", quiz.StudentToken));

        var attempt = await quizzes.StartAsync(quiz.StudentToken, quiz.Id);
        var spoken = SpeechWarmUpTexts.ForAttempt(attempt.Questions).Single();

        // Lời đọc cả câu theo thứ tự riêng của lượt được ghi nhận ngay lúc bắt đầu, không chờ bộ chữ làm mới.
        Assert.NotNull(await SpeakAsync(spoken, quiz.StudentToken));
        await DrainAsync();

        // Học sinh khác cùng lớp chưa có lượt này nhưng tiếng đã có sẵn nên vẫn nghe được.
        var other = await quizzes.AnotherStudentAsync(quiz);
        Assert.NotNull(await SpeakAsync(spoken, other));
    }

    [Fact]
    public async Task Prefetch_queues_only_the_texts_the_caller_may_have_voice_made_for()
    {
        var quizzes = new AttemptTestKit(factory);
        var quiz = await quizzes.OpenQuizAsync(1);
        await DrainAsync();
        var foreign = "Chữ tạo trước lạ " + Unique() + ".";
        var instruction = LevelOneSpeech.Instruction(QuestionType.TRUE_FALSE);
        var before = factory.Prefetch.Batches.Count;

        var (res, body) = await Http.PostAsync<PrefetchData>(Tts + "/prefetch", new { texts = new[] { foreign, instruction } }, quiz.StudentToken);
        await DrainAsync();

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal([instruction], factory.Prefetch.Batches.Skip(before).Single());
        Assert.Equal(0, Ai.CallsFor(foreign));
        Assert.True(body.Data!.Queued <= 1);
    }
}
