using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy tắc thuần của thư viện bài mẫu (đề xuất, rút, duyệt, từ chối, trích tóm tắt) và của việc viết lại một phần bằng AI (loại phần được viết lại, giới hạn từng loại).</summary>
public sealed class LessonSampleRulesTests
{
    // ---- Đề xuất, rút, duyệt, từ chối ----

    [Fact]
    public void Only_a_published_lesson_that_is_not_pending_or_approved_can_be_requested()
    {
        Assert.Null(LessonSampleRules.RequestBlockReason(ContentStatus.PUBLISHED, null));
        Assert.Null(LessonSampleRules.RequestBlockReason(ContentStatus.PUBLISHED, LessonSampleStatus.REJECTED));

        Assert.Contains("đã xuất bản", LessonSampleRules.RequestBlockReason(ContentStatus.DRAFT, null));
        Assert.Contains("lưu trữ", LessonSampleRules.RequestBlockReason(ContentStatus.ARCHIVED, null));
        Assert.Contains("chờ quản trị", LessonSampleRules.RequestBlockReason(ContentStatus.PUBLISHED, LessonSampleStatus.PENDING));
        Assert.Contains("đã ở trong thư viện", LessonSampleRules.RequestBlockReason(ContentStatus.PUBLISHED, LessonSampleStatus.APPROVED));
    }

    [Theory]
    [InlineData(null, false, false, false)]
    [InlineData(LessonSampleStatus.PENDING, true, true, true)]
    [InlineData(LessonSampleStatus.APPROVED, true, false, true)]
    [InlineData(LessonSampleStatus.REJECTED, true, false, false)]
    public void Withdraw_approve_and_reject_follow_the_library_state(LessonSampleStatus? status, bool canWithdraw, bool canApprove, bool canReject)
    {
        Assert.Equal(canWithdraw, LessonSampleRules.CanWithdraw(status));
        Assert.Equal(canApprove, LessonSampleRules.CanApprove(status));
        Assert.Equal(canReject, LessonSampleRules.CanReject(status));
    }

    [Fact]
    public void The_summary_excerpt_is_plain_cut_at_a_word_boundary_and_never_longer_than_200_characters()
    {
        Assert.Equal(string.Empty, LessonSampleRules.SummaryExcerpt(null));
        Assert.Equal(string.Empty, LessonSampleRules.SummaryExcerpt("  \n "));
        Assert.Equal("Một câu ngắn. Câu thứ hai.", LessonSampleRules.SummaryExcerpt(" Một   câu ngắn.\n\nCâu thứ hai. "));

        var exactly200 = new string('a', 200);
        Assert.Equal(exactly200, LessonSampleRules.SummaryExcerpt(exactly200));

        var long1 = string.Join(' ', Enumerable.Repeat("Hoàng thành Thăng Long", 30));
        var cut = LessonSampleRules.SummaryExcerpt(long1);
        Assert.True(cut.Length <= LessonSampleRules.SummaryExcerptMaxLength);
        Assert.EndsWith("…", cut);
        Assert.DoesNotContain("  ", cut);
        // Cắt ở ranh giới từ, không cụt giữa chữ.
        Assert.Contains(cut[..^1].Split(' ').Last(), new[] { "Hoàng", "thành", "Thăng", "Long" });

        var noSpaces = LessonSampleRules.SummaryExcerpt(new string('b', 500));
        Assert.Equal(LessonSampleRules.SummaryExcerptMaxLength, noSpaces.Length);
    }

    // ---- Viết lại một phần ----

    [Theory]
    [InlineData(1, LessonSectionType.STORY, true)]
    [InlineData(1, LessonSectionType.IMAGE, true)]
    [InlineData(1, LessonSectionType.READING, false)]
    [InlineData(1, LessonSectionType.MILESTONE, false)]
    [InlineData(2, LessonSectionType.READING, true)]
    [InlineData(2, LessonSectionType.IMAGE, true)]
    [InlineData(2, LessonSectionType.MILESTONE, true)]
    [InlineData(2, LessonSectionType.STORY, false)]
    [InlineData(2, LessonSectionType.REFLECTION, false)]
    [InlineData(3, LessonSectionType.READING, true)]
    [InlineData(3, LessonSectionType.MILESTONE, true)]
    [InlineData(3, LessonSectionType.REFLECTION, true)]
    [InlineData(3, LessonSectionType.IMAGE, false)]
    [InlineData(3, LessonSectionType.SOURCE_EXCERPT, false)]
    public void A_section_can_be_rewritten_when_it_is_in_the_frame_of_the_level_and_is_not_a_source_excerpt(int level, LessonSectionType type, bool expected) =>
        Assert.Equal(expected, SectionRewriteRules.CanRewrite(level, type));

    private static string Words(int count) => string.Join(' ', Enumerable.Range(1, count).Select(i => $"từ{i}"));

    [Fact]
    public void A_story_step_needs_two_to_four_sentences_at_most_50_words_and_short_sentences()
    {
        Assert.Empty(SectionRewriteRules.Check(1, LessonSectionType.STORY, "Cậu bé lớn nhanh. Cậu bé vươn vai."));

        Assert.Single(SectionRewriteRules.Check(1, LessonSectionType.STORY, "Chỉ một câu thôi."), w => w.Contains("1 câu"));
        Assert.Contains(SectionRewriteRules.Check(1, LessonSectionType.STORY, "Một. Hai. Ba. Bốn. Năm."), w => w.Contains("5 câu"));
        Assert.Contains(SectionRewriteRules.Check(1, LessonSectionType.STORY, Words(30) + ". " + Words(30) + "."), w => w.Contains("tối đa 50 từ"));
        // Câu trung bình dài hơn 15 từ.
        Assert.Contains(SectionRewriteRules.Check(1, LessonSectionType.STORY, Words(20) + ". " + Words(20) + "."), w => w.Contains("trung bình"));
    }

    [Fact]
    public void A_caption_is_one_sentence_up_to_20_words_at_level_one_and_40_at_levels_two_and_three()
    {
        Assert.Empty(SectionRewriteRules.Check(1, LessonSectionType.IMAGE, "Cổng làng buổi sáng."));
        Assert.Contains(SectionRewriteRules.Check(1, LessonSectionType.IMAGE, Words(25) + "."), w => w.Contains("tối đa 20 từ"));
        Assert.Empty(SectionRewriteRules.Check(2, LessonSectionType.IMAGE, Words(25) + "."));
        Assert.Contains(SectionRewriteRules.Check(2, LessonSectionType.IMAGE, Words(45) + "."), w => w.Contains("tối đa 40 từ"));
        Assert.Contains(SectionRewriteRules.Check(2, LessonSectionType.IMAGE, "Câu một. Câu hai."), w => w.Contains("chỉ một câu"));
    }

    [Fact]
    public void A_reading_has_20_to_400_words_and_a_milestone_one_or_two_sentences_up_to_60_words()
    {
        Assert.Empty(SectionRewriteRules.Check(2, LessonSectionType.READING, Words(20)));
        Assert.Empty(SectionRewriteRules.Check(2, LessonSectionType.READING, Words(400)));
        Assert.Contains(SectionRewriteRules.Check(2, LessonSectionType.READING, Words(19)), w => w.Contains("20-400"));
        Assert.Contains(SectionRewriteRules.Check(2, LessonSectionType.READING, Words(401)), w => w.Contains("20-400"));

        Assert.Empty(SectionRewriteRules.Check(2, LessonSectionType.MILESTONE, "Vua dời đô. Kinh thành mới ra đời."));
        Assert.Contains(SectionRewriteRules.Check(2, LessonSectionType.MILESTONE, "Một. Hai. Ba."), w => w.Contains("3 câu"));
        Assert.Contains(SectionRewriteRules.Check(2, LessonSectionType.MILESTONE, Words(61) + "."), w => w.Contains("tối đa 60 từ"));
    }

    [Fact]
    public void A_reflection_is_one_question_ending_with_a_question_mark_up_to_60_words()
    {
        Assert.Empty(SectionRewriteRules.Check(3, LessonSectionType.REFLECTION, "Vì sao các vua chọn vùng đất này?"));
        Assert.Contains(SectionRewriteRules.Check(3, LessonSectionType.REFLECTION, "Hãy kể về vùng đất này."), w => w.Contains("dấu hỏi"));
        Assert.Contains(SectionRewriteRules.Check(3, LessonSectionType.REFLECTION, "Vì sao? Ai chọn?"), w => w.Contains("2 câu"));
        Assert.Contains(SectionRewriteRules.Check(3, LessonSectionType.REFLECTION, Words(61) + "?"), w => w.Contains("tối đa 60 từ"));
    }

    [Theory]
    [InlineData("**Đậm** không được dùng")]
    [InlineData("## Tiêu đề trong chữ")]
    [InlineData("- gạch đầu dòng\n- thứ hai")]
    [InlineData("Có `mã` trong chữ")]
    public void Markdown_marks_are_flagged_because_lessons_use_plain_text(string text) =>
        Assert.Contains(SectionRewriteRules.Check(2, LessonSectionType.READING, Words(25) + "\n" + text), w => w.Contains("Markdown"));

    [Fact]
    public void Plain_text_with_a_hyphen_inside_a_word_or_a_dash_is_not_flagged()
    {
        Assert.DoesNotContain(SectionRewriteRules.Check(2, LessonSectionType.READING, Words(25) + " Hà Nội - Thăng Long, Đông-Tây."), w => w.Contains("Markdown"));
    }
}
