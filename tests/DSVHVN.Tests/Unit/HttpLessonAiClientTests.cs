using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Ai;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Bản gọi Dịch vụ AI nội bộ qua HTTP (Ai:Mode = Remote) theo hợp đồng POST /v1/compose-lesson, thử với một máy chủ giả: đường dẫn, header khóa nội bộ
/// và mã truy vết, JSON camelCase, phân loại lỗi thử lại được và không thử lại được, kiểm cấu hình khi khởi động.
/// </summary>
public sealed class HttpLessonAiClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return await respond(request, cancellationToken);
        }
    }

    private static readonly AiComposeRequest Request = new(
        7, 9, 2, LessonLength.MEDIUM,
        [new AiHeritageInput(1, "Hoàng thành", "Mô tả", "Nội dung", [new AiTimelineInput(5, "Thời Lý", "Dời đô", null, 1010, "1010")],
            [new AiSourceInput("Quyết định", "https://x/y")], [new AiImageInput(3, "https://x/a.jpg", "Cổng")])],
        "Nội dung hiện có");

    private static HttpLessonAiClient Client(StubHandler handler, string baseUrl = "http://ai.local:8000/") =>
        new(new HttpClient(handler), Options.Create(new AiOptions { Mode = AiMode.Remote, BaseUrl = baseUrl, InternalKey = "khoa-noi-bo" }));

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };

    [Fact]
    public async Task A_good_response_is_parsed_and_the_request_follows_the_contract()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(new
        {
            content = "Nội dung bài.",
            steps = new object[] { new { kind = "MILESTONE", text = "Dời đô", year = 1010, eventId = 5, title = "1010" } },
            sourceQuotes = Array.Empty<object>(),
            keyPoints = new[] { "Ý 1", "Ý 2", "Ý 3" },
            gradeLevel = 2,
            length = "MEDIUM",
            model = "mo-hinh-that",
            usage = new { inputTokens = 120, outputTokens = 340 },
        })));

        var result = await Client(handler).ComposeLessonAsync(Request, "ma-truy-vet-0001", CancellationToken.None);

        Assert.Equal(("Nội dung bài.", 2, LessonLength.MEDIUM, "mo-hinh-that", 340), (result.Content, result.GradeLevel, result.Length, result.Model, result.Usage.OutputTokens));
        var step = Assert.Single(result.Steps);
        Assert.Equal((LessonSectionType.MILESTONE, 1010, 5L), (step.Kind, step.Year, step.EventId));

        var (sent, body) = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("http://ai.local:8000/v1/compose-lesson", sent.RequestUri!.ToString());
        Assert.Equal("khoa-noi-bo", sent.Headers.GetValues("X-Internal-Key").Single());
        Assert.Equal("ma-truy-vet-0001", sent.Headers.GetValues("X-Correlation-Id").Single());

        // JSON camelCase, enum viết chữ, tiếng Việt giữ nguyên dấu, đúng các trường của hợp đồng.
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal((7, 9, 2, "MEDIUM", "Nội dung hiện có"),
            (root.GetProperty("generationId").GetInt32(), root.GetProperty("lessonId").GetInt32(), root.GetProperty("gradeLevel").GetInt32(),
                root.GetProperty("length").GetString(), root.GetProperty("currentContent").GetString()));
        var heritage = root.GetProperty("heritages")[0];
        Assert.Equal("Hoàng thành", heritage.GetProperty("name").GetString());
        Assert.Equal(1010, heritage.GetProperty("timeline")[0].GetProperty("startYear").GetInt32());
        Assert.Equal("https://x/a.jpg", heritage.GetProperty("images")[0].GetProperty("url").GetString());
        Assert.Contains("Hoàng thành", body);                       // không bị escape \uXXXX
        // Không có trường nào mang dữ liệu cá nhân.
        // (teacherNote là yêu cầu thêm của giáo viên, không phải thông tin cá nhân.)
        var withoutNote = body.Replace("teacherNote", string.Empty);
        foreach (var forbidden in new[] { "email", "student", "teacher", "className", "fullName" })
            Assert.DoesNotContain(forbidden, withoutNote, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.UnprocessableEntity, false)]
    public async Task Server_and_rate_errors_can_be_retried_but_client_errors_cannot(HttpStatusCode status, bool retryable)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));

        var ex = await Assert.ThrowsAsync<AiServiceException>(() => Client(handler).ComposeLessonAsync(Request, "x", CancellationToken.None));

        Assert.Equal(retryable, ex.Retryable);
        Assert.Contains(((int)status).ToString(), ex.Message);
    }

    [Fact]
    public async Task Network_failures_and_unreadable_bodies_can_be_retried()
    {
        var down = new StubHandler((_, _) => throw new HttpRequestException("connection refused"));
        var ex = await Assert.ThrowsAsync<AiServiceException>(() => Client(down).ComposeLessonAsync(Request, "x", CancellationToken.None));
        Assert.True(ex.Retryable);

        var garbage = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("không phải json") }));
        ex = await Assert.ThrowsAsync<AiServiceException>(() => Client(garbage).ComposeLessonAsync(Request, "x", CancellationToken.None));
        Assert.True(ex.Retryable);

        var wrongShape = new StubHandler((_, _) => Task.FromResult(Json(new { content = 5, steps = "x" })));
        ex = await Assert.ThrowsAsync<AiServiceException>(() => Client(wrongShape).ComposeLessonAsync(Request, "x", CancellationToken.None));
        Assert.True(ex.Retryable);

        var empty = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json") }));
        ex = await Assert.ThrowsAsync<AiServiceException>(() => Client(empty).ComposeLessonAsync(Request, "x", CancellationToken.None));
        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task The_wait_time_set_by_the_caller_cancels_the_call()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // HttpClient hủy theo token của người gọi; lỗi hủy đi thẳng lên cho LessonAiService phân biệt với hủy của người dùng.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).ComposeLessonAsync(Request, "x", cts.Token));
    }

    [Fact]
    public async Task A_base_url_without_a_trailing_slash_works_too()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<AiServiceException>(() => Client(handler, "http://ai.local:8000").ComposeLessonAsync(Request, "x", CancellationToken.None));

        Assert.Equal("http://ai.local:8000/v1/compose-lesson", handler.Seen.Single().Request.RequestUri!.ToString());
    }

    // ---- Kiểm cấu hình ----

    private static ValidateOptionsResult Validate(AiOptions options) => new AiOptionsValidator().Validate(null, options);

    [Fact]
    public void The_default_configuration_is_the_simulated_mode_with_the_contract_wait_and_retry_values()
    {
        var options = new AiOptions();

        Assert.True(Validate(options).Succeeded);
        Assert.Equal((AiMode.Simulated, 45d, 2), (options.Mode, options.AttemptTimeoutSeconds, options.MaxRetries));
        Assert.Equal(TimeSpan.FromSeconds(45), options.AttemptTimeout);
    }

    [Fact]
    public void Remote_mode_needs_an_absolute_base_url_and_an_internal_key_and_names_what_is_missing()
    {
        var missing = Validate(new AiOptions { Mode = AiMode.Remote });
        Assert.True(missing.Failed);
        Assert.Contains("Ai:BaseUrl", missing.FailureMessage);
        Assert.Contains("Ai:InternalKey", missing.FailureMessage);

        Assert.True(Validate(new AiOptions { Mode = AiMode.Remote, BaseUrl = "/relative", InternalKey = "k" }).Failed);
        Assert.True(Validate(new AiOptions { Mode = AiMode.Remote, BaseUrl = "ftp://ai.local", InternalKey = "k" }).Failed);
        Assert.True(Validate(new AiOptions { Mode = AiMode.Remote, BaseUrl = "http://ai.local:8000", InternalKey = "k" }).Succeeded);
        Assert.True(Validate(new AiOptions { Mode = AiMode.Simulated, BaseUrl = "", InternalKey = "" }).Succeeded);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(-1, 2)]
    [InlineData(45, -1)]
    [InlineData(45, 6)]
    public void Wait_and_retry_values_must_be_reasonable(double seconds, int retries) =>
        Assert.True(Validate(new AiOptions { AttemptTimeoutSeconds = seconds, MaxRetries = retries }).Failed);

    // ---- Soạn bài có yêu cầu thêm, và viết lại một phần ----

    [Fact]
    public async Task The_teacher_note_of_the_compose_request_goes_out_as_teacherNote()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<AiServiceException>(() =>
            Client(handler).ComposeLessonAsync(Request with { TeacherNote = "Nhấn mạnh kiến trúc; dùng ví dụ gần gũi" }, "x", CancellationToken.None));

        using var json = JsonDocument.Parse(handler.Seen.Single().Body);
        Assert.Equal("Nhấn mạnh kiến trúc; dùng ví dụ gần gũi", json.RootElement.GetProperty("teacherNote").GetString());
        Assert.Equal(string.Empty, JsonDocument.Parse(JsonSerializer.Serialize(Request, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .RootElement.GetProperty("teacherNote").GetString());
    }

    private static readonly AiRewriteSectionRequest RewriteRequest = new(
        7, 9, 2, LessonSectionType.MILESTONE, "Vua dời đô về Thăng Long.", "1010", 1010, "Ngắn hơn", "Nội dung chính của bài.",
        [new AiHeritageInput(1, "Hoàng thành", "Mô tả", "Nội dung", [], [], [])]);

    [Fact]
    public async Task A_good_rewrite_response_is_parsed_and_the_request_follows_the_contract()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(new
        {
            text = "Năm 1010, vua Lý dời đô.",
            title = (string?)null,
            model = "mo-hinh-that",
            usage = new { inputTokens = 80, outputTokens = 20 },
        })));

        var result = await Client(handler).RewriteSectionAsync(RewriteRequest, "ma-truy-vet-0002", CancellationToken.None);

        Assert.Equal(("Năm 1010, vua Lý dời đô.", (string?)null, "mo-hinh-that", 20), (result.Text, result.Title, result.Model, result.Usage.OutputTokens));
        var (sent, body) = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("http://ai.local:8000/v1/rewrite-section", sent.RequestUri!.ToString());
        Assert.Equal("khoa-noi-bo", sent.Headers.GetValues("X-Internal-Key").Single());
        Assert.Equal("ma-truy-vet-0002", sent.Headers.GetValues("X-Correlation-Id").Single());

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal((7, 9, 2, "MILESTONE", "Vua dời đô về Thăng Long."),
            (root.GetProperty("generationId").GetInt32(), root.GetProperty("lessonId").GetInt32(), root.GetProperty("gradeLevel").GetInt32(),
                root.GetProperty("kind").GetString(), root.GetProperty("currentText").GetString()));
        Assert.Equal(("1010", 1010, "Ngắn hơn", "Nội dung chính của bài."),
            (root.GetProperty("title").GetString(), root.GetProperty("year").GetInt32(), root.GetProperty("teacherNote").GetString(),
                root.GetProperty("lessonContent").GetString()));
        Assert.Equal("Hoàng thành", root.GetProperty("heritages")[0].GetProperty("name").GetString());
        Assert.Contains("Vua dời đô về Thăng Long", body);          // không bị escape \uXXXX
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.UnprocessableEntity, false)]
    public async Task Rewrite_errors_are_classified_like_compose_errors(HttpStatusCode status, bool retryable)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));

        var ex = await Assert.ThrowsAsync<AiServiceException>(() => Client(handler).RewriteSectionAsync(RewriteRequest, "x", CancellationToken.None));

        Assert.Equal(retryable, ex.Retryable);
    }

    [Fact]
    public async Task A_rewrite_response_with_the_wrong_shape_or_no_body_can_be_retried()
    {
        var wrongShape = new StubHandler((_, _) => Task.FromResult(Json(new { text = 5, usage = "x" })));
        Assert.True((await Assert.ThrowsAsync<AiServiceException>(() => Client(wrongShape).RewriteSectionAsync(RewriteRequest, "x", CancellationToken.None))).Retryable);

        var empty = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json") }));
        Assert.True((await Assert.ThrowsAsync<AiServiceException>(() => Client(empty).RewriteSectionAsync(RewriteRequest, "x", CancellationToken.None))).Retryable);

        var down = new StubHandler((_, _) => throw new HttpRequestException("connection refused"));
        Assert.True((await Assert.ThrowsAsync<AiServiceException>(() => Client(down).RewriteSectionAsync(RewriteRequest, "x", CancellationToken.None))).Retryable);
    }
}
