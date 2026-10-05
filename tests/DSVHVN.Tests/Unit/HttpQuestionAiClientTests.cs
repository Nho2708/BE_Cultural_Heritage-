using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using DSVHVN.Infrastructure.Ai;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Bản gọi Dịch vụ AI nội bộ qua HTTP cho việc sinh câu hỏi (Ai:Mode = Remote) theo hợp đồng POST /v1/generate-questions, thử với một máy chủ giả:
/// đường dẫn, header khóa nội bộ và mã truy vết, JSON camelCase không lộ dữ liệu cá nhân, đọc đủ các dạng đáp án, phân loại lỗi thử lại được.
/// </summary>
public sealed class HttpQuestionAiClientTests
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

    private static readonly AiQuestionRequest Request = new(
        7, 9, 2, 3, DifficultyLevel.HARD, [QuestionType.SINGLE_CHOICE, QuestionType.FILL_BLANK],
        [new AiHeritageInput(1, "Hoàng thành", "Mô tả", "Nội dung", [new AiTimelineInput(5, "Thời Lý", "Dời đô", null, 1010, "1010")],
            [new AiSourceInput("Quyết định", "https://x/y")], [new AiImageInput(3, "https://x/a.jpg", "Cổng")])]);

    private static HttpQuestionAiClient Client(StubHandler handler, string baseUrl = "http://ai.local:8000/") =>
        new(new HttpClient(handler), Options.Create(new AiOptions { Mode = AiMode.Remote, BaseUrl = baseUrl, InternalKey = "khoa-noi-bo" }));

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };

    [Fact]
    public async Task A_good_response_with_every_answer_shape_is_parsed_and_the_request_follows_the_contract()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(new
        {
            questions = new object[]
            {
                new
                {
                    heritageId = 1, questionType = "SINGLE_CHOICE", content = "Hoàng thành dời đô năm nào?", explanation = "Năm 1010.",
                    options = new[] { new { content = "1010", isCorrect = true }, new { content = "1100", isCorrect = false }, new { content = "1200", isCorrect = false }, new { content = "1300", isCorrect = false } },
                },
                new
                {
                    heritageId = 1, questionType = "FILL_BLANK", content = "Vua ___ dời đô.", explanation = "Lý Thái Tổ.",
                    blanks = new[] { new { accepted = new[] { "Lý Thái Tổ" } } }, distractors = new[] { "Lê Lợi", "Trần Nhân Tông" },
                },
                new
                {
                    heritageId = 1, questionType = "MATCHING", content = "Ghép hình với tên.", explanation = "Theo chú thích.",
                    pairs = new[] { new { left = (string?)null, leftImageUrl = "https://x/a.jpg", right = "Cổng" } },
                },
            },
            model = "mo-hinh-that",
            usage = new { inputTokens = 120, outputTokens = 340 },
        })));

        var result = await Client(handler).GenerateQuestionsAsync(Request, "ma-truy-vet-0001", CancellationToken.None);

        Assert.Equal(("mo-hinh-that", 340, 3), (result.Model, result.Usage.OutputTokens, result.Questions.Count));
        Assert.Equal([true, false, false, false], result.Questions[0].Options!.Select(o => o.IsCorrect));
        Assert.Equal("Lý Thái Tổ", result.Questions[1].Blanks![0].Accepted![0]);
        Assert.Equal(["Lê Lợi", "Trần Nhân Tông"], result.Questions[1].Distractors);
        Assert.Equal("https://x/a.jpg", result.Questions[2].Pairs![0].LeftImageUrl);
        Assert.Equal(QuestionType.MATCHING, result.Questions[2].QuestionType);

        var (sent, body) = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("http://ai.local:8000/v1/generate-questions", sent.RequestUri!.ToString());
        Assert.Equal("khoa-noi-bo", sent.Headers.GetValues("X-Internal-Key").Single());
        Assert.Equal("ma-truy-vet-0001", sent.Headers.GetValues("X-Correlation-Id").Single());

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal((7, 9, 2, 3, "HARD"), (root.GetProperty("generationId").GetInt32(), root.GetProperty("lessonId").GetInt32(),
            root.GetProperty("gradeLevel").GetInt32(), root.GetProperty("count").GetInt32(), root.GetProperty("difficulty").GetString()));
        Assert.Equal(["SINGLE_CHOICE", "FILL_BLANK"], root.GetProperty("types").EnumerateArray().Select(t => t.GetString()!).ToArray());
        Assert.Equal("https://x/a.jpg", root.GetProperty("heritages")[0].GetProperty("images")[0].GetProperty("url").GetString());
        Assert.Contains("Hoàng thành", body);
        foreach (var forbidden in new[] { "email", "student", "teacher", "className", "fullName" })
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
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
    public async Task Error_codes_are_classified_as_retryable_or_not(HttpStatusCode status, bool retryable)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        var error = await Assert.ThrowsAsync<AiServiceException>(() => Client(handler).GenerateQuestionsAsync(Request, "x", CancellationToken.None));
        Assert.Equal(retryable, error.Retryable);
        Assert.Contains(((int)status).ToString(), error.Message);
    }

    [Fact]
    public async Task Network_failures_and_unreadable_bodies_are_retryable()
    {
        var down = new StubHandler((_, _) => throw new HttpRequestException("không nối được"));
        Assert.True((await Assert.ThrowsAsync<AiServiceException>(() => Client(down).GenerateQuestionsAsync(Request, "x", CancellationToken.None))).Retryable);

        var garbage = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("không phải JSON", System.Text.Encoding.UTF8, "application/json") }));
        Assert.True((await Assert.ThrowsAsync<AiServiceException>(() => Client(garbage).GenerateQuestionsAsync(Request, "x", CancellationToken.None))).Retryable);

        var wrongShape = new StubHandler((_, _) => Task.FromResult(Json(new { questions = "không phải mảng" })));
        Assert.True((await Assert.ThrowsAsync<AiServiceException>(() => Client(wrongShape).GenerateQuestionsAsync(Request, "x", CancellationToken.None))).Retryable);
    }

    [Fact]
    public async Task A_base_address_without_a_trailing_slash_works_too()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(new { questions = Array.Empty<object>(), model = "m", usage = new { inputTokens = 1, outputTokens = 1 } })));
        await Client(handler, "http://ai.local:8000").GenerateQuestionsAsync(Request, "x", CancellationToken.None);
        Assert.Equal("http://ai.local:8000/v1/generate-questions", handler.Seen.Single().Request.RequestUri!.ToString());
    }
}
