using DSVHVN.Application.Attempts;
using DSVHVN.Application.Questions;
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
    public void A_quiz_gives_the_spoken_question_feedback_by_position_content_explanation_and_image_labels_only_for_image_choice()
    {
        var questions = new[]
        {
            new SpeechWarmUpTexts.QuestionSource("Hình nào là đình làng?", "Đình làng có mái cong.", QuestionType.IMAGE_CHOICE, ["Đình làng", "Chùa Một Cột", " "], 1),
            new SpeechWarmUpTexts.QuestionSource("Hội Gióng ở đâu?", null, QuestionType.SINGLE_CHOICE, ["Phù Đổng", "Hội An"], 2),
            new SpeechWarmUpTexts.QuestionSource("Đúng hay sai: Cậu bé lớn nhanh.", "Đúng vậy.", QuestionType.TRUE_FALSE, ["Đúng", "Sai"], 3),
            new SpeechWarmUpTexts.QuestionSource("Hình nào là đình làng?", "Đình làng có mái cong.", QuestionType.IMAGE_CHOICE, ["Đình làng"], 4),
        };

        var texts = SpeechWarmUpTexts.ForQuiz(questions);

        Assert.Equal(
        [
            "Hình nào là đình làng? Em chạm vào hình đúng.", "Đúng rồi! Đình làng có mái cong.", "Đáp án đúng ở đây nè! Đình làng có mái cong.",
            "Hình nào là đình làng?", "Đình làng có mái cong.", "Đình làng", "Chùa Một Cột",
            "Hội Gióng ở đâu? A: Phù Đổng. B: Hội An. Em chọn câu trả lời nhé.", "Hội Gióng ở đâu?",
            "Đúng hay sai: Cậu bé lớn nhanh. Em chạm Đúng hoặc Sai.", "Chính xác! Đúng vậy.", "Đáp án đúng ở đây nè! Đúng vậy.",
            "Đúng hay sai: Cậu bé lớn nhanh.", "Đúng vậy.",
        ], texts);
    }

    [Fact]
    public void Matching_and_ordering_are_not_spoken_whole_at_publish_because_each_attempt_shuffles_them()
    {
        var questions = new[]
        {
            new SpeechWarmUpTexts.QuestionSource("Nối hình với tên", "Chùa Một Cột ở Hà Nội.", QuestionType.MATCHING, ["Hình chùa"]),
            new SpeechWarmUpTexts.QuestionSource("Xếp các bước", null, QuestionType.ORDERING, ["Vẽ mẫu", "Khắc ván"]),
        };

        Assert.Equal(
        [
            "Đúng rồi! Chùa Một Cột ở Hà Nội.", "Giỏi quá! Chùa Một Cột ở Hà Nội.", "Chính xác! Chùa Một Cột ở Hà Nội.",
            "Đáp án đúng ở đây nè! Chùa Một Cột ở Hà Nội.", "Nối hình với tên", "Chùa Một Cột ở Hà Nội.",
            "Xếp các bước",
        ], SpeechWarmUpTexts.ForQuiz(questions));
    }

    [Fact]
    public void An_attempt_gives_each_spoken_question_in_the_order_shown_with_its_shuffled_names_and_steps()
    {
        var questions = new[]
        {
            Shown(1, QuestionType.MATCHING, "Nối hình với tên", rights: ["Văn Miếu", "Chùa Một Cột"]),
            Shown(2, QuestionType.SINGLE_CHOICE, "Hội Gióng ở đâu?", options: ["Phù Đổng", "Hội An"]),
            Shown(3, QuestionType.ORDERING, "Xếp các bước", items: ["Khắc ván", "Vẽ mẫu"]),
        };

        Assert.Equal(
        [
            "Nối hình với tên. Văn Miếu. Chùa Một Cột. Em chạm một hình, rồi chạm tên của hình đó.",
            "Hội Gióng ở đâu? A: Phù Đổng. B: Hội An. Em chọn câu trả lời nhé.",
            "Xếp các bước. Khắc ván. Vẽ mẫu. Em chạm các bước theo đúng thứ tự.",
        ], SpeechWarmUpTexts.ForAttempt(questions));
    }

    private static StudentAttemptQuestionDto Shown(
        int position, QuestionType type, string content, string[]? options = null, string[]? rights = null, string[]? items = null) =>
        new(position, position, null, type, type.ToString(), content, null, null, null,
            (options ?? []).Select(o => new QuestionPreviewOptionDto(o, null)).ToList(), [], rights ?? [], items ?? [], 0, [], [], 1, false, null);
}
