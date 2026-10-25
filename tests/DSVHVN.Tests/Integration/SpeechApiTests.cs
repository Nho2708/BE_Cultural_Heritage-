using System.Net;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Speech;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure;
using DSVHVN.Infrastructure.Persistence;
using DSVHVN.Infrastructure.Speech;
using DSVHVN.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DSVHVN.Tests.Integration;

/// <summary>Bộ ghi lại mọi đợt chữ được xếp vào hàng đợi tạo trước (từ nút nghe của giao diện và từ lúc xuất bản), rồi chuyển cho hàng đợi thật.</summary>
public sealed class RecordingPrefetcher : ISpeechPrefetcher
{
    private readonly object _lock = new();
    private readonly List<List<string>> _batches = [];

    /// <summary>Hàng đợi thật; gán khi Api dựng dịch vụ lần đầu.</summary>
    public ISpeechPrefetcher? Inner { get; set; }

    public bool Enabled => Inner?.Enabled ?? false;

    public IReadOnlyList<IReadOnlyList<string>> Batches
    {
        get
        {
            lock (_lock) return _batches.Select(b => (IReadOnlyList<string>)b.ToList()).ToList();
        }
    }

    public IEnumerable<string> AllTexts => Batches.SelectMany(b => b);

    public int Enqueue(IEnumerable<string?> texts)
    {
        var list = texts.Where(t => t is not null).Select(t => t!).ToList();
        lock (_lock) _batches.Add(list);
        return Inner?.Enqueue(list) ?? 0;
    }
}

/// <summary>
/// Api chạy giọng tự host với Dịch vụ AI giả (<see cref="TtsAi"/>) và bộ ghi hàng đợi tạo trước (<see cref="Prefetch"/>). Tác vụ nền tạo trước ghi CSDL song song với các yêu cầu,
/// nên CSDL là một tệp SQLite (mỗi lượt truy cập một kết nối riêng như SQL Server) thay cho kết nối trong bộ nhớ dùng chung của <see cref="ApiFactory"/>.
/// </summary>
public class SelfHostedSpeechFactory : ApiFactory
{
    public const string InternalKey = "khoa-noi-bo-kiem-thu";

    private readonly string _dbFile = Path.Combine(Path.GetTempPath(), "dsvhvn_kiem_thu_giong_" + Guid.NewGuid().ToString("N")[..12] + ".db");

    public FakeTtsAiHandler TtsAi { get; } = new();

    public RecordingPrefetcher Prefetch { get; } = new();

    public SelfHostedTtsService Speech => Services.GetRequiredService<SelfHostedTtsService>();

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
    [
        new("Tts:Provider", "SelfHosted"),
        new("Tts:WaitSeconds", "10"),
        new("Tts:PrefetchRetrySeconds", "0.01"),
        new("Ai:BaseUrl", "http://ai.kiem-thu:8000"),
        new("Ai:InternalKey", InternalKey),
    ];

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbFile, _dbFile + "-wal", _dbFile + "-shm", _dbFile + "-journal" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Thư mục tạm của máy phát triển.
            }
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbFile}"));
            services.AddKeyedSingleton<HttpMessageHandler>(SpeechServiceKeys.AiHandler, TtsAi);
            services.Replace(ServiceDescriptor.Singleton<ISpeechPrefetcher>(sp =>
            {
                Prefetch.Inner = sp.GetRequiredService<SelfHostedTtsService>();
                return Prefetch;
            }));
        });
    }
}

/// <summary>Api với <c>Tts:Provider = None</c>.</summary>
public sealed class NoSpeechFactory : ApiFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [new("Tts:Provider", "None")];
}

/// <summary>
/// Giọng tự host qua HTTP: nghe ngay (sai rồi trúng, tệp phục vụ ở /files, 503 trả liên kết null), đồng thời một lần sinh, tạo trước (202, kiểm lỗi, tới khi đã có đệm),
/// và tạo trước lúc xuất bản bài học và bài chơi của lớp cấp 1 (lớp cấp 2 thì không).
/// </summary>
public sealed class SpeechSelfHostedApiTests(SelfHostedSpeechFactory factory) : IClassFixture<SelfHostedSpeechFactory>
{
    private const string Tts = "/api/v1/tts";

    private readonly PlatformClient _api = new(factory);
    private HttpClient Http => _api.Http;
    private FakeTtsAiHandler Ai => factory.TtsAi;

    private sealed record TtsData(string? AudioUrl);

    private sealed record PrefetchData(int Queued);

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];

    private static string Key(string normalizedText) => StoredFileRules.SpeechClipKey(SpeechRules.DefaultVoice, SpeechRules.Hash(normalizedText, SpeechRules.DefaultVoice));

    private async Task<string?> SpeakAsync(string text, string? token = null)
    {
        token ??= await _api.AdminTokenAsync();
        var (res, body) = await Http.PostAsync<TtsData>(Tts, new { text }, token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(body.Success);
        return body.Data!.AudioUrl;
    }

    private static void AssertQueued(IReadOnlyCollection<string> queued, string? raw)
    {
        // Chữ rỗng thì không có gì để xếp hàng.
        if (SpeechRules.Normalize(PlainText.FromMarkdown(raw)) is { } normalized) Assert.Contains(normalized, queued);
    }

    private Task<bool> IsCachedAsync(string normalizedText) =>
        factory.WithDbAsync(db => db.SpeechClips.AnyAsync(c => c.TextHash == SpeechRules.Hash(normalizedText, SpeechRules.DefaultVoice)));

    /// <summary>Chờ hàng đợi tạo trước trống và Dịch vụ AI rảnh, để lúc ghi tiếp không chạm việc ghi của tác vụ nền (kiểm thử dùng chung một kết nối SQLite).</summary>
    private async Task DrainAsync()
    {
        await Eventually.WaitAsync(() => factory.Speech.PendingPrefetch == 0 && factory.Speech.WaitingForAi == 0, "hàng đợi tạo trước trống");
        await Task.Delay(50);
    }

    // ---- Nghe ngay ----

    [Fact]
    public async Task A_miss_is_generated_stored_and_served_from_files_and_the_second_request_does_not_call_the_ai_service()
    {
        var text = "Hội Gióng là lễ hội lớn. " + Unique();

        var first = await SpeakAsync(text);

        Assert.Equal("/files/" + Key(text), first);
        var call = Assert.Single(Ai.Calls, c => c.Text == text);
        Assert.Equal((SpeechRules.DefaultVoice, SelfHostedSpeechFactory.InternalKey, "/v1/tts"), (call.Voice, call.InternalKey, call.Path));

        var file = await Http.GetAsync(first);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("audio/mpeg", file.Content.Headers.ContentType?.MediaType);
        Assert.True(NarrationRules.IsMp3(await file.Content.ReadAsByteArrayAsync()));
        Assert.Contains("immutable", file.Headers.CacheControl?.ToString());

        Assert.Equal(first, await SpeakAsync("  " + text.Replace(" ", "   ") + " "));
        Assert.Equal(1, Ai.CallsFor(text));
    }

    [Fact]
    public async Task Empty_text_and_text_over_the_limit_return_a_null_link_without_calling_the_ai_service()
    {
        var before = Ai.Calls.Count;

        Assert.Null(await SpeakAsync("   "));
        Assert.Null(await SpeakAsync(new string('a', SpeechRules.TextMaxLength + 1)));

        Assert.Equal(before, Ai.Calls.Count);
    }

    [Fact]
    public async Task When_the_ai_service_is_not_ready_the_link_is_null_with_status_200_and_the_next_try_works()
    {
        var text = "Mô hình đang nạp. " + Unique();
        Ai.Override = (t, nth) => t == text && nth == 1 ? FakeTtsAiHandler.Unavailable() : null;

        Assert.Null(await SpeakAsync(text));
        Assert.False(await IsCachedAsync(text));

        Assert.Equal("/files/" + Key(text), await SpeakAsync(text));
        Assert.Equal(2, Ai.CallsFor(text));
    }

    [Fact]
    public async Task Concurrent_requests_for_the_same_text_make_one_call_to_the_ai_service()
    {
        var text = "Nhiều em bấm nghe cùng lúc. " + Unique();
        var token = await _api.AdminTokenAsync();
        Ai.Hold(text);

        var requests = Enumerable.Range(0, 6).Select(_ => SpeakAsync(text, token)).ToList();
        await Eventually.WaitAsync(() => Ai.CallsFor(text) == 1, "lời gọi đầu tới Dịch vụ AI");
        await Task.Delay(300);   // để các yêu cầu còn lại kịp tới và cùng chờ trên lần sinh đó
        Ai.Release(text);
        var urls = await Task.WhenAll(requests);

        Assert.All(urls, u => Assert.Equal("/files/" + Key(text), u));
        Assert.Equal(1, Ai.CallsFor(text));
    }

    // ---- Tạo trước ----

    [Fact]
    public async Task Anonymous_prefetch_returns_401()
    {
        var (res, _) = await Http.PostAsync<object>(Tts + "/prefetch", new { texts = new[] { "Xin chào" } });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Prefetch_returns_202_with_the_number_queued_and_the_texts_become_cached_in_the_background()
    {
        var token = await _api.AdminTokenAsync();
        var a = "Câu tạo trước một. " + Unique();
        var b = "Câu tạo trước hai. " + Unique();

        var (res, body) = await Http.PostAsync<PrefetchData>(Tts + "/prefetch", new { texts = new[] { a, "", "   ", "  " + a + " ", b } }, token);

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.True(body.Success);
        Assert.Equal(2, body.Data!.Queued);
        await Eventually.WaitAsync(async () => await IsCachedAsync(a) && await IsCachedAsync(b), "hai đoạn đã có đệm");
        await DrainAsync();

        // Nghe ngay đoạn đã tạo trước: có sẵn, không gọi Dịch vụ AI nữa.
        Assert.Equal("/files/" + Key(a), await SpeakAsync(a, token));
        Assert.Equal(1, Ai.CallsFor(a));
        Assert.Equal(1, Ai.CallsFor(b));
    }

    [Fact]
    public async Task Prefetching_text_that_is_already_cached_does_not_call_the_ai_service_again()
    {
        var token = await _api.AdminTokenAsync();
        var text = "Đã nghe rồi. " + Unique();
        await SpeakAsync(text, token);

        var (res, body) = await Http.PostAsync<PrefetchData>(Tts + "/prefetch", new { texts = new[] { text } }, token);
        await DrainAsync();

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal(1, body.Data!.Queued);
        Assert.Equal(1, Ai.CallsFor(text));
    }

    [Fact]
    public async Task Prefetch_with_no_texts_or_only_blank_texts_queues_nothing()
    {
        var token = await _api.AdminTokenAsync();

        foreach (var texts in new object?[] { Array.Empty<string>(), new[] { "", "   ", "\n" }, null })
        {
            var (res, body) = await Http.PostAsync<PrefetchData>(Tts + "/prefetch", new { texts }, token);
            Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
            Assert.Equal(0, body.Data!.Queued);
        }
    }

    [Fact]
    public async Task Prefetch_accepts_40_texts_and_refuses_41_or_a_text_over_1000_characters()
    {
        var token = await _api.AdminTokenAsync();
        var unique = Unique();

        var (ok, _) = await Http.PostAsync<PrefetchData>(Tts + "/prefetch",
            new { texts = Enumerable.Range(1, SpeechRules.PrefetchMaxTexts).Select(i => $"Đoạn {i} lô {unique}.") }, token);
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);

        var (many, manyBody) = await Http.PostAsync<PrefetchData>(Tts + "/prefetch",
            new { texts = Enumerable.Range(1, SpeechRules.PrefetchMaxTexts + 1).Select(i => $"Đoạn {i}.") }, token);
        Assert.Equal(HttpStatusCode.BadRequest, many.StatusCode);
        Assert.False(manyBody.Success);
        Assert.Contains("texts", manyBody.Errors!.Keys);

        var (long1, _) = await Http.PostAsync<PrefetchData>(Tts + "/prefetch", new { texts = new[] { new string('a', SpeechRules.TextMaxLength + 1) } }, token);
        Assert.Equal(HttpStatusCode.BadRequest, long1.StatusCode);

        // Đúng 1000 ký tự sau khi cắt hai đầu thì được nhận.
        var (edge, edgeBody) = await Http.PostAsync<PrefetchData>(Tts + "/prefetch", new { texts = new[] { " " + new string('b', SpeechRules.TextMaxLength) + " " } }, token);
        Assert.Equal(HttpStatusCode.Accepted, edge.StatusCode);
        Assert.Equal(1, edgeBody.Data!.Queued);
        await DrainAsync();
    }

    // ---- Tạo trước lúc xuất bản ----

    [Fact]
    public async Task Publishing_a_lesson_of_a_grade_2_class_queues_its_story_sentences_titles_captions_key_points_and_the_take_home_question()
    {
        var kit = new LessonTestKit(factory);
        var (school, _) = await kit.SchoolWithClassAsync(1);
        var grade2 = await kit.Classroom.CreateClassAsync(school.TeacherToken, "Lớp 2A", 2);
        var heritage = await kit.HeritageAsync("Hội Gióng");
        var before = factory.Prefetch.Batches.Count;

        var lesson = await kit.PublishedLessonAsync(school.TeacherToken, grade2.Id, [heritage.Id]);

        var queued = factory.Prefetch.Batches.Skip(before).SelectMany(b => b).ToList();
        var stories = lesson.Sections.Where(s => s.Type == LessonSectionType.STORY).ToList();
        Assert.NotEmpty(stories);
        foreach (var story in stories)
        {
            AssertQueued(queued, story.Title);
            foreach (var sentence in SpeechRules.SplitSentences(PlainText.FromMarkdown(story.Body))) AssertQueued(queued, sentence);
        }
        foreach (var caption in lesson.Sections.Where(s => s.Type == LessonSectionType.IMAGE))
            foreach (var sentence in SpeechRules.SplitSentences(PlainText.FromMarkdown(caption.Body))) AssertQueued(queued, sentence);
        foreach (var point in lesson.KeyPoints) AssertQueued(queued, point);
        Assert.DoesNotContain(queued, t => t == "");
        var takeHome = AlbumRules.TakeHomeQuestion(heritage.HeritageType, heritage.Name);
        Assert.Contains(takeHome, queued);

        // Đến cùng: máy chủ tạo giọng trước cho câu mang về nhà (tên di sản của bài là duy nhất).
        await Eventually.WaitAsync(() => Ai.CallsFor(takeHome) == 1, "câu mang về nhà được gửi sang Dịch vụ AI");
        await Eventually.WaitAsync(() => IsCachedAsync(takeHome), "câu mang về nhà đã có đệm");
        await DrainAsync();
    }

    [Fact]
    public async Task Publishing_a_lesson_of_a_grade_8_class_queues_nothing()
    {
        var kit = new LessonTestKit(factory);
        var (school, grade8) = await kit.SchoolWithClassAsync(2);
        var heritage = await kit.HeritageAsync("Phố cổ Hội An", events: 3);
        var before = factory.Prefetch.Batches.Count;
        var callsBefore = Ai.Calls.Count;

        await kit.PublishedLessonAsync(school.TeacherToken, grade8.Id, [heritage.Id]);

        Assert.Equal(before, factory.Prefetch.Batches.Count);
        await Task.Delay(200);
        Assert.Equal(callsBefore, Ai.Calls.Count);
    }

    [Fact]
    public async Task Republishing_an_already_published_lesson_does_not_queue_again()
    {
        var kit = new LessonTestKit(factory);
        var (school, grade3) = await kit.SchoolWithClassAsync(1);
        var heritage = await kit.HeritageAsync("Đình làng Đình Bảng");
        var lesson = await kit.PublishedLessonAsync(school.TeacherToken, grade3.Id, [heritage.Id]);
        await DrainAsync();
        var before = factory.Prefetch.Batches.Count;

        await kit.PublishAsync(school.TeacherToken, lesson.Id);

        Assert.Equal(before, factory.Prefetch.Batches.Count);
    }

    [Fact]
    public async Task Publishing_a_quiz_of_a_grade_3_lesson_queues_each_question_and_explanation_but_a_grade_8_quiz_queues_nothing()
    {
        var quizzes = new AttemptTestKit(factory);

        var before = factory.Prefetch.Batches.Count;
        var levelOne = await quizzes.OpenQuizAsync(1);
        var queuedForQuiz = factory.Prefetch.Batches.Skip(before).Last();
        foreach (var question in levelOne.Questions)
        {
            AssertQueued(queuedForQuiz, question.Content);
            AssertQueued(queuedForQuiz, question.Explanation);
            if (question.QuestionType == QuestionType.IMAGE_CHOICE)
                foreach (var option in question.Options) AssertQueued(queuedForQuiz, option.Content);
        }
        await DrainAsync();

        var middle = factory.Prefetch.Batches.Count;
        await quizzes.OpenQuizAsync(2);
        await DrainAsync();
        Assert.Equal(middle, factory.Prefetch.Batches.Count);
    }
}

/// <summary>Các nguồn giọng khác không đổi hành vi: <c>None</c> luôn trả liên kết null và không nhận tạo trước; mặc định (FPT.AI, chưa có khóa) cũng trả null và tạo trước nhận 0.</summary>
public sealed class SpeechOtherProvidersApiTests(NoSpeechFactory none, ApiFactory fptAi) : IClassFixture<NoSpeechFactory>, IClassFixture<ApiFactory>
{
    private sealed record TtsData(string? AudioUrl);

    private sealed record PrefetchData(int Queued);

    [Fact]
    public async Task With_provider_none_the_link_is_null_and_prefetch_accepts_but_queues_nothing()
    {
        var api = new PlatformClient(none);
        var token = await api.AdminTokenAsync();

        var (speak, speakBody) = await api.Http.PostAsync<TtsData>("/api/v1/tts", new { text = "Xin chào" }, token);
        Assert.Equal(HttpStatusCode.OK, speak.StatusCode);
        Assert.Null(speakBody.Data!.AudioUrl);

        var (prefetch, prefetchBody) = await api.Http.PostAsync<PrefetchData>("/api/v1/tts/prefetch", new { texts = new[] { "Xin chào" } }, token);
        Assert.Equal(HttpStatusCode.Accepted, prefetch.StatusCode);
        Assert.Equal(0, prefetchBody.Data!.Queued);
        Assert.False(none.Services.GetRequiredService<ISpeechPrefetcher>().Enabled);
    }

    [Fact]
    public async Task With_the_default_provider_fpt_ai_the_link_is_null_without_a_key_and_prefetch_queues_nothing()
    {
        var api = new PlatformClient(fptAi);
        var token = await api.AdminTokenAsync();

        var (speak, speakBody) = await api.Http.PostAsync<TtsData>("/api/v1/tts", new { text = "Xin chào" }, token);
        Assert.Equal(HttpStatusCode.OK, speak.StatusCode);
        Assert.Null(speakBody.Data!.AudioUrl);
        Assert.IsType<FptAiTtsService>(fptAi.Services.GetRequiredService<ITtsService>());

        var (prefetch, prefetchBody) = await api.Http.PostAsync<PrefetchData>("/api/v1/tts/prefetch", new { texts = new[] { "Xin chào" } }, token);
        Assert.Equal(HttpStatusCode.Accepted, prefetch.StatusCode);
        Assert.Equal(0, prefetchBody.Data!.Queued);
        Assert.False(fptAi.Services.GetRequiredService<ISpeechPrefetcher>().Enabled);
    }

    [Fact]
    public async Task Publishing_a_level_one_lesson_with_the_default_provider_still_works_and_queues_nothing()
    {
        var kit = new LessonTestKit(fptAi);
        var (school, grade3) = await kit.SchoolWithClassAsync(1);
        var heritage = await kit.HeritageAsync("Hát xoan Phú Thọ");

        var lesson = await kit.PublishedLessonAsync(school.TeacherToken, grade3.Id, [heritage.Id]);

        Assert.Equal(ContentStatus.PUBLISHED, lesson.Status);
    }
}
