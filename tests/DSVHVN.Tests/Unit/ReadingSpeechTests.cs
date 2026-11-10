using DSVHVN.Application.Speech;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Nút "Nghe bài đọc" của lớp cấp 2 và 3: máy chủ tạo trước từng câu của nội dung chính và của mỗi phần Bài đọc, cách tách câu phải trùng từng ký tự với cách web và app
/// tách để đọc. Bộ câu mẫu ở đây giống hệt bộ câu mẫu trong kiểm thử lời đọc bài của web học tập và app, đổi bên nào thì đổi cả hai.
/// </summary>
public sealed class ReadingSpeechTests
{
    /// <summary>Chuỗi mẫu chung (đã thay xuống dòng bằng ký tự xuống dòng thật): ba đoạn, một đoạn có ba câu và dấu chấm lửng (dấu chấm lửng không tách câu, như Intl.Segmenter của giao diện).</summary>
    public const string Sample =
        "Hoàng thành Thăng Long được xây dựng vào năm 1010.\n\n" +
        "Trải qua nhiều triều đại (Lý, Trần, Lê), nơi đây trở thành trung tâm chính trị của đất nước! Vì sao các vua chọn vùng đất này? Hãy cùng tìm hiểu…\n\n" +
        "Năm 2010, UNESCO ghi danh khu trung tâm Hoàng thành là Di sản Văn hóa Thế giới.";

    public static readonly string[] SampleSentences =
    [
        "Hoàng thành Thăng Long được xây dựng vào năm 1010.",
        "Trải qua nhiều triều đại (Lý, Trần, Lê), nơi đây trở thành trung tâm chính trị của đất nước!",
        "Vì sao các vua chọn vùng đất này?",
        "Hãy cùng tìm hiểu… Năm 2010, UNESCO ghi danh khu trung tâm Hoàng thành là Di sản Văn hóa Thế giới.",
    ];

    private static LessonSection Section(LessonSectionType type, string? title, string? body, int order) =>
        new() { Id = order, SectionType = type, Title = title, Body = body, SortOrder = order };

    [Fact]
    public void The_shared_sample_splits_into_the_four_expected_sentences_in_order_because_an_ellipsis_does_not_end_a_sentence()
    {
        Assert.Equal(SampleSentences, SpeechWarmUpTexts.ForReadingLesson(Sample, []));
        // Bộ tách câu thuần cho cùng kết quả (không cần qua bước bỏ Markdown).
        Assert.Equal(SampleSentences, SpeechRules.SplitSentences(Sample));
    }

    [Fact]
    public void The_main_content_comes_first_then_each_reading_section_in_order_and_nothing_else_is_read()
    {
        var sections = new[]
        {
            // Cố ý đưa phần có thứ tự lớn trước để kiểm thứ tự đọc theo SortOrder.
            Section(LessonSectionType.READING, "Bài đọc hai", "Phần hai. Hết phần hai.", 5),
            Section(LessonSectionType.MILESTONE, "1010", "Dời đô về Thăng Long.", 1),
            Section(LessonSectionType.SOURCE_EXCERPT, null, "Trích nguyên văn chiếu dời đô.", 2),
            Section(LessonSectionType.REFLECTION, null, "Em nghĩ gì về việc dời đô?", 3),
            Section(LessonSectionType.IMAGE, "Hình", "Chú thích hình.", 4),
            Section(LessonSectionType.READING, "Bài đọc một", "Phần một.", 0),
        };

        var texts = SpeechWarmUpTexts.ForReadingLesson("Câu một của bài. Câu hai của bài!", sections);

        Assert.Equal(["Câu một của bài.", "Câu hai của bài!", "Phần một.", "Phần hai.", "Hết phần hai."], texts);
    }

    [Fact]
    public void Markdown_is_removed_before_splitting_and_repeated_empty_or_too_long_sentences_are_dropped()
    {
        var tooLong = new string('a', 1001) + ".";
        var sections = new[]
        {
            Section(LessonSectionType.READING, null, "## Giới thiệu\n\nHội Gióng là **lễ hội** lớn. Hội Gióng là *lễ hội* lớn.", 1),
            Section(LessonSectionType.READING, null, "   ", 2),
            Section(LessonSectionType.READING, null, tooLong + " Câu ngắn.", 3),
        };

        var texts = SpeechWarmUpTexts.ForReadingLesson("Hội Gióng là lễ hội lớn.", sections);

        // Tiêu đề Markdown thành một câu riêng; câu trùng chỉ giữ một lần; câu dài quá 1000 ký tự bị bỏ.
        Assert.Equal(["Hội Gióng là lễ hội lớn.", "Giới thiệu.", "Câu ngắn."], texts);
    }

    [Fact]
    public void A_lesson_without_main_content_or_reading_sections_gives_nothing()
    {
        Assert.Empty(SpeechWarmUpTexts.ForReadingLesson(null, []));
        Assert.Empty(SpeechWarmUpTexts.ForReadingLesson("  ", [Section(LessonSectionType.MILESTONE, null, "Chỉ một mốc.", 1)]));
    }

    [Fact]
    public void The_allowed_texts_of_a_level_two_or_three_student_cover_the_main_content_and_reading_sections_only()
    {
        var sections = new[]
        {
            Section(LessonSectionType.READING, null, "Phần bài đọc thêm. Câu hai của phần.", 1),
            Section(LessonSectionType.MILESTONE, null, "Mốc không được đọc.", 2),
        };

        var allowed = new AllowedSpeechTextsBuilder().AddReadingLesson("Câu một của bài. Câu hai của bài!", sections).Build();

        Assert.True(allowed.Contains("Câu một của bài."));
        Assert.True(allowed.Contains("Câu hai của phần."));
        // Web và app tách câu hơi khác máy chủ: một đoạn nằm trong nội dung bài cũng được nhận.
        Assert.True(allowed.Contains("Phần bài đọc thêm. Câu hai của phần."));
        Assert.True(allowed.Contains("Câu một của bài. Câu hai của bài!"));
        Assert.False(allowed.Contains("Mốc không được đọc."));
        Assert.False(allowed.Contains("Một câu lạ không thuộc bài nào."));
    }
}
