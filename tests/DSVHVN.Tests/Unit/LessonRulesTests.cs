using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy tắc thuần của bài học: số từ, khung nội dung và luồng theo cấp, điều kiện xuất bản và cảnh báo, mã bài học.</summary>
public sealed class LessonRulesTests
{
    private static string Words(int n) => string.Join(' ', Enumerable.Repeat("từ", n));

    private static LessonSectionView Story(string? image = "https://a/b.jpg", int words = 20) =>
        new(LessonSectionType.STORY, Words(words), image, null, null);

    private static LessonContent Content(string? title = "Bài", int heritages = 1, string? summary = "Tóm tắt",
        string[]? keyPoints = null, params LessonSectionView[] sections) =>
        new(title, heritages, summary, keyPoints ?? ["Ý chính"], sections);

    // ---- Số từ ----

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("   \n\t ", 0)]
    [InlineData("một", 1)]
    [InlineData("  một   hai\nba\tbốn ", 4)]
    [InlineData("Xin chào, thế giới!", 4)]
    public void Word_count_splits_on_whitespace(string? text, int expected) => Assert.Equal(expected, LessonText.CountWords(text));

    [Fact]
    public void Main_content_counts_the_summary_and_extra_reading_paragraphs_only()
    {
        var sections = new[]
        {
            new LessonSectionView(LessonSectionType.READING, Words(30), null, null, null),
            Story(words: 40),
            new LessonSectionView(LessonSectionType.MILESTONE, Words(10), null, 1010, null),
            new LessonSectionView(LessonSectionType.SOURCE_EXCERPT, Words(10), null, null, "Nguồn"),
            new LessonSectionView(LessonSectionType.REFLECTION, Words(10), null, null, null),
        };
        Assert.Equal(100 + 30, LessonChecks.MainWordCount(Words(100), sections));
        Assert.Equal(0, LessonChecks.MainWordCount(null, []));
    }

    // ---- Khung theo cấp ----

    [Fact]
    public void Each_level_has_its_own_word_range_flow_and_allowed_section_types()
    {
        var l1 = LessonFrames.For(1);
        var l2 = LessonFrames.For(2);
        var l3 = LessonFrames.For(3);

        Assert.Equal([(150, 250), (300, 500), (600, 900)], new[] { l1, l2, l3 }.Select(f => (f.MinWords, f.MaxWords)));
        Assert.Equal([(150, 200, 250), (300, 400, 500), (600, 750, 900)],
            new[] { l1, l2, l3 }.Select(f => (f.TargetWords(LessonLength.SHORT), f.TargetWords(LessonLength.MEDIUM), f.TargetWords(LessonLength.DETAILED))));
        Assert.Equal([LessonSectionType.STORY, LessonSectionType.IMAGE], l1.AllowedTypes);
        Assert.Equal([LessonSectionType.READING, LessonSectionType.IMAGE, LessonSectionType.MILESTONE], l2.AllowedTypes);
        Assert.Equal([LessonSectionType.READING, LessonSectionType.SOURCE_EXCERPT, LessonSectionType.MILESTONE, LessonSectionType.REFLECTION], l3.AllowedTypes);
        Assert.Equal(["STEP_BY_STEP", "FREE_STEPS", "DOCUMENT"], new[] { l1.Layout, l2.Layout, l3.Layout });
        Assert.Equal([true, false, false], new[] { l1.SequentialFlow, l2.SequentialFlow, l3.SequentialFlow });
        Assert.Equal(["Nhớ lại", "Nghe chuyện", "Xem hình", "Chơi", "Nhận thẻ"], l1.Flow.Select(s => s.Label));
        Assert.Equal(["RECALL", "LISTEN_STORY", "VIEW_IMAGES", "PLAY", "EARN_CARD"], l1.Flow.Select(s => s.Key));
        Assert.Equal(["Đọc bài", "Xem dòng thời gian", "Làm bài kiểm tra", "Xem giải thích"], l2.Flow.Select(s => s.Label));
        Assert.Equal(["Đọc tài liệu", "Làm bài kiểm tra có giờ", "Xem phân tích kết quả"], l3.Flow.Select(s => s.Label));
        Assert.Throws<ArgumentOutOfRangeException>(() => LessonFrames.For(4));
        Assert.True(l1.Allows(LessonSectionType.STORY));
        Assert.False(l1.Allows(LessonSectionType.READING));
        Assert.False(l3.Allows(LessonSectionType.IMAGE));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(9, 2)]
    [InlineData(10, 3)]
    [InlineData(12, 3)]
    public void The_level_of_a_lesson_is_the_level_of_its_class_grade(byte grade, int level) =>
        Assert.Equal(level, DSVHVN.Domain.Classes.SchoolClass.SchoolLevelOf(grade));

    // ---- Điều kiện xuất bản ----

    [Fact]
    public void Common_requirements_are_title_a_published_heritage_a_summary_and_one_to_ten_key_points()
    {
        var empty = LessonChecks.Evaluate(1, new LessonContent(" ", 0, null, [], []));
        Assert.Equal(
        [
            "tiêu đề bài học", "ít nhất một di sản đã xuất bản", "tóm tắt", "1-10 ý chính (mỗi ý tối đa 300 ký tự)", "ít nhất một bước câu chuyện",
        ], empty.Missing);
        Assert.False(empty.IsReady);

        var tooMany = LessonChecks.Evaluate(2, Content(keyPoints: Enumerable.Repeat("ý", 11).ToArray(),
            sections: new LessonSectionView(LessonSectionType.MILESTONE, "Mốc", null, 1010, null)));
        Assert.Contains("1-10 ý chính (mỗi ý tối đa 300 ký tự)", tooMany.Missing);
        var tooLong = LessonChecks.Evaluate(2, Content(keyPoints: [new string('x', 301)],
            sections: new LessonSectionView(LessonSectionType.MILESTONE, "Mốc", null, 1010, null)));
        Assert.Contains("1-10 ý chính (mỗi ý tối đa 300 ký tự)", tooLong.Missing);
        var ok = LessonChecks.Evaluate(2, Content(keyPoints: [new string('x', 300)],
            sections: new LessonSectionView(LessonSectionType.MILESTONE, "Mốc", null, 1010, null)));
        Assert.True(ok.IsReady);
    }

    [Fact]
    public void Level_one_needs_a_story_step_and_a_picture_for_every_step_but_length_only_warns()
    {
        Assert.Contains("ít nhất một bước câu chuyện", LessonChecks.Evaluate(1, Content()).Missing);
        Assert.Contains("hình cho mỗi bước câu chuyện", LessonChecks.Evaluate(1, Content(sections: [Story(), Story(image: null)])).Missing);
        Assert.Contains("hình cho mỗi bước câu chuyện", LessonChecks.Evaluate(1, Content(sections: [Story(image: "  ")])).Missing);

        var shortStory = LessonChecks.Evaluate(1, Content(summary: Words(20), sections: [Story()]));
        Assert.True(shortStory.IsReady);
        Assert.Contains("Nội dung hiện có 20 từ; bài của lớp cấp 1 thường dài 150-250 từ. Bạn vẫn có thể lưu hoặc xuất bản.", shortStory.Warnings);
        Assert.Contains("Câu chuyện có 1 bước; cấp 1 thường có 3-6 bước.", shortStory.Warnings);

        var good = LessonChecks.Evaluate(1, Content(summary: Words(200), sections: [Story(), Story(), Story(), Story()]));
        Assert.True(good.IsReady);
        Assert.Empty(good.Warnings);

        var longStep = LessonChecks.Evaluate(1, Content(summary: Words(200), sections: [Story(), Story(), Story(words: 51)]));
        Assert.Contains("Bước 3 dài 51 từ; mỗi bước nên tối đa 50 từ.", longStep.Warnings);
        Assert.True(longStep.IsReady);

        Assert.Contains(LessonChecks.Evaluate(1, Content(summary: Words(251), sections: [Story(), Story(), Story()])).Warnings, w => w.Contains("251 từ"));
        Assert.DoesNotContain(LessonChecks.Evaluate(1, Content(summary: Words(250), sections: [Story(), Story(), Story()])).Warnings, w => w.Contains("thường dài"));
        Assert.DoesNotContain(LessonChecks.Evaluate(1, Content(summary: Words(150), sections: [Story(), Story(), Story()])).Warnings, w => w.Contains("thường dài"));
        Assert.Contains(LessonChecks.Evaluate(1, Content(summary: Words(149), sections: [Story(), Story(), Story()])).Warnings, w => w.Contains("149 từ"));
    }

    [Fact]
    public void Level_two_needs_a_milestone_and_prefers_three_to_eight_milestones_and_a_captioned_picture()
    {
        Assert.Contains("ít nhất một mốc thời gian", LessonChecks.Evaluate(2, Content()).Missing);
        LessonSectionView Milestone() => new(LessonSectionType.MILESTONE, "Mốc", null, 1010, null);

        var one = LessonChecks.Evaluate(2, Content(summary: Words(400), sections: [Milestone()]));
        Assert.True(one.IsReady);
        Assert.Contains("Bài có 1 mốc thời gian; cấp 2 thường có 3-8 mốc.", one.Warnings);
        Assert.Contains("Bài cấp 2 nên có ít nhất một hình kèm chú thích.", one.Warnings);

        var captioned = new LessonSectionView(LessonSectionType.IMAGE, "Chú thích", "https://a/b.jpg", null, null);
        var good = LessonChecks.Evaluate(2, Content(summary: Words(400), sections: [Milestone(), Milestone(), Milestone(), captioned]));
        Assert.Empty(good.Warnings);
        var nine = LessonChecks.Evaluate(2, Content(summary: Words(400), sections: Enumerable.Repeat(Milestone(), 9).Append(captioned).ToArray()));
        Assert.Contains("Bài có 9 mốc thời gian; cấp 2 thường có 3-8 mốc.", nine.Warnings);

        // Bài đọc thêm tính vào số từ.
        var withReading = LessonChecks.Evaluate(2, Content(summary: Words(250),
            sections: [Milestone(), new LessonSectionView(LessonSectionType.READING, Words(100), null, null, null)]));
        Assert.DoesNotContain(withReading.Warnings, w => w.Contains("thường dài"));
    }

    [Fact]
    public void Level_three_needs_excerpts_with_sources_and_warns_about_excess_excerpts_and_reflections()
    {
        LessonSectionView Excerpt(string? source = "Nguồn", int words = 20) =>
            new(LessonSectionType.SOURCE_EXCERPT, Words(words), null, null, source);
        LessonSectionView Reflection() => new(LessonSectionType.REFLECTION, "Vì sao?", null, null, null);

        Assert.Contains("ít nhất một đoạn trích tư liệu gốc", LessonChecks.Evaluate(3, Content()).Missing);
        Assert.Contains("nguồn của mỗi đoạn trích", LessonChecks.Evaluate(3, Content(sections: [Excerpt(), Excerpt(source: " ")])).Missing);

        var good = LessonChecks.Evaluate(3, Content(summary: Words(700), sections: [Excerpt(), Reflection()]));
        Assert.True(good.IsReady);
        Assert.Empty(good.Warnings);

        var noReflection = LessonChecks.Evaluate(3, Content(summary: Words(700), sections: [Excerpt()]));
        Assert.Contains("Bài có 0 câu hỏi gợi suy nghĩ; cấp 3 thường có 1-5 câu.", noReflection.Warnings);
        var four = LessonChecks.Evaluate(3, Content(summary: Words(700), sections: [Excerpt(), Excerpt(), Excerpt(), Excerpt(words: 151), Reflection()]));
        Assert.Contains("Bài có 4 đoạn trích; cấp 3 thường có 1-3 đoạn.", four.Warnings);
        Assert.Contains("Đoạn trích 4 dài 151 từ; mỗi đoạn nên tối đa 150 từ.", four.Warnings);
        Assert.True(four.IsReady);
        var six = LessonChecks.Evaluate(3, Content(summary: Words(700), sections: [Excerpt(), .. Enumerable.Repeat(Reflection(), 6)]));
        Assert.Contains("Bài có 6 câu hỏi gợi suy nghĩ; cấp 3 thường có 1-5 câu.", six.Warnings);
    }

    [Fact]
    public void Sections_of_another_level_are_ignored_by_the_frame_and_reported_as_a_warning()
    {
        var content = Content(summary: Words(200), sections:
        [
            Story(), Story(), Story(),
            new LessonSectionView(LessonSectionType.MILESTONE, "Mốc", null, 1010, null),
            new LessonSectionView(LessonSectionType.REFLECTION, "Vì sao?", null, null, null),
        ]);

        var check = LessonChecks.Evaluate(1, content);

        Assert.True(check.IsReady);
        Assert.Contains("Có 2 phần thuộc khung của cấp khác nên học sinh lớp cấp 1 sẽ không thấy; hãy xóa hoặc đổi bằng phần của cấp 1.", check.Warnings);
        // Ở cấp 3 các bước câu chuyện không tính, nên thiếu đoạn trích.
        Assert.Contains("ít nhất một đoạn trích tư liệu gốc", LessonChecks.Evaluate(3, content).Missing);
    }

    // ---- Mã bài học ----

    [Fact]
    public void Lesson_codes_are_BH_plus_six_characters_without_confusing_letters()
    {
        var codes = Enumerable.Range(0, 5000).Select(_ => LessonCodes.NewCode()).ToList();

        Assert.All(codes, c =>
        {
            Assert.Equal(8, c.Length);
            Assert.StartsWith("BH", c);
            Assert.True(LessonCodes.IsWellFormed(c));
            Assert.DoesNotContain(c[2..], ch => "0O1I".Contains(ch));
        });
        Assert.True(codes.Distinct().Count() > 4990);           // khoảng một tỉ tổ hợp
    }

    [Theory]
    [InlineData("bh 7f3k2q", "BH7F3K2Q")]
    [InlineData(" BH-7F3K-2Q ", "BH7F3K2Q")]
    [InlineData("bh–7f3k—2q", "BH7F3K2Q")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Typed_codes_are_normalized_before_matching(string? typed, string expected) =>
        Assert.Equal(expected, LessonCodes.Normalize(typed));

    [Theory]
    [InlineData("BH7F3K2Q", true)]
    [InlineData("BH222222", true)]
    [InlineData("HS7F3K2Q", false)]     // mã học sinh
    [InlineData("BH7F3K2", false)]
    [InlineData("BH7F3K2QQ", false)]
    [InlineData("BH0F3K2Q", false)]     // 0 không thuộc bảng ký tự
    [InlineData("BHOF3K2Q", false)]
    [InlineData("BH1F3K2Q", false)]
    [InlineData("BHIF3K2Q", false)]
    [InlineData("bh7f3k2q", false)]     // phải chuẩn hóa trước
    [InlineData(null, false)]
    public void Well_formed_codes_use_the_32_character_alphabet(string? code, bool expected) =>
        Assert.Equal(expected, LessonCodes.IsWellFormed(code));

    [Fact]
    public void Vietnam_day_boundaries_for_the_plan_period_are_utc_plus_seven()
    {
        Assert.Equal(new DateTime(2026, 9, 28, 17, 0, 0, DateTimeKind.Utc), VietnamTime.StartOfDayUtc(new DateOnly(2026, 9, 29)));
        Assert.Equal(new DateTime(2026, 12, 31, 17, 0, 0, DateTimeKind.Utc), VietnamTime.StartOfDayUtc(new DateOnly(2027, 1, 1)));
    }
}
