using DSVHVN.Application.Lessons;
using DSVHVN.Application.Translations;
using DSVHVN.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Tóm tắt di sản là văn bản thuần (hiện như chữ thường và đọc thành tiếng): hàm bỏ ký hiệu Markdown, bản giả lập AI và bộ chạy kiểm kết quả đều không để lọt
/// tiêu đề "##", chữ đậm, gạch đầu dòng hay liên kết vào tóm tắt hoặc bản dịch của nó.
/// </summary>
public sealed class PlainTextTests
{
    private const string Markdown =
        "## Giới thiệu\nKhu **Hoàng thành** nằm ở trung tâm [Hà Nội](https://example.org/ha-noi).\n\n"
        + "## Gợi ý cho học sinh\n- Vẽ sơ đồ các công trình.\n* Thảo luận về *giá trị* di sản\n1. Làm bài tập\n\n> Trích dẫn ngắn\n\n---\n`mã` và ~~bỏ~~ ![ảnh](a.png)";

    [Fact]
    public void Markdown_marks_are_removed_and_text_is_kept()
    {
        var plain = PlainText.FromMarkdown(Markdown);

        Assert.Equal(
            "Giới thiệu.\nKhu Hoàng thành nằm ở trung tâm Hà Nội.\n\n"
            + "Gợi ý cho học sinh.\nVẽ sơ đồ các công trình.\nThảo luận về giá trị di sản.\nLàm bài tập.\n\nTrích dẫn ngắn\n\nmã và bỏ ảnh",
            plain);
        foreach (var mark in new[] { "#", "**", "](", "```", "~~", "!["}) Assert.DoesNotContain(mark, plain);
    }

    [Fact]
    public void A_heading_becomes_its_own_sentence_and_keeps_an_existing_end_mark()
    {
        Assert.Equal("Lịch sử hình thành.\nNăm 1010.", PlainText.FromMarkdown("## Lịch sử hình thành\nNăm 1010."));
        Assert.Equal("Bạn có biết?\nCó.", PlainText.FromMarkdown("### Bạn có biết?\nCó."));
    }

    [Fact]
    public void Heading_marks_in_the_middle_of_a_flattened_line_are_dropped()
    {
        var plain = PlainText.FromMarkdown("Di tích quốc gia đặc biệt (2009). ## Giới thiệu Khu Trung tâm là phần lõi. ### Chi tiết rất hay");
        Assert.DoesNotContain("#", plain);
        Assert.Equal("Di tích quốc gia đặc biệt (2009). Giới thiệu Khu Trung tâm là phần lõi. Chi tiết rất hay", plain);
    }

    [Fact]
    public void Ordinary_text_is_left_as_it_is()
    {
        const string text = "Năm 1010, vua Lý Thái Tổ dời đô. Hồ sơ số #1328 (ii), (iii) và (vi) - ghi danh năm 2010; snake_case_name giữ nguyên.";
        Assert.Equal(text, PlainText.FromMarkdown(text));
        Assert.Equal(string.Empty, PlainText.FromMarkdown(null));
        Assert.Equal(string.Empty, PlainText.FromMarkdown("  \n\n "));
    }

    [Fact]
    public void Fenced_code_keeps_its_text_without_the_fence_and_escapes_are_resolved()
    {
        Assert.Equal("dòng một\ndòng hai\nGiá 5 * 3 = 15", PlainText.FromMarkdown("```\ndòng một\ndòng hai\n```\nGiá 5 \\* 3 = 15"));
    }

    [Fact]
    public async Task Simulated_summary_of_markdown_content_is_plain_text()
    {
        var source = new AiHeritageText(
            "Hoàng thành Thăng Long",
            "Trung tâm **quyền lực** của kinh đô.",
            "## Giới thiệu\nKhu di sản gồm hai bộ phận liền kề.\n\n## Gợi ý\n- Vẽ sơ đồ đơn giản.\n- Làm bài tập.",
            [new AiTimelineText(1, "Thời Lý", null)], [], []);

        var result = await new SimulatedHeritageAiClient().SummarizeHeritageAsync(new AiSummarizeRequest(1, 2, source), "x", CancellationToken.None);

        foreach (var text in new[] { result.ShortText, result.DetailedText })
        {
            Assert.DoesNotContain("#", text);
            Assert.DoesNotContain("**", text);
            Assert.DoesNotContain("- ", text);
        }
        Assert.Contains("Giới thiệu. Khu di sản gồm hai bộ phận liền kề.", result.ShortText);
        Assert.Contains("Trung tâm quyền lực của kinh đô.", result.ShortText);
    }

    [Fact]
    public async Task Simulated_translation_keeps_the_heading_mark_in_front_of_the_language_tag()
    {
        var content = new AiHeritageText("Tên", null, "## Giới thiệu\nĐoạn một.\n\n- Mục một", [], [], []);
        var result = await new SimulatedHeritageAiClient().TranslateHeritageAsync(
            new AiTranslateRequest(1, 2, "en", "vi", content, "vi", new AiSummaryText("Ngắn.", "Chi tiết.")), "x", CancellationToken.None);

        Assert.StartsWith("## [en] Giới thiệu\n", result.Content.Content);
        Assert.Equal("[en] Ngắn.", result.Summary!.ShortText);
        Assert.Equal("- [en] Mục", SimulatedHeritageAiClient.TagText("en", "- Mục")[..10]);
    }

    private sealed class Fixed(AiSummarizeResult summary, AiTranslateResult? translation = null) : IHeritageAiClient
    {
        public string ModelName => "kiem-thu";

        public Task<AiSummarizeResult> SummarizeHeritageAsync(AiSummarizeRequest request, string correlationId, CancellationToken ct) => Task.FromResult(summary);

        public Task<AiTranslateResult> TranslateHeritageAsync(AiTranslateRequest request, string correlationId, CancellationToken ct) => Task.FromResult(translation!);
    }

    private static HeritageAiRunner Runner(IHeritageAiClient client) =>
        new(client, Options.Create(new AiOptions { MaxRetries = 0, HeritageAttemptTimeoutSeconds = 5 }), NullLogger<HeritageAiRunner>.Instance);

    [Fact]
    public async Task Runner_strips_markdown_from_what_the_ai_service_returned()
    {
        var raw = new AiSummarizeResult("## Giới thiệu\nBản **ngắn**.", "## Mở đầu\n- Ý một\n- Ý hai\n\nHết [liên kết](http://x).", "m", new AiUsage(1, 1));
        var request = new AiSummarizeRequest(1, 2, new AiHeritageText("Tên", null, "Nội dung", [], [], []));

        var attempt = await Runner(new Fixed(raw)).SummarizeAsync(request, "x", CancellationToken.None);

        Assert.True(attempt.Succeeded);
        Assert.Equal("Giới thiệu. Bản ngắn.", attempt.Value!.ShortText);
        Assert.Equal("Mở đầu.\nÝ một.\nÝ hai.\n\nHết liên kết.", attempt.Value.DetailedText);
    }

    [Fact]
    public async Task Runner_strips_markdown_from_a_translated_summary_and_rejects_one_that_is_only_marks()
    {
        var request = new AiTranslateRequest(1, 2, "en", "vi", new AiHeritageText("Tên", null, null, [], [], []), "vi", new AiSummaryText("Ngắn.", "Chi tiết."));
        var good = new AiTranslateResult(
            new AiHeritageText("Name", null, null, [], [], []), new AiSummaryText("## Intro\nShort.", "**Detailed** text."), "m", new AiUsage(1, 1));
        var empty = good with { Summary = new AiSummaryText("##", "---") };

        var ok = await Runner(new Fixed(null!, good)).TranslateAsync(request, "x", CancellationToken.None);
        var bad = await Runner(new Fixed(null!, empty)).TranslateAsync(request, "x", CancellationToken.None);

        Assert.Equal(("Intro. Short.", "Detailed text."), (ok.Value!.Summary!.ShortText, ok.Value.Summary.DetailedText));
        Assert.False(bad.Succeeded);
    }
}
