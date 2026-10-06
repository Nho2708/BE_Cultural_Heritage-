using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Recommendations;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Xếp hạng di sản liên quan: bản giả lập tính bằng bộ độ tương đồng, người gọi đặt thời gian chờ 2 giây và tự tính dự phòng khi Dịch vụ AI lỗi hoặc quá giờ,
/// hủy từ người gọi không bị nuốt; bản gọi qua HTTP theo hợp đồng và không gửi dữ liệu cá nhân.
/// </summary>
public sealed class RelatedRankerTests
{
    private static HeritageFeatures H(long id, HeritageType type, int province, string name = "Di sản") =>
        new(id, name, type, province, province == 1 ? "Bắc" : "Nam", province == 1 ? RecognitionLevel.NATIONAL : RecognitionLevel.PROVINCIAL, null, null, name);

    private static RelatedRequest Request(IReadOnlyList<HeritageFeatures>? candidates = null) => new(
        7, 2, DifficultyLevel.MEDIUM,
        [new WeightedHeritage(H(1, HeritageType.TANGIBLE, 1, "Hoàng thành"), 1d)],
        candidates ?? [H(2, HeritageType.TANGIBLE, 1, "Văn Miếu"), H(3, HeritageType.INTANGIBLE, 40, "Ca trù"), H(4, HeritageType.NATURAL, 1, "Vịnh")]);

    private sealed class FakeClient(Func<RelatedRequest, CancellationToken, Task<IReadOnlyList<RelatedHeritage>>> behavior) : IRecommendationAiClient
    {
        public Task<IReadOnlyList<RelatedHeritage>> RankRelatedAsync(RelatedRequest request, CancellationToken ct) => behavior(request, ct);
    }

    private static RelatedRanker Ranker(IRecommendationAiClient client) => new(client, NullLogger<RelatedRanker>.Instance);

    [Fact]
    public async Task The_simulated_service_ranks_by_similarity_and_leaves_out_heritages_with_nothing_in_common()
    {
        var ranker = Ranker(new SimulatedRecommendationAiClient());
        var result = await ranker.RankAsync(Request(), CancellationToken.None);

        Assert.False(result.UsedFallback);
        Assert.Equal([2L, 4L], result.Scores.Keys.Order());            // Ca trù khác loại, khác tỉnh: không có điểm chung
        Assert.True(result.Of(2)!.Score > result.Of(4)!.Score);        // cùng loại và cùng tỉnh hơn chỉ cùng tỉnh
        Assert.Contains("Hoàng thành", result.Of(2)!.Reason);
    }

    [Fact]
    public async Task With_no_references_or_no_candidates_nothing_is_called_and_the_result_is_empty()
    {
        var calls = 0;
        var ranker = Ranker(new FakeClient((_, _) => { calls++; return Task.FromResult<IReadOnlyList<RelatedHeritage>>([]); }));

        Assert.Same(RankedRelated.Empty, await ranker.RankAsync(Request() with { References = [] }, CancellationToken.None));
        Assert.Same(RankedRelated.Empty, await ranker.RankAsync(Request([]), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Scores_for_heritages_that_were_not_candidates_are_dropped_and_duplicates_keep_the_best()
    {
        var ranker = Ranker(new FakeClient((_, _) => Task.FromResult<IReadOnlyList<RelatedHeritage>>(
        [
            new RelatedHeritage(2, 1d, "thấp"), new RelatedHeritage(2, 4d, "cao"), new RelatedHeritage(99, 9d, "ngoài danh sách"),
        ])));
        var result = await ranker.RankAsync(Request(), CancellationToken.None);

        Assert.Equal([2L], result.Scores.Keys);
        Assert.Equal("cao", result.Of(2)!.Reason);
    }

    [Fact]
    public async Task When_the_ai_service_fails_the_server_falls_back_to_the_same_province_or_type()
    {
        var ranker = Ranker(new FakeClient((_, _) => throw new HttpRequestException("mất kết nối")));
        var result = await ranker.RankAsync(Request(), CancellationToken.None);

        Assert.True(result.UsedFallback);
        Assert.Equal([2L, 4L], result.Scores.Keys.Order());
    }

    [Fact]
    public async Task When_the_ai_service_is_too_slow_the_call_is_cancelled_after_two_seconds_and_the_fallback_answers()
    {
        var cancelled = false;
        var ranker = Ranker(new FakeClient(async (_, ct) =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }
            return [];
        }));

        var started = DateTime.UtcNow;
        var result = await ranker.RankAsync(Request(), CancellationToken.None);

        Assert.True(cancelled);
        Assert.True(result.UsedFallback);
        Assert.InRange((DateTime.UtcNow - started).TotalSeconds, 1.5, 8);
    }

    [Fact]
    public async Task A_cancellation_from_the_caller_is_not_swallowed_by_the_fallback()
    {
        var ranker = Ranker(new FakeClient(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            return [];
        }));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ranker.RankAsync(Request(), cts.Token));
    }

    // ---- Bản gọi qua HTTP ----

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return await respond(request);
        }
    }

    private static HttpRecommendationAiClient Http(StubHandler handler) =>
        new(new HttpClient(handler), Options.Create(new AiOptions { Mode = AiMode.Remote, BaseUrl = "http://ai.local:8000/", InternalKey = "khoa-noi-bo" }));

    [Fact]
    public async Task The_http_client_posts_the_contract_with_the_internal_key_and_no_personal_data()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { related = new[] { new { heritageId = 2, score = 3.5, reason = "Cùng loại" } } }, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        }));
        var result = await Http(handler).RankRelatedAsync(Request(), CancellationToken.None);

        var seen = Assert.Single(handler.Seen);
        Assert.Equal("http://ai.local:8000/v1/rank-related", seen.Request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, seen.Request.Method);
        Assert.Equal("khoa-noi-bo", seen.Request.Headers.GetValues("X-Internal-Key").Single());
        using var json = JsonDocument.Parse(seen.Body);
        Assert.Equal(7, json.RootElement.GetProperty("studentId").GetInt64());
        Assert.Equal(2, json.RootElement.GetProperty("level").GetInt32());
        Assert.Equal("MEDIUM", json.RootElement.GetProperty("targetDifficulty").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("references").GetArrayLength());
        Assert.Equal(3, json.RootElement.GetProperty("candidates").GetArrayLength());
        Assert.DoesNotContain("fullName", seen.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("studentCode", seen.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("className", seen.Body, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(new RelatedHeritage(2, 3.5, "Cùng loại"), Assert.Single(result));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task The_http_client_turns_error_responses_into_ai_exceptions_that_the_ranker_answers_with_the_fallback(HttpStatusCode status)
    {
        var client = Http(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(status))));
        await Assert.ThrowsAsync<AiServiceException>(() => client.RankRelatedAsync(Request(), CancellationToken.None));

        var result = await Ranker(client).RankAsync(Request(), CancellationToken.None);
        Assert.True(result.UsedFallback);
    }

    [Fact]
    public async Task An_empty_http_body_is_an_error_and_the_fallback_answers()
    {
        var client = Http(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) })));
        var result = await Ranker(client).RankAsync(Request(), CancellationToken.None);
        Assert.True(result.UsedFallback);
    }
}
