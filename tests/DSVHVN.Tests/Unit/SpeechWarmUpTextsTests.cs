using DSVHVN.Application.Speech;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Lessons;

namespace DSVHVN.Tests.Unit;

/// <summary>Chọn đoạn chữ để tạo trước giọng đọc cho bài học và bài chơi cấp 1: câu của Kể chuyện, tiêu đề, chú thích hình, ý chính, câu mang về nhà, câu hỏi và chữ trên thẻ hình.</summary>
public sealed class SpeechWarmUpTextsTests
{
    private static LessonSection Section(LessonSectionType type, string? title, string? body, int order) =>
        new() { Id = order, SectionType = type, Title = title, Body = body, SortOrder = order };

    [Fact]
    public void A_lesson_gives_story_titles_and_sentences_image_captions_key_points_and_the_take_home_question_in_reading_order()
    {
        var sections = new[]
        {
            // Cố ý đưa phần có thứ tự lớn trước để kiểm thứ tự đọc theo SortOrder.
            Section(LessonSectionType.IMAGE, "Hình cổng làng", "Cổng làng xưa. Mái ngói rêu phong.", 3),
            Section(LessonSectionType.STORY, "Cậu bé làng Gióng", "Ngày xưa có một cậu bé. Cậu lớn nhanh như thổi!", 1),
            Section(LessonSectionType.STORY, "Roi sắt", "Cậu nhổ tre làm gậy.", 2),
        };

        var texts = SpeechWarmUpTexts.ForLesson(sections, ["Hội Gióng tổ chức hằng năm.", "Em nên nhớ ngày hội."], "Ông bà đã từng xem Hội Gióng chưa?");

        Assert.Equal(
        [
            "Cậu bé làng Gióng", "Ngày xưa có một cậu bé.", "Cậu lớn nhanh như thổi!",
            "Roi sắt", "Cậu nhổ tre làm gậy.",
            "Cổng làng xưa.", "Mái ngói rêu phong.",
            "Hội Gióng tổ chức hằng năm.", "Em nên nhớ ngày hội.",
            "Ông bà đã từng xem Hội Gióng chưa?",
        ], texts);
    }

    [Fact]
    public void Sections_that_level_one_does_not_read_aloud_are_skipped_and_image_titles_are_not_read()
    {
        var sections = new[]
        {
            Section(LessonSectionType.READING, "Bài đọc", "Không đọc phần này.", 1),
            Section(LessonSectionType.MILESTONE, "Năm 1010", "Dời đô.", 2),
            Section(LessonSectionType.SOURCE_EXCERPT, "Chiếu dời đô", "Trích.", 3),
            Section(LessonSectionType.REFLECTION, "Nghĩ gì", "Em nghĩ sao?", 4),
            Section(LessonSectionType.IMAGE, "Tiêu đề hình", "Chú thích hình.", 5),
        };

        Assert.Equal(["Chú thích hình."], SpeechWarmUpTexts.ForLesson(sections, [], null));
    }

    [Fact]
    public void Markdown_marks_are_removed_before_splitting_and_repeated_or_empty_texts_are_dropped()
    {
        var sections = new[]
        {
            Section(LessonSectionType.STORY, "**Hội Gióng**", "Cậu bé *rất* khỏe. Cậu bé *rất* khỏe.", 1),
            Section(LessonSectionType.STORY, "", "   ", 2),
        };

        var texts = SpeechWarmUpTexts.ForLesson(sections, ["", "Hội Gióng"], "  ");

        Assert.Equal(["Hội Gióng", "Cậu bé rất khỏe."], texts);
    }

    [Fact]
    public void A_sentence_longer_than_the_limit_is_not_queued()
    {
        var tooLong = new string('a', 1001) + ".";
        var sections = new[] { Section(LessonSectionType.STORY, "Tên", tooLong + " Câu ngắn.", 1) };

        Assert.Equal(["Tên", "Câu ngắn."], SpeechWarmUpTexts.ForLesson(sections, [], null));
    }

    [Fact]
    public void A_quiz_gives_content_explanation_and_image_labels_only_for_image_choice_questions()
    {
        var questions = new[]
        {
            new SpeechWarmUpTexts.QuestionSource("Hình nào là đình làng?", "Đình làng có mái cong.", QuestionType.IMAGE_CHOICE, ["Đình làng", "Chùa Một Cột", " "]),
            new SpeechWarmUpTexts.QuestionSource("Hội Gióng ở đâu?", null, QuestionType.SINGLE_CHOICE, ["Phù Đổng", "Hội An"]),
            new SpeechWarmUpTexts.QuestionSource("Đúng hay sai: Cậu bé lớn nhanh.", "Đúng vậy.", QuestionType.TRUE_FALSE, ["Đúng", "Sai"]),
            new SpeechWarmUpTexts.QuestionSource("Hình nào là đình làng?", "Đình làng có mái cong.", QuestionType.IMAGE_CHOICE, ["Đình làng"]),
        };

        var texts = SpeechWarmUpTexts.ForQuiz(questions);

        Assert.Equal(
        [
            "Hình nào là đình làng?", "Đình làng có mái cong.", "Đình làng", "Chùa Một Cột",
            "Hội Gióng ở đâu?",
            "Đúng hay sai: Cậu bé lớn nhanh.", "Đúng vậy.",
        ], texts);
    }
}
