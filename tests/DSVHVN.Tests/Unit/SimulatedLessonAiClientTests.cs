using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Ai;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Bản giả lập Dịch vụ AI: sinh nội dung mẫu đúng khung của cấp từ chính dữ liệu di sản, tất định, không gọi mạng. Mọi kết quả phải qua được bước kiểm
/// kết quả của backend (số từ trong khoảng của cấp, bước, hình, mốc, trích nguyên văn có nguồn).
/// </summary>
public sealed class SimulatedLessonAiClientTests
{
    private readonly SimulatedLessonAiClient _ai = new();

    private const string Content =
        "Hoàng thành Thăng Long là trung tâm quyền lực của nhiều triều đại phong kiến Việt Nam suốt hơn mười thế kỷ. " +
        "Khu di tích nằm giữa lòng Hà Nội và được UNESCO ghi danh là Di sản văn hóa thế giới vào năm hai nghìn không trăm mười. " +
        "Các nhà khảo cổ đã tìm thấy nhiều lớp văn hóa chồng lên nhau từ thế kỷ thứ bảy đến thế kỷ mười chín. " +
        "Trung tâm của hoàng thành là điện Kính Thiên, nơi các vua nhà Lê thiết triều. " +
        "# Tiêu đề Markdown\n**Cổng Đoan Môn** là lối vào chính của khu cung điện xưa. " +
        "Ngày nay du khách có thể tham quan khu khảo cổ học số mười tám Hoàng Diệu để xem các di vật quý.";

    private static AiHeritageInput Heritage(long id = 1, string name = "Hoàng thành Thăng Long", string? content = Content, int images = 3, int events = 6,
        bool sources = true) => new(
        id, name, "Trung tâm quyền lực của nhiều triều đại.", content,
        Enumerable.Range(0, events).Select(i => new AiTimelineInput(id * 100 + i, "Thời Lý", $"Sự kiện {i + 1}", "Mô tả sự kiện. Thêm một câu nữa.", 1010 + i * 50,
            (1010 + i * 50).ToString())).ToList(),
        sources ? [new AiSourceInput("Quyết định xếp hạng — Thủ tướng Chính phủ, truy cập 26/09/2026", "https://vanban.chinhphu.vn/qd-1272")] : [],
        Enumerable.Range(0, images).Select(i => new AiImageInput(id * 1000 + i, $"https://upload.wikimedia.org/{id}-{i}.jpg", i == 0 ? "Cổng thành" : null)).ToList());

    private static AiComposeRequest Request(int level, LessonLength length, params AiHeritageInput[] heritages) =>
        new(1, 1, level, length, heritages.Length == 0 ? [Heritage()] : heritages, "");

    private Task<AiComposeResult> Compose(AiComposeRequest request) => _ai.ComposeLessonAsync(request, "test", CancellationToken.None);

    private static int Words(string? s) => LessonText.CountWords(s);

    [Theory]
    [InlineData(1, LessonLength.SHORT)]
    [InlineData(1, LessonLength.MEDIUM)]
    [InlineData(1, LessonLength.DETAILED)]
    [InlineData(2, LessonLength.SHORT)]
    [InlineData(2, LessonLength.MEDIUM)]
    [InlineData(2, LessonLength.DETAILED)]
    [InlineData(3, LessonLength.SHORT)]
    [InlineData(3, LessonLength.MEDIUM)]
    [InlineData(3, LessonLength.DETAILED)]
    public async Task Word_count_is_inside_the_range_of_the_level_for_every_length(int level, LessonLength length)
    {
        var frame = LessonFrames.For(level);

        var result = await Compose(Request(level, length));

        Assert.InRange(Words(result.Content), frame.MinWords, frame.MaxWords);
        Assert.InRange(Words(result.Content), Math.Min(frame.TargetWords(length), frame.MaxWords - 10), Math.Min(frame.TargetWords(length), frame.MaxWords - 10) + 10);
        Assert.Equal((level, length, SimulatedLessonAiClient.Name), (result.GradeLevel, result.Length, result.Model));
        Assert.InRange(result.KeyPoints.Count, 3, 7);
        Assert.All(result.KeyPoints, k => Assert.InRange(k.Length, 1, 300));
        Assert.True(result.Usage.InputTokens > 0 && result.Usage.OutputTokens > 0);
    }

    [Fact]
    public async Task Output_does_not_depend_on_the_clock_or_randomness()
    {
        var request = Request(2, LessonLength.MEDIUM, Heritage(1), Heritage(2, "Văn Miếu"));

        var a = await Compose(request);
        var b = await Compose(request);

        Assert.Equal(a.Content, b.Content);
        Assert.Equal(a.Steps, b.Steps);
        Assert.Equal(a.KeyPoints, b.KeyPoints);
    }

    [Fact]
    public async Task Level_one_story_uses_short_sentences_and_three_to_six_steps_that_each_carry_a_picture_from_the_offered_images()
    {
        var heritage = Heritage();
        var result = await Compose(Request(1, LessonLength.DETAILED, heritage));

        var steps = result.Steps.Where(s => s.Kind == LessonSectionType.STORY).ToList();
        Assert.InRange(steps.Count, 3, 6);
        Assert.All(steps, s =>
        {
            Assert.InRange(Words(s.Text), 1, 50);
            Assert.Contains(s.MediaId!.Value, heritage.Images.Select(i => i.MediaId));
        });
        Assert.Equal(result.Content, string.Join(' ', steps.Select(s => s.Text)));
        Assert.All(result.Content.Split('.', StringSplitOptions.RemoveEmptyEntries), sentence => Assert.InRange(Words(sentence), 1, 15));
        var gallery = result.Steps.Where(s => s.Kind == LessonSectionType.IMAGE).ToList();
        Assert.InRange(gallery.Count, 1, 3);
        Assert.Equal(gallery.Count, gallery.Select(g => g.MediaId).Distinct().Count());
        Assert.Empty(result.SourceQuotes);
        Assert.DoesNotContain(result.Steps, s => s.Kind is LessonSectionType.MILESTONE or LessonSectionType.REFLECTION or LessonSectionType.READING);
        Assert.DoesNotContain("#", result.Content);
        Assert.DoesNotContain("*", result.Content);
    }

    [Fact]
    public async Task Level_one_still_reaches_the_minimum_length_when_the_heritage_has_almost_no_text_or_no_pictures()
    {
        var bare = new AiHeritageInput(7, "Chùa Một Cột", null, "Ngắn.", [], [], []);

        foreach (var length in new[] { LessonLength.SHORT, LessonLength.DETAILED })
        {
            var result = await Compose(Request(1, length, bare));
            Assert.InRange(Words(result.Content), 150, 250);
            Assert.All(result.Steps.Where(s => s.Kind == LessonSectionType.STORY), s => Assert.Null(s.MediaId));   // không có ảnh thì không bịa ảnh
            Assert.DoesNotContain(result.Steps, s => s.Kind == LessonSectionType.IMAGE);
        }
    }

    [Fact]
    public async Task Level_two_has_milestones_in_time_order_picked_from_the_timeline_and_captioned_pictures()
    {
        var heritage = Heritage(events: 20);
        var result = await Compose(Request(2, LessonLength.DETAILED, heritage));

        var milestones = result.Steps.Where(s => s.Kind == LessonSectionType.MILESTONE).ToList();
        Assert.InRange(milestones.Count, 3, 8);
        Assert.Equal(milestones.Select(m => m.Year).Order(), milestones.Select(m => m.Year));
        Assert.All(milestones, m =>
        {
            Assert.Contains(m.EventId!.Value, heritage.Timeline.Select(t => t.EventId));
            Assert.Equal(heritage.Timeline.Single(t => t.EventId == m.EventId).StartYear, m.Year);
            Assert.False(string.IsNullOrWhiteSpace(m.Text));
        });
        var images = result.Steps.Where(s => s.Kind == LessonSectionType.IMAGE).ToList();
        Assert.InRange(images.Count, 1, 2);
        Assert.All(images, i => Assert.False(string.IsNullOrWhiteSpace(i.Text)));
        Assert.Empty(result.SourceQuotes);
        Assert.DoesNotContain(result.Steps, s => s.Kind is LessonSectionType.STORY or LessonSectionType.REFLECTION);
    }

    [Fact]
    public async Task Level_two_with_a_short_timeline_takes_all_its_events_and_with_none_leaves_the_teacher_to_add_them()
    {
        var two = await Compose(Request(2, LessonLength.MEDIUM, Heritage(events: 2)));
        Assert.Equal(2, two.Steps.Count(s => s.Kind == LessonSectionType.MILESTONE));

        var none = await Compose(Request(2, LessonLength.MEDIUM, Heritage(events: 0)));
        Assert.DoesNotContain(none.Steps, s => s.Kind == LessonSectionType.MILESTONE);
        Assert.InRange(Words(none.Content), 300, 500);
    }

    [Fact]
    public async Task Level_three_quotes_are_verbatim_pieces_of_the_heritage_text_with_a_source_and_there_are_reflection_questions()
    {
        var heritages = new[] { Heritage(1), Heritage(2, "Văn Miếu"), Heritage(3, "Chùa Một Cột", sources: false) };
        var result = await Compose(Request(3, LessonLength.MEDIUM, heritages));

        Assert.InRange(result.SourceQuotes.Count, 1, 3);
        Assert.All(result.SourceQuotes, q =>
        {
            Assert.Contains(q.Quote, Content);                                 // nguyên văn
            Assert.InRange(Words(q.Quote), 8, 60);
            Assert.False(string.IsNullOrWhiteSpace(q.Source));
            Assert.Contains(q.HeritageId, heritages.Select(h => h.HeritageId));
        });
        Assert.Contains(result.SourceQuotes, q => q.Source.Contains("Quyết định xếp hạng") && q.Source.Contains("vanban.chinhphu.vn"));
        var reflections = result.Steps.Where(s => s.Kind == LessonSectionType.REFLECTION).ToList();
        Assert.InRange(reflections.Count, 1, 5);
        Assert.All(reflections, r => Assert.False(string.IsNullOrWhiteSpace(r.Text)));
        Assert.DoesNotContain(result.Steps, s => s.Kind is LessonSectionType.STORY or LessonSectionType.IMAGE);
        Assert.Contains("Hoàng thành Thăng Long", result.Content);
        Assert.Contains("Văn Miếu", result.Content);
        Assert.Contains("Chùa Một Cột", result.Content);
    }

    [Fact]
    public async Task A_source_is_named_even_when_the_heritage_has_none_listed()
    {
        var result = await Compose(Request(3, LessonLength.SHORT, Heritage(sources: false)));
        Assert.All(result.SourceQuotes, q => Assert.Equal("Cục Di sản văn hóa, dsvh.gov.vn", q.Source));
    }

    [Fact]
    public async Task The_content_pulls_from_every_heritage_of_the_lesson_and_names_them()
    {
        var result = await Compose(Request(2, LessonLength.DETAILED, Heritage(1, "Hoàng thành Thăng Long"), Heritage(2, "Văn Miếu Quốc Tử Giám")));

        Assert.Contains("Hoàng thành Thăng Long", result.Content);
        Assert.Contains("Văn Miếu Quốc Tử Giám", result.Content);
        Assert.Equal(2, result.KeyPoints.Count(k => k.Contains("là di sản văn hóa của Việt Nam")));
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_call_and_an_unknown_level_is_a_client_error()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _ai.ComposeLessonAsync(Request(1, LessonLength.SHORT), "t", cts.Token));

        var ex = await Assert.ThrowsAsync<AiServiceException>(() => Compose(Request(4, LessonLength.SHORT)));
        Assert.False(ex.Retryable);
    }
}
