using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Questions;
using DSVHVN.Tests.Support;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Chấm từng dạng câu theo quy tắc Cách chấm từng dạng câu: số phần đúng trên tổng số phần, không trừ điểm, bỏ trống 0 điểm, so chữ điền từ
/// không phân biệt hoa thường nhưng phân biệt dấu, làm tròn điểm 2 chữ số (nửa lên); và chấm thử đáp án chuẩn dùng để kiểm câu hợp lệ.
/// </summary>
public sealed class QuestionGraderTests
{
    private static Question Q(SaveQuestionRequest request)
    {
        var result = QuestionBuilder.Build(request, QuestionSamples.Library);
        Assert.True(result.IsValid, string.Join("; ", result.ErrorTexts));
        return result.Question!;
    }

    private static GradeResult Grade(Question q, QuestionSubmission? s) => QuestionGrader.Grade(q.QuestionType!.Value, q.Answers, s);

    // ---- Chọn một phương án: một phần ----

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(-1, false)]
    [InlineData(4, false)]
    public void Single_choice_is_one_part_right_or_wrong(int selected, bool expected)
    {
        var result = Grade(Q(QuestionSamples.SingleChoice()), new QuestionSubmission(SelectedIndex: selected));
        Assert.Equal((1, expected), (result.PartsTotal, result.IsCorrect));
        Assert.Equal(expected ? 1 : 0, result.PartsCorrect);
    }

    [Fact]
    public void Choice_types_with_no_answer_score_nothing()
    {
        foreach (var sample in new[] { QuestionSamples.SingleChoice(), QuestionSamples.TrueFalse(), QuestionSamples.ImageChoice(), QuestionSamples.SourceReading() })
        {
            var q = Q(sample);
            Assert.Equal((1, 0), (Grade(q, null).PartsTotal, Grade(q, null).PartsCorrect));
            Assert.Equal(0, Grade(q, new QuestionSubmission()).PartsCorrect);
        }
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]
    [InlineData(false, 1, true)]
    [InlineData(false, 0, false)]
    public void True_false_grades_the_chosen_side(bool answerIsTrue, int selected, bool expected)
    {
        var q = Q(QuestionSamples.TrueFalse(answer: answerIsTrue));
        Assert.Equal(expected, Grade(q, new QuestionSubmission(SelectedIndex: selected)).IsCorrect);
    }

    [Fact]
    public void Image_choice_and_source_reading_are_one_part_each()
    {
        var image = Q(QuestionSamples.ImageChoice());
        Assert.True(Grade(image, new QuestionSubmission(SelectedIndex: 1)).IsCorrect);
        Assert.False(Grade(image, new QuestionSubmission(SelectedIndex: 0)).IsCorrect);
        var reading = Q(QuestionSamples.SourceReading());
        Assert.True(Grade(reading, new QuestionSubmission(SelectedIndex: 0)).IsCorrect);
        Assert.False(Grade(reading, new QuestionSubmission(SelectedIndex: 2)).IsCorrect);
        Assert.Equal(1, Grade(reading, null).PartsTotal);
    }

    // ---- Ghép cặp ----

    [Fact]
    public void Matching_scores_each_correct_pair()
    {
        var q = Q(QuestionSamples.Matching());
        Assert.Equal(3, Grade(q, null).PartsTotal);
        Assert.Equal(3, Grade(q, new QuestionSubmission(Matches: [0, 1, 2])).PartsCorrect);
        Assert.True(Grade(q, new QuestionSubmission(Matches: [0, 1, 2])).IsCorrect);
        Assert.Equal([true, false, false], Grade(q, new QuestionSubmission(Matches: [0, 2, 1])).Parts);
        Assert.Equal(0, Grade(q, new QuestionSubmission(Matches: [1, 2, 0])).PartsCorrect);
        // Cặp chưa nối không được; nối thiếu vẫn được các cặp đúng.
        Assert.Equal([true, false, false], Grade(q, new QuestionSubmission(Matches: [0, null])).Parts);
        Assert.Equal(0, Grade(q, null).PartsCorrect);
    }

    [Fact]
    public void Matching_counts_a_right_side_only_once()
    {
        var q = Q(QuestionSamples.Matching());
        // Vế phải 0 được nối hai lần: chỉ lần đầu tính (cặp 0 đúng), lần sau không tính dù vế trái 1 vốn không khớp.
        var result = Grade(q, new QuestionSubmission(Matches: [0, 0, 2]));
        Assert.Equal([true, false, true], result.Parts);
        // Vế phải 1 nối cho vế trái 0 trước, rồi vế trái 1 nối vế phải 1: vế phải 1 đã dùng nên cặp 1 không được.
        Assert.Equal([false, false, true], Grade(q, new QuestionSubmission(Matches: [1, 1, 2])).Parts);
    }

    // ---- Sắp xếp ----

    [Fact]
    public void Ordering_scores_each_item_in_its_exact_position()
    {
        var q = Q(QuestionSamples.Ordering(items: 4));
        Assert.Equal(4, Grade(q, null).PartsTotal);
        Assert.True(Grade(q, new QuestionSubmission(Order: [0, 1, 2, 3])).IsCorrect);
        // Đổi chỗ hai mục đầu: hai mục sau vẫn đúng vị trí tuyệt đối.
        var swapped = Grade(q, new QuestionSubmission(Order: [1, 0, 2, 3]));
        Assert.Equal((2, 4), (swapped.PartsCorrect, swapped.PartsTotal));
        // Đảo ngược hoàn toàn: không mục nào đúng vị trí.
        Assert.Equal(0, Grade(q, new QuestionSubmission(Order: [3, 2, 1, 0])).PartsCorrect);
    }

    [Fact]
    public void Ordering_that_was_never_touched_scores_nothing_but_the_original_order_counts_once_arranged()
    {
        var q = Q(QuestionSamples.Ordering(items: 3));
        Assert.Equal(0, Grade(q, null).PartsCorrect);
        Assert.Equal(0, Grade(q, new QuestionSubmission(Order: [])).PartsCorrect);
        Assert.Equal(3, Grade(q, new QuestionSubmission(Order: [0, 1, 2])).PartsCorrect);
        // Mục lặp hoặc ngoài phạm vi không được tính hai lần.
        Assert.Equal([true, false, false], Grade(q, new QuestionSubmission(Order: [0, 0, 9])).Parts);
    }

    // ---- Điền từ ----

    [Theory]
    [InlineData("Lý Thái Tổ", true)]
    [InlineData("  lý   thái  tổ  ", true)]
    [InlineData("LÝ THÁI TỔ", true)]
    [InlineData("Lý Công Uẩn", true)]
    [InlineData("Ly Thai To", false)]               // phân biệt dấu
    [InlineData("Lý Thái", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void Fill_blank_ignores_case_and_extra_spaces_but_not_accents(string? typed, bool expected)
    {
        var result = Grade(Q(QuestionSamples.FillBlank()), new QuestionSubmission(Blanks: [typed]));
        Assert.Equal((1, expected), (result.PartsTotal, result.IsCorrect));
    }

    [Fact]
    public void Fill_blank_matches_composed_and_decomposed_vietnamese_letters_alike()
    {
        var decomposed = "Lý Thái Tổ".Normalize(System.Text.NormalizationForm.FormD);
        Assert.NotEqual("Lý Thái Tổ", decomposed);
        Assert.True(Grade(Q(QuestionSamples.FillBlank()), new QuestionSubmission(Blanks: [decomposed])).IsCorrect);
    }

    [Fact]
    public void Fill_blank_scores_each_blank_separately()
    {
        var q = Q(QuestionSamples.TwoBlanks());
        Assert.Equal(2, Grade(q, null).PartsTotal);
        Assert.Equal([true, false], Grade(q, new QuestionSubmission(Blanks: ["lý thái tổ", "Hoa Lư"])).Parts);
        Assert.Equal([false, true], Grade(q, new QuestionSubmission(Blanks: [null, "THĂNG LONG"])).Parts);
        Assert.True(Grade(q, new QuestionSubmission(Blanks: ["Lý Thái Tổ", "Thăng Long"])).IsCorrect);
        Assert.Equal(0, Grade(q, new QuestionSubmission(Blanks: ["Thăng Long", "Lý Thái Tổ"])).PartsCorrect);  // chữ đúng nhưng sai chỗ
    }

    [Fact]
    public void Fill_blank_with_a_word_bank_matches_the_chosen_word_and_never_a_distractor()
    {
        var q = Q(QuestionSamples.FillBlank(wordBank: true));
        Assert.True(Grade(q, new QuestionSubmission(Blanks: ["Lý Thái Tổ"])).IsCorrect);
        Assert.False(Grade(q, new QuestionSubmission(Blanks: ["Lê Lợi"])).IsCorrect);          // từ gây nhiễu
    }

    // ---- Nhiều mệnh đề ----

    [Fact]
    public void Multi_true_false_scores_each_statement_and_unchosen_ones_do_not_count()
    {
        var q = Q(QuestionSamples.Statements());        // đúng, sai, đúng
        Assert.Equal(3, Grade(q, null).PartsTotal);
        Assert.True(Grade(q, new QuestionSubmission(Statements: [true, false, true])).IsCorrect);
        Assert.Equal([true, true, false], Grade(q, new QuestionSubmission(Statements: [true, false, false])).Parts);
        Assert.Equal([false, false, false], Grade(q, new QuestionSubmission(Statements: [false, true, false])).Parts);
        Assert.Equal([true, false, false], Grade(q, new QuestionSubmission(Statements: [true, null])).Parts);
        Assert.Equal(0, Grade(q, null).PartsCorrect);
    }

    // ---- Điểm của câu ----

    [Theory]
    [InlineData(1.0, 3, 3, 1.0)]
    [InlineData(1.0, 2, 3, 0.67)]
    [InlineData(1.0, 1, 3, 0.33)]
    [InlineData(1.0, 0, 3, 0.0)]
    [InlineData(2.0, 1, 4, 0.5)]
    [InlineData(0.5, 1, 3, 0.17)]          // 0,1666… làm tròn lên
    [InlineData(1.0, 1, 8, 0.13)]          // 0,125 làm tròn nửa lên
    [InlineData(5.0, 2, 3, 3.33)]
    public void The_score_of_a_question_is_max_points_times_correct_parts_over_total_parts_rounded_to_two_places(double point, int correct, int total, double expected)
    {
        var parts = Enumerable.Range(0, total).Select(i => i < correct).ToList();
        Assert.Equal((decimal)expected, QuestionGrader.Score((decimal)point, new GradeResult(parts)));
    }

    [Fact]
    public void A_question_with_no_parts_scores_zero_and_is_never_correct()
    {
        var none = new GradeResult([]);
        Assert.Equal(0m, QuestionGrader.Score(1m, none));
        Assert.False(none.IsCorrect);
        Assert.Equal(0m, none.Fraction);
    }

    // ---- Chấm thử đáp án chuẩn ----

    [Fact]
    public void The_answer_key_of_every_sample_scores_every_part_and_a_blank_answer_scores_none()
    {
        foreach (var sample in QuestionSamples.OfEveryType().Append(QuestionSamples.TwoBlanks()).Append(QuestionSamples.FillBlank(wordBank: true)))
        {
            var q = Q(sample);
            var key = QuestionGrader.AnswerKey(q.QuestionType!.Value, q.Answers);
            var full = Grade(q, key);
            Assert.True(full.IsCorrect, $"{sample.QuestionType}: đáp án chuẩn không đúng đủ");
            Assert.Equal(full.PartsTotal, QuestionGrader.PartsTotal(q.QuestionType.Value, q.Answers));
            Assert.Equal(0, Grade(q, null).PartsCorrect);
            Assert.Null(QuestionGrader.SelfCheck(q.QuestionType.Value, q.Answers));
        }
    }

    [Theory]
    [InlineData(QuestionType.SINGLE_CHOICE, 1)]
    [InlineData(QuestionType.TRUE_FALSE, 1)]
    [InlineData(QuestionType.IMAGE_CHOICE, 1)]
    [InlineData(QuestionType.MATCHING, 3)]
    [InlineData(QuestionType.ORDERING, 4)]
    [InlineData(QuestionType.FILL_BLANK, 1)]
    [InlineData(QuestionType.MULTI_TRUE_FALSE, 3)]
    [InlineData(QuestionType.SOURCE_READING, 1)]
    public void The_number_of_parts_follows_the_structure_of_each_type(QuestionType type, int parts)
    {
        var q = Q(QuestionSamples.OfEveryType().Single(s => s.QuestionType == type));
        Assert.Equal(parts, QuestionGrader.PartsTotal(type, q.Answers));
    }

    [Fact]
    public void The_trial_check_rejects_a_broken_answer_structure()
    {
        // Không có phương án đúng: đáp án chuẩn không chấm ra đúng.
        var broken = new List<Answer> { new() { Content = "A", SortOrder = 1 }, new() { Content = "B", SortOrder = 2 } };
        Assert.NotNull(QuestionGrader.SelfCheck(QuestionType.SINGLE_CHOICE, broken));
        // Không có phần nào để chấm.
        Assert.NotNull(QuestionGrader.SelfCheck(QuestionType.MATCHING, []));
        Assert.NotNull(QuestionGrader.SelfCheck(QuestionType.FILL_BLANK, [new Answer { Content = "x", IsCorrect = false, SortOrder = 1 }]));
    }
}
