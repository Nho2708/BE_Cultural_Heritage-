using DSVHVN.Application.Speech;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Lời đọc to của bài chơi cấp 1 phải trùng từng ký tự với lời web đọc thì tiếng tạo trước mới dùng được. Các câu mẫu ở đây giống hệt câu mẫu trong
/// kiểm thử lời đọc của web (bài chơi cấp 1), đổi bên nào thì đổi cả hai.
/// </summary>
public sealed class LevelOneSpeechTests
{
    [Fact]
    public void A_single_choice_question_reads_the_blank_as_cho_trong_then_lettered_options_then_the_instruction()
    {
        var speech = LevelOneSpeech.Question(QuestionType.SINGLE_CHOICE, "Vua nào dời đô về ___?", ["Lý Thái Tổ", null, "Trần Hưng Đạo"], [], []);

        Assert.Equal("Vua nào dời đô về chỗ trống? A: Lý Thái Tổ. B: hình. C: Trần Hưng Đạo. Em chọn câu trả lời nhé.", speech);
    }

    [Fact]
    public void True_false_and_image_choice_questions_do_not_read_their_options()
    {
        Assert.Equal("Hội Gióng có ở Phù Đổng. Em chạm Đúng hoặc Sai.",
            LevelOneSpeech.Question(QuestionType.TRUE_FALSE, "Hội Gióng có ở Phù Đổng", ["Đúng", "Sai"], [], []));
        Assert.Equal("Hình nào là đình làng? Em chạm vào hình đúng.",
            LevelOneSpeech.Question(QuestionType.IMAGE_CHOICE, "  Hình nào là đình làng?  ", ["Đình làng", "Chùa"], [], []));
    }

    [Fact]
    public void Matching_reads_the_names_and_ordering_reads_the_steps_in_the_order_shown()
    {
        Assert.Equal("Nối hình với tên. Chùa Một Cột. Văn Miếu! Em chạm một hình, rồi chạm tên của hình đó.",
            LevelOneSpeech.Question(QuestionType.MATCHING, "Nối hình với tên", [], ["Chùa Một Cột", "Văn Miếu!"], []));
        Assert.Equal("Xếp các bước làm tranh Đông Hồ: Vẽ mẫu. Khắc ván. Em chạm các bước theo đúng thứ tự.",
            LevelOneSpeech.Question(QuestionType.ORDERING, "Xếp các bước làm tranh Đông Hồ:", [], [], ["Vẽ mẫu", " ", "Khắc ván"]));
    }

    [Fact]
    public void Feedback_praises_by_question_position_and_reveals_with_the_explanation()
    {
        Assert.Equal(["Giỏi quá! Đình làng có mái cong.", "Đáp án đúng ở đây nè! Đình làng có mái cong."],
            LevelOneSpeech.Feedback("  Đình làng có mái cong. ", 2));
        Assert.Equal(["Đúng rồi! Đúng vậy.", "Đáp án đúng ở đây nè! Đúng vậy."], LevelOneSpeech.Feedback("Đúng vậy.", 4));
    }

    [Fact]
    public void Feedback_without_a_known_position_gives_every_praise_and_without_an_explanation_gives_nothing()
    {
        Assert.Equal(
            ["Đúng rồi! Đúng vậy.", "Giỏi quá! Đúng vậy.", "Chính xác! Đúng vậy.", "Đáp án đúng ở đây nè! Đúng vậy."],
            LevelOneSpeech.Feedback("Đúng vậy.", null));
        Assert.Empty(LevelOneSpeech.Feedback("   ", 1));
        Assert.Empty(LevelOneSpeech.Feedback(null, null));
    }
}
