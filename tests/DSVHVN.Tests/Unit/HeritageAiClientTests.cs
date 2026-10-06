using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Dịch vụ AI tóm tắt và dịch di sản: bản giả lập (tất định, chỉ dùng chữ đã có, tóm tắt đúng độ dài, giữ mã phần con), bản gọi qua HTTP theo hợp đồng
/// (đường dẫn, khóa nội bộ, phân loại lỗi), và bộ chạy có kiểm kết quả, thử lại, thời gian chờ.
/// </summary>
public sealed class HeritageAiClientTests
{
    private static string Sentences(int count) =>
        string.Join(' ', Enumerable.Range(1, count).Select(i => $"Câu số {i} nói về lịch sử và kiến trúc của di sản này."));

    private static AiHeritageText Vietnamese(int sentences = 60) => new(
        "Hoàng thành Thăng Long",
        "Trung tâm quyền lực của nhiều triều đại.",
        Sentences(sentences),
        [new AiTimelineText(11, "Thời Lý", "Dời đô về Thăng Long"), new AiTimelineText(12, "Thời Trần", null)],
        [new AiEventText(21, 11, "Dời đô", "Năm 1010")],
        [new AiMediaText(31, "Cổng Đoan Môn")]);

    private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // ---- Bản giả lập ----

    [Fact]
    public async Task Simulated_summary_has_a_short_and_a_detailed_version_of_the_expected_length_built_only_from_existing_text()
    {
        var client = new SimulatedHeritageAiClient();
        var source = Vietnamese();

        var result = await client.SummarizeHeritageAsync(new AiSummarizeRequest(1, 2, source), "x", CancellationToken.None);

        Assert.Equal(SimulatedHeritageAiClient.Name, result.Model);
        Assert.InRange(Words(result.ShortText), TranslationRules.ShortSummaryMinWords, TranslationRules.ShortSummaryMaxWords);
        Assert.InRange(Words(result.DetailedText), TranslationRules.DetailedSummaryMinWords, TranslationRules.DetailedSummaryMaxWords);
        Assert.True(Words(result.ShortText) < Words(result.DetailedText));
        Assert.True(result.ShortText.Length <= TranslationRules.SummaryShortMaxLength);
        Assert.StartsWith("Hoàng thành Thăng Long.", result.ShortText);
        Assert.EndsWith(".", result.ShortText);
        // Không thêm dữ kiện mới: mọi từ của tóm tắt đều có trong nội dung gốc.
        var known = new HashSet<string>(
            new[] { source.Name, source.ShortDescription!, source.Content!, "Các giai đoạn lịch sử:", "Thời Lý;", "Thời Trần." }
                .SelectMany(t => t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Select(w => w.Trim('.', ';', ',', ':')));
        foreach (var word in (result.ShortText + " " + result.DetailedText).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains(word.Trim('.', ';', ',', ':'), known);
    }

    [Fact]
    public async Task Simulated_summary_of_short_content_is_not_padded_and_is_deterministic()
    {
        var client = new SimulatedHeritageAiClient();
        var source = new AiHeritageText("Chùa Một Cột", "Ngôi chùa độc đáo ở Hà Nội.", null, [], [], []);

        var first = await client.SummarizeHeritageAsync(new AiSummarizeRequest(1, 2, source), "a", CancellationToken.None);
        var second = await client.SummarizeHeritageAsync(new AiSummarizeRequest(9, 2, source), "b", CancellationToken.None);

        Assert.Equal(first.ShortText, second.ShortText);
        Assert.Equal(first.DetailedText, second.DetailedText);
        Assert.True(Words(first.ShortText) < TranslationRules.ShortSummaryMinWords);
        Assert.Contains("Ngôi chùa độc đáo ở Hà Nội", first.ShortText);
    }

    [Fact]
    public async Task Simulated_summary_without_any_text_is_a_permanent_failure()
    {
        var client = new SimulatedHeritageAiClient();
        var error = await Assert.ThrowsAsync<AiServiceException>(() =>
            client.SummarizeHeritageAsync(new AiSummarizeRequest(1, 2, new AiHeritageText("Tên", null, "  ", [], [], [])), "x", CancellationToken.None));
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task Simulated_translation_keeps_every_id_and_marks_each_part_with_the_target_language()
    {
        var client = new SimulatedHeritageAiClient();
        var summary = new AiSummaryText("Bản ngắn.", "Bản chi tiết.");

        var result = await client.TranslateHeritageAsync(
            new AiTranslateRequest(1, 2, "ko", "vi", Vietnamese(3), "vi", summary), "x", CancellationToken.None);

        Assert.Equal("[ko] Hoàng thành Thăng Long", result.Content.Name);
        Assert.StartsWith("[ko] Trung tâm", result.Content.ShortDescription);
        Assert.Equal([11L, 12L], result.Content.Timelines.Select(t => t.Id));
        Assert.Null(result.Content.Timelines[1].Description);
        Assert.Equal([21L], result.Content.Events.Select(e => e.Id));
        Assert.Equal(("[ko] Cổng Đoan Môn", 31L), (result.Content.Media[0].Caption, result.Content.Media[0].Id));
        Assert.Equal(("[ko] Bản ngắn.", "[ko] Bản chi tiết."), (result.Summary!.ShortText, result.Summary.DetailedText));

        var without = await client.TranslateHeritageAsync(
            new AiTranslateRequest(1, 2, "zh-Hans", "vi", Vietnamese(3), null, null), "x", CancellationToken.None);
        Assert.Null(without.Summary);
        Assert.StartsWith("[zh-Hans]", without.Content.Name);
    }

    [Fact]
    public async Task Simulated_translation_rejects_an_unknown_target_language_without_retry()
    {
        var error = await Assert.ThrowsAsync<AiServiceException>(() => new SimulatedHeritageAiClient().TranslateHeritageAsync(
            new AiTranslateRequest(1, 2, "fr", "vi", Vietnamese(3), null, null), "x", CancellationToken.None));
        Assert.False(error.Retryable);
    }

    // ---- Bản HTTP ----

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return await respond(request, cancellationToken);
        }
    }

    private static HttpHeritageAiClient Http(StubHandler handler) =>
        new(new HttpClient(handler), Options.Create(new AiOptions { Mode = AiMode.Remote, BaseUrl = "http://ai.local:8000/", InternalKey = "khoa-noi-bo" }));

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };

    [Fact]
    public async Task Http_client_posts_to_the_two_contract_paths_with_the_internal_key_and_reads_the_results()
    {
        var handler = new StubHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("summarize-heritage")
            ? Json(new { shortText = "Ngắn", detailedText = "Chi tiết", model = "mo-hinh-that", usage = new { inputTokens = 10, outputTokens = 20 } })
            : Json(new
            {
                content = new
                {
                    name = "Imperial Citadel", shortDescription = "Power centre", content = "Text",
                    timelines = new[] { new { id = 11, name = "Ly", description = (string?)null } },
                    events = Array.Empty<object>(), media = new[] { new { id = 31, caption = "Gate" } },
                },
                summary = new { shortText = "Short", detailedText = "Detail" },
                model = "mo-hinh-that", usage = new { inputTokens = 1, outputTokens = 2 },
            })));
        var client = Http(handler);

        var summary = await client.SummarizeHeritageAsync(new AiSummarizeRequest(5, 6, Vietnamese(3)), "ma-truy-vet", CancellationToken.None);
        var translation = await client.TranslateHeritageAsync(
            new AiTranslateRequest(7, 6, "en", "vi", Vietnamese(3), "vi", new AiSummaryText("Ngắn", "Chi tiết")), "ma-truy-vet", CancellationToken.None);

        Assert.Equal(("Ngắn", "Chi tiết", "mo-hinh-that", 20), (summary.ShortText, summary.DetailedText, summary.Model, summary.Usage.OutputTokens));
        Assert.Equal(("Imperial Citadel", 11L, "Gate", "Short"),
            (translation.Content.Name, translation.Content.Timelines[0].Id, translation.Content.Media[0].Caption, translation.Summary!.ShortText));

        Assert.Equal(["http://ai.local:8000/v1/summarize-heritage", "http://ai.local:8000/v1/translate-heritage"],
            handler.Seen.Select(s => s.Request.RequestUri!.ToString()));
        Assert.All(handler.Seen, s =>
        {
            Assert.Equal(HttpMethod.Post, s.Request.Method);
            Assert.Equal("khoa-noi-bo", s.Request.Headers.GetValues("X-Internal-Key").Single());
            Assert.Equal("ma-truy-vet", s.Request.Headers.GetValues("X-Correlation-Id").Single());
            // Chỉ chữ của di sản: không có thông tin người dùng.
            foreach (var forbidden in new[] { "email", "student", "teacher", "fullName", "address" })
                Assert.DoesNotContain(forbidden, s.Body, StringComparison.OrdinalIgnoreCase);
        });
        using var sent = JsonDocument.Parse(handler.Seen[1].Body);
        Assert.Equal(("en", "vi", "vi"), (sent.RootElement.GetProperty("targetLanguage").GetString(), sent.RootElement.GetProperty("contentLanguage").GetString(),
            sent.RootElement.GetProperty("summaryLanguage").GetString()));
        Assert.Contains("Hoàng thành Thăng Long", handler.Seen[1].Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.UnprocessableEntity, false)]
    public async Task Http_error_codes_are_classified_as_retryable_or_not(HttpStatusCode status, bool retryable)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        var error = await Assert.ThrowsAsync<AiServiceException>(() =>
            Http(handler).SummarizeHeritageAsync(new AiSummarizeRequest(1, 2, Vietnamese(3)), "x", CancellationToken.None));
        Assert.Equal(retryable, error.Retryable);
    }

    [Fact]
    public async Task Http_network_failures_and_unreadable_bodies_are_retryable()
    {
        var down = new StubHandler((_, _) => throw new HttpRequestException("không nối được"));
        Assert.True((await Assert.ThrowsAsync<AiServiceException>(() => Http(down).TranslateHeritageAsync(
            new AiTranslateRequest(1, 2, "en", "vi", Vietnamese(3), null, null), "x", CancellationToken.None))).Retryable);

        var garbage = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("không phải JSON", System.Text.Encoding.UTF8, "application/json") }));
        Assert.True((await Assert.ThrowsAsync<AiServiceException>(() => Http(garbage).SummarizeHeritageAsync(
            new AiSummarizeRequest(1, 2, Vietnamese(3)), "x", CancellationToken.None))).Retryable);
    }

    // ---- Bộ chạy: kiểm kết quả, thử lại, thời gian chờ ----

    private sealed class Flaky(Func<int, Task<AiSummarizeResult>> summarize, Func<int, AiTranslateRequest, Task<AiTranslateResult>>? translate = null) : IHeritageAiClient
    {
        public int Calls;

        public string ModelName => "kiem-thu";

        public Task<AiSummarizeResult> SummarizeHeritageAsync(AiSummarizeRequest request, string correlationId, CancellationToken ct) =>
            summarize(Interlocked.Increment(ref Calls));

        public Task<AiTranslateResult> TranslateHeritageAsync(AiTranslateRequest request, string correlationId, CancellationToken ct) =>
            translate!(Interlocked.Increment(ref Calls), request);
    }

    /// <summary>Dịch vụ AI chậm: chờ tới khi bị hủy, như một cuộc gọi mạng bị cắt bởi thời gian chờ.</summary>
    private sealed class Slow : IHeritageAiClient
    {
        public int Calls;

        public string ModelName => "kiem-thu";

        public async Task<AiSummarizeResult> SummarizeHeritageAsync(AiSummarizeRequest request, string correlationId, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Ok();
        }

        public Task<AiTranslateResult> TranslateHeritageAsync(AiTranslateRequest request, string correlationId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static HeritageAiRunner Runner(IHeritageAiClient client, int retries = 2, double timeoutSeconds = 5) =>
        new(client, Options.Create(new AiOptions { MaxRetries = retries, HeritageAttemptTimeoutSeconds = timeoutSeconds }), NullLogger<HeritageAiRunner>.Instance);

    private static readonly AiSummarizeRequest SummarizeRequest = new(1, 2, Vietnamese(3));

    private static AiSummarizeResult Ok() => new("Ngắn", "Chi tiết", "m", new AiUsage(1, 1));

    [Fact]
    public async Task Runner_retries_retryable_errors_up_to_two_more_times_and_then_succeeds()
    {
        var client = new Flaky(call => call < 3 ? throw new AiServiceException("lỗi tạm", retryable: true) : Task.FromResult(Ok()));
        var attempt = await Runner(client).SummarizeAsync(SummarizeRequest, "x", CancellationToken.None);
        Assert.True(attempt.Succeeded);
        Assert.Equal(3, client.Calls);
    }

    [Fact]
    public async Task Runner_gives_up_after_three_attempts_and_reports_the_last_error()
    {
        var client = new Flaky(_ => throw new AiServiceException("vẫn lỗi", retryable: true));
        var attempt = await Runner(client).SummarizeAsync(SummarizeRequest, "x", CancellationToken.None);
        Assert.False(attempt.Succeeded);
        Assert.Equal("vẫn lỗi", attempt.Error);
        Assert.Equal(3, client.Calls);
    }

    [Fact]
    public async Task Runner_does_not_retry_a_permanent_error()
    {
        var client = new Flaky(_ => throw new AiServiceException("sai khóa", retryable: false));
        var attempt = await Runner(client).SummarizeAsync(SummarizeRequest, "x", CancellationToken.None);
        Assert.False(attempt.Succeeded);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task Runner_turns_a_slow_attempt_into_a_timeout_and_a_network_error_into_a_retry()
    {
        var slow = new Slow();
        var timedOut = await Runner(slow, retries: 1, timeoutSeconds: 0.1).SummarizeAsync(SummarizeRequest, "x", CancellationToken.None);
        Assert.False(timedOut.Succeeded);
        Assert.Contains("thời gian chờ", timedOut.Error);
        Assert.Equal(2, slow.Calls);   // một lần đầu và một lần thử lại, mỗi lần bị cắt ở 0,1 giây

        var network = new Flaky(call => call == 1 ? throw new HttpRequestException("rớt mạng") : Task.FromResult(Ok()));
        Assert.True((await Runner(network).SummarizeAsync(SummarizeRequest, "x", CancellationToken.None)).Succeeded);
        Assert.Equal(2, network.Calls);
    }

    [Fact]
    public async Task Runner_propagates_cancellation_of_the_caller()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Runner(new Flaky(_ => Task.FromResult(Ok()))).SummarizeAsync(SummarizeRequest, "x", cts.Token));
    }

    [Fact]
    public async Task Runner_retries_a_summary_missing_one_of_its_two_versions()
    {
        var client = new Flaky(call => Task.FromResult(call == 1 ? new AiSummarizeResult("Ngắn", "  ", "m", new AiUsage(1, 1)) : Ok()));
        var attempt = await Runner(client).SummarizeAsync(SummarizeRequest, "x", CancellationToken.None);
        Assert.True(attempt.Succeeded);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task Runner_cuts_texts_to_the_column_widths_and_drops_unknown_ids_and_empty_parts()
    {
        var request = new AiTranslateRequest(1, 2, "en", "vi", Vietnamese(3), "vi", new AiSummaryText("Ngắn", "Chi tiết"));
        var client = new Flaky(_ => throw new InvalidOperationException(), (_, _) => Task.FromResult(new AiTranslateResult(
            new AiHeritageText(
                new string('N', 400), new string('M', 1500), "Nội dung",
                [new AiTimelineText(11, " Ly ", "mô tả"), new AiTimelineText(99, "Lạ", null), new AiTimelineText(12, "  ", null), new AiTimelineText(11, "Trùng", null)],
                [new AiEventText(21, 11, "Dời đô", "  ")],
                [new AiMediaText(31, new string('C', 700)), new AiMediaText(77, "Lạ")]),
            new AiSummaryText(new string('S', 2500), "Chi tiết"), "m", new AiUsage(1, 1))));

        var attempt = await Runner(client).TranslateAsync(request, "x", CancellationToken.None);

        var result = attempt.Value!;
        Assert.Equal(TranslationRules.NameMaxLength, result.Content.Name.Length);
        Assert.Equal(TranslationRules.ShortDescriptionMaxLength, result.Content.ShortDescription!.Length);
        Assert.Equal([11L], result.Content.Timelines.Select(t => t.Id));
        Assert.Equal("Ly", result.Content.Timelines[0].Name);
        Assert.Null(result.Content.Events[0].Description);
        Assert.Equal([31L], result.Content.Media.Select(m => m.Id));
        Assert.Equal(TranslationRules.CaptionMaxLength, result.Content.Media[0].Caption.Length);
        Assert.Equal(TranslationRules.SummaryShortMaxLength, result.Summary!.ShortText.Length);
    }

    [Fact]
    public async Task Runner_retries_a_translation_without_a_name_or_without_the_summary_that_was_asked_for()
    {
        var request = new AiTranslateRequest(1, 2, "en", "vi", Vietnamese(3), "vi", new AiSummaryText("Ngắn", "Chi tiết"));
        AiTranslateResult Good() => new(new AiHeritageText("Name", null, null, [], [], []), new AiSummaryText("S", "D"), "m", new AiUsage(1, 1));
        var client = new Flaky(_ => throw new InvalidOperationException(), (call, _) => Task.FromResult(call switch
        {
            1 => Good() with { Content = new AiHeritageText(" ", null, null, [], [], []) },
            2 => Good() with { Summary = null },
            _ => Good(),
        }));

        var attempt = await Runner(client).TranslateAsync(request, "x", CancellationToken.None);

        Assert.True(attempt.Succeeded);
        Assert.Equal(3, client.Calls);
    }
}
