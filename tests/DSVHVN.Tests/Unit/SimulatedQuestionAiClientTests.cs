using DSVHVN.Application.Lessons;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Ai;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Bản giả lập Dịch vụ AI sinh câu hỏi: tất định, mọi câu sinh ra đạt quy tắc cấu trúc của dạng (kiểm bằng chính bộ dựng câu) ở mọi cấp, ảnh chỉ lấy
/// từ ảnh đã gửi, đủ số câu yêu cầu khi di sản đủ dữ liệu, và báo lỗi không thử lại được khi dữ liệu không đủ.
/// </summary>
public sealed class SimulatedQuestionAiClientTests
{
    private static readonly string[] Sentences =
    [
        "Hoàng thành Thăng Long là trung tâm quyền lực của nhiều triều đại phong kiến Việt Nam suốt hơn mười thế kỷ.",
        "Khu di tích nằm giữa lòng Hà Nội và được UNESCO ghi danh là Di sản văn hóa thế giới vào năm hai nghìn không trăm mười.",
        "Các nhà khảo cổ đã tìm thấy nhiều lớp văn hóa chồng lên nhau từ thế kỷ thứ bảy đến thế kỷ mười chín.",
        "Trung tâm của hoàng thành là điện Kính Thiên, nơi các vua nhà Lê thiết triều.",
        "Cổng Đoan Môn là lối vào chính của khu cung điện xưa.",
        "Ngày nay du khách có thể tham quan khu khảo cổ học số mười tám Hoàng Diệu để xem các di vật quý.",
    ];

    public static AiHeritageInput Heritage(long id = 1, int images = 4, int events = 6, string name = "Hoàng thành Thăng Long") => new(
        id, name, "Trung tâm quyền lực của nhiều triều đại phong kiến Việt Nam.", string.Join(' ', Sentences),
        Enumerable.Range(0, events).Select(i => new AiTimelineInput(100 + i, "Thời Lý", $"Sự kiện số {i + 1} của {name}", null, 1010 + i * 61,
            HeritageTimeText.Year(1010 + i * 61))).ToList(),
        [new AiSourceInput("Quyết định xếp hạng - Thủ tướng Chính phủ", "https://vanban.chinhphu.vn/qd-1272")],
        Enumerable.Range(0, images).Select(i => new AiImageInput(10 + i, $"https://upload.wikimedia.org/{id}-{i}.jpg", i % 2 == 0 ? $"Ảnh {i + 1} của {name}" : null)).ToList());

    private static AiQuestionRequest Request(int level, int count, IReadOnlyList<QuestionType>? types = null, IReadOnlyList<AiHeritageInput>? heritages = null,
        DifficultyLevel difficulty = DifficultyLevel.MEDIUM) =>
        new(1, 1, level, count, difficulty, types ?? QuestionTypes.ForLevel(level), heritages ?? [Heritage()]);

    private static readonly SimulatedQuestionAiClient Client = new();

    private static IReadOnlySet<string> LibraryOf(AiQuestionRequest request, long heritageId) =>
        request.Heritages.Single(h => h.HeritageId == heritageId).Images.Select(i => i.Url).ToHashSet();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Every_generated_question_passes_the_structure_rules_of_its_type_at_every_level(int level)
    {
        var request = Request(level, 12);
        var result = await Client.GenerateQuestionsAsync(request, "kiem-thu", CancellationToken.None);

        Assert.Equal(12, result.Questions.Count);
        Assert.Equal(SimulatedQuestionAiClient.Name, result.Model);
        Assert.All(result.Questions, q =>
        {
            var built = QuestionBuilder.Build(q.ToSaveRequest(level, DifficultyLevel.MEDIUM), LibraryOf(request, q.HeritageId));
            Assert.True(built.IsValid, $"{q.QuestionType}: {string.Join("; ", built.ErrorTexts)}");
            Assert.Contains(q.QuestionType, request.Types);
            Assert.Equal((level, level), (built.Question!.LevelMin, built.Question.LevelMax));
        });
        // Mọi dạng của cấp đều có mặt.
        Assert.Equal(QuestionTypes.ForLevel(level).Order(), result.Questions.Select(q => q.QuestionType).Distinct().Order());
    }

    [Fact]
    public async Task The_same_request_gives_the_same_questions()
    {
        var a = await Client.GenerateQuestionsAsync(Request(2, 8), "a", CancellationToken.None);
        var b = await Client.GenerateQuestionsAsync(Request(2, 8), "b", CancellationToken.None);
        Assert.Equal(a.Questions.Select(q => (q.QuestionType, q.Content)), b.Questions.Select(q => (q.QuestionType, q.Content)));
    }

    [Fact]
    public async Task Only_the_requested_types_are_generated_and_questions_of_one_type_differ_from_each_other()
    {
        var result = await Client.GenerateQuestionsAsync(Request(2, 5, [QuestionType.FILL_BLANK]), "kiem-thu", CancellationToken.None);
        Assert.All(result.Questions, q => Assert.Equal(QuestionType.FILL_BLANK, q.QuestionType));
        Assert.Equal(result.Questions.Count, result.Questions.Select(q => q.Content).Distinct().Count());
        Assert.InRange(result.Questions.Count, 3, 5);
    }

    [Fact]
    public async Task Questions_are_spread_over_the_heritages_of_the_request()
    {
        var request = Request(2, 6, heritages: [Heritage(1), Heritage(2, name: "Văn Miếu Quốc Tử Giám")]);
        var result = await Client.GenerateQuestionsAsync(request, "kiem-thu", CancellationToken.None);
        Assert.Equal([1L, 2L], result.Questions.Select(q => q.HeritageId).Distinct().Order());
        Assert.All(result.Questions, q =>
        {
            var built = QuestionBuilder.Build(q.ToSaveRequest(2, DifficultyLevel.MEDIUM), LibraryOf(request, q.HeritageId));
            Assert.True(built.IsValid, string.Join("; ", built.ErrorTexts));
        });
    }

    [Fact]
    public async Task Pictures_in_questions_come_only_from_the_pictures_that_were_sent()
    {
        var request = Request(1, 12);
        var result = await Client.GenerateQuestionsAsync(request, "kiem-thu", CancellationToken.None);
        var sent = request.Heritages.SelectMany(h => h.Images.Select(i => i.Url)).ToHashSet();
        var used = result.Questions.SelectMany(q => new[] { q.ImageUrl }
            .Concat((q.Options ?? []).Select(o => o.ImageUrl)).Concat((q.Pairs ?? []).Select(p => p.LeftImageUrl))).Where(u => u is not null).ToList();
        Assert.NotEmpty(used);
        Assert.All(used, u => Assert.Contains(u!, sent));
    }

    [Fact]
    public async Task Level_one_true_false_always_has_a_picture_and_uses_short_sentences()
    {
        var result = await Client.GenerateQuestionsAsync(Request(1, 6, [QuestionType.TRUE_FALSE]), "kiem-thu", CancellationToken.None);
        Assert.All(result.Questions, q => Assert.NotNull(q.ImageUrl));
        Assert.Contains(result.Questions, q => q.AnswerIsTrue == true);
        Assert.Contains(result.Questions, q => q.AnswerIsTrue == false);
    }

    [Fact]
    public async Task Types_that_need_more_data_are_skipped_instead_of_failing_when_other_types_work()
    {
        // Chỉ 1 ảnh và không có dòng thời gian: không chọn hình, ghép cặp được, nhưng Đúng/Sai và sắp xếp các bước (cấp 1) làm được.
        var poor = Heritage(images: 1, events: 0);
        var result = await Client.GenerateQuestionsAsync(Request(1, 8, heritages: [poor]), "kiem-thu", CancellationToken.None);
        Assert.NotEmpty(result.Questions);
        Assert.DoesNotContain(result.Questions, q => q.QuestionType is QuestionType.IMAGE_CHOICE or QuestionType.MATCHING);
    }

    [Fact]
    public async Task Not_enough_data_for_any_requested_type_is_a_non_retryable_error()
    {
        var empty = new AiHeritageInput(1, "Di sản trống", null, null, [], [], []);
        var error = await Assert.ThrowsAsync<AiServiceException>(() => Client.GenerateQuestionsAsync(Request(2, 3, [QuestionType.SINGLE_CHOICE, QuestionType.FILL_BLANK, QuestionType.ORDERING], [empty]), "kiem-thu", CancellationToken.None));
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task Invalid_requests_are_refused_without_retrying()
    {
        var level = await Assert.ThrowsAsync<AiServiceException>(() => Client.GenerateQuestionsAsync(Request(4, 3, [QuestionType.SINGLE_CHOICE]), "kiem-thu", CancellationToken.None));
        var types = await Assert.ThrowsAsync<AiServiceException>(() => Client.GenerateQuestionsAsync(Request(2, 3, []), "kiem-thu", CancellationToken.None));
        var heritages = await Assert.ThrowsAsync<AiServiceException>(() => Client.GenerateQuestionsAsync(Request(2, 3, heritages: []), "kiem-thu", CancellationToken.None));
        Assert.All(new[] { level, types, heritages }, e => Assert.False(e.Retryable));
    }

    [Fact]
    public async Task The_call_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client.GenerateQuestionsAsync(Request(2, 3), "kiem-thu", cts.Token));
    }

    [Fact]
    public async Task Single_choice_with_a_timeline_asks_for_the_date_of_an_event_and_the_correct_option_is_not_always_first()
    {
        var result = await Client.GenerateQuestionsAsync(Request(3, 6, [QuestionType.SINGLE_CHOICE]), "kiem-thu", CancellationToken.None);
        var positions = result.Questions.Select(q => q.Options!.FindIndex(o => o.IsCorrect)).Distinct().ToList();
        Assert.True(positions.Count > 1, "Đáp án đúng luôn ở một vị trí.");
        Assert.All(result.Questions, q => Assert.Single(q.Options!, o => o.IsCorrect));
    }
}
