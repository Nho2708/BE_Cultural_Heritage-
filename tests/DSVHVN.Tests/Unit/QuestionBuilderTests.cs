using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using DSVHVN.Tests.Support;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Kiểm hợp lệ và dựng câu hỏi 8 dạng: cấu trúc đáp án từng dạng (số phần tử, độ dài, không trùng, đúng một đáp án đúng...), cấp phù hợp theo bảng
/// dạng câu theo cấp, ảnh phải thuộc thư viện của di sản, và các dòng <c>answers</c> sinh ra đúng lược đồ CSDL v4.
/// </summary>
public sealed class QuestionBuilderTests
{
    private static QuestionBuildResult Build(SaveQuestionRequest request) => QuestionBuilder.Build(request, QuestionSamples.Library);

    private static void AssertFails(SaveQuestionRequest request, string field)
    {
        var result = Build(request);
        Assert.False(result.IsValid);
        Assert.Contains(field, result.ErrorTexts.Keys);
    }

    // ---- Bảng dạng câu theo cấp ----

    [Fact]
    public void The_type_table_gives_four_four_and_three_types_to_the_three_levels()
    {
        Assert.Equal(8, QuestionTypes.All.Count);
        Assert.Equal([QuestionType.TRUE_FALSE, QuestionType.IMAGE_CHOICE, QuestionType.MATCHING, QuestionType.ORDERING], QuestionTypes.ForLevel(1));
        Assert.Equal([QuestionType.SINGLE_CHOICE, QuestionType.TRUE_FALSE, QuestionType.ORDERING, QuestionType.FILL_BLANK], QuestionTypes.ForLevel(2));
        Assert.Equal([QuestionType.SINGLE_CHOICE, QuestionType.MULTI_TRUE_FALSE, QuestionType.SOURCE_READING], QuestionTypes.ForLevel(3));
        // Ba dạng dùng ở hai cấp, năm dạng ở một cấp.
        Assert.Equal(3, QuestionTypes.All.Count(t => t.MaxLevel > t.MinLevel));
        Assert.Equal(5, QuestionTypes.All.Count(t => t.MaxLevel == t.MinLevel));
    }

    [Fact]
    public void A_valid_sample_of_every_type_builds_and_stores_the_default_levels_of_its_type()
    {
        foreach (var sample in QuestionSamples.OfEveryType())
        {
            var result = Build(sample);
            Assert.True(result.IsValid, $"{sample.QuestionType}: {string.Join("; ", result.ErrorTexts)}");
            var info = QuestionTypes.Of(sample.QuestionType!.Value);
            Assert.Equal((info.MinLevel, info.MaxLevel), (result.Question!.LevelMin, result.Question.LevelMax));
            Assert.Equal(sample.QuestionType, result.Question.QuestionType);
            Assert.NotEmpty(result.Question.Answers);
        }
    }

    // ---- Chung ----

    [Fact]
    public void Common_fields_are_required_and_bounded()
    {
        var ok = QuestionSamples.SingleChoice();
        AssertFails(ok with { QuestionType = null }, "questionType");
        AssertFails(ok with { HeritageId = null }, "heritageId");
        AssertFails(ok with { Content = "  " }, "content");
        AssertFails(ok with { Content = "Ngắn quá" }, "content");
        AssertFails(ok with { Content = new string('a', 1001) }, "content");
        AssertFails(ok with { Explanation = null }, "explanation");
        AssertFails(ok with { Explanation = new string('a', 2001) }, "explanation");
        AssertFails(ok with { DifficultyLevel = null }, "difficultyLevel");
    }

    [Theory]
    [InlineData(QuestionType.SINGLE_CHOICE, 1, 3)]
    [InlineData(QuestionType.SINGLE_CHOICE, 1, 2)]
    [InlineData(QuestionType.IMAGE_CHOICE, 1, 2)]
    [InlineData(QuestionType.MULTI_TRUE_FALSE, 2, 3)]
    [InlineData(QuestionType.SOURCE_READING, 2, 3)]
    [InlineData(QuestionType.FILL_BLANK, 2, 3)]
    [InlineData(QuestionType.TRUE_FALSE, 2, 3)]
    public void Levels_outside_the_levels_of_the_type_are_refused(QuestionType type, int min, int max)
    {
        var sample = QuestionSamples.OfEveryType().Single(s => s.QuestionType == type);
        AssertFails(sample with { LevelMin = min, LevelMax = max }, "levelMin");
    }

    [Fact]
    public void Levels_must_be_ordered_and_between_one_and_three()
    {
        var ok = QuestionSamples.SingleChoice();
        AssertFails(ok with { LevelMin = 3, LevelMax = 2 }, "levelMin");
        AssertFails(ok with { LevelMin = 0, LevelMax = 3 }, "levelMin");
        AssertFails(ok with { LevelMin = 2, LevelMax = 4 }, "levelMax");
        // Thu hẹp cấp trong các cấp của dạng được.
        var narrowed = Build(ok with { LevelMin = 3, LevelMax = 3 });
        Assert.Equal((3, 3), (narrowed.Question!.LevelMin, narrowed.Question.LevelMax));
    }

    // ---- Một đáp án đúng ----

    [Fact]
    public void Single_choice_needs_four_distinct_options_with_exactly_one_correct()
    {
        var ok = QuestionSamples.SingleChoice();
        AssertFails(ok with { Options = ok.Options!.Take(3).ToList() }, "options");
        AssertFails(ok with { Options = null }, "options");
        AssertFails(ok with { Options = [.. ok.Options!, new("Năm 2020", null, false)] }, "options");
        AssertFails(ok with { Options = ok.Options!.Select(o => o with { IsCorrect = false }).ToList() }, "options");
        AssertFails(ok with { Options = ok.Options!.Select(o => o with { IsCorrect = true }).ToList() }, "options");
        AssertFails(ok with { Options = [new("A", null, true), new("a ", null, false), new("C", null, false), new("D", null, false)] }, "options[1].content");
        AssertFails(ok with { Options = [new(" ", null, true), new("B", null, false), new("C", null, false), new("D", null, false)] }, "options[0].content");
        AssertFails(ok with { Options = [new(new string('x', 1001), null, true), new("B", null, false), new("C", null, false), new("D", null, false)] }, "options[0].content");
    }

    [Fact]
    public void Single_choice_rows_follow_the_options_in_order()
    {
        var rows = Build(QuestionSamples.SingleChoice()).Question!.Answers.OrderBy(a => a.SortOrder).ToList();
        Assert.Equal(["Năm 1999", "Năm 2010", "Năm 2003", "Năm 2015"], rows.Select(a => a.Content));
        Assert.Equal([false, true, false, false], rows.Select(a => a.IsCorrect));
        Assert.Equal([1, 2, 3, 4], rows.Select(a => a.SortOrder!.Value));
        Assert.All(rows, a => Assert.True(a.ImageUrl is null && a.MatchContent is null && a.CorrectPosition is null && a.BlankIndex is null));
    }

    // ---- Đúng/Sai ----

    [Fact]
    public void True_false_stores_two_fixed_options_and_marks_the_right_one()
    {
        var yes = Build(QuestionSamples.TrueFalse(answer: true)).Question!.Answers.OrderBy(a => a.SortOrder).ToList();
        Assert.Equal(["Đúng", "Sai"], yes.Select(a => a.Content));
        Assert.Equal([true, false], yes.Select(a => a.IsCorrect));
        var no = Build(QuestionSamples.TrueFalse(answer: false)).Question!.Answers.OrderBy(a => a.SortOrder).ToList();
        Assert.Equal([false, true], no.Select(a => a.IsCorrect));
        Assert.Equal(QuestionSamples.ImageA, Build(QuestionSamples.TrueFalse()).Question!.ImageUrl);
    }

    [Fact]
    public void True_false_usable_at_level_one_must_have_a_picture_but_level_two_only_may_have_one()
    {
        AssertFails(QuestionSamples.TrueFalse(image: null), "imageUrl");
        AssertFails(QuestionSamples.TrueFalse(image: null, levelMin: 1, levelMax: 1), "imageUrl");
        var levelTwo = Build(QuestionSamples.TrueFalse(image: null, levelMin: 2, levelMax: 2));
        Assert.True(levelTwo.IsValid);
        Assert.Null(levelTwo.Question!.ImageUrl);
        AssertFails(QuestionSamples.TrueFalse(answer: true) with { AnswerIsTrue = null }, "answerIsTrue");
    }

    // ---- Ảnh thuộc thư viện ----

    [Fact]
    public void Every_picture_must_be_in_the_library_of_the_heritage()
    {
        AssertFails(QuestionSamples.TrueFalse(image: "https://nguoi-la.example/anh.jpg"), "imageUrl");
        AssertFails(QuestionSamples.ImageChoice() with
        {
            Options = [new("Một", "https://nguoi-la.example/1.jpg", true), new("Hai", QuestionSamples.ImageB, false), new("Ba", QuestionSamples.ImageC, false)],
        }, "options[0].imageUrl");
        AssertFails(QuestionSamples.Matching() with
        {
            Pairs = [new(null, "https://nguoi-la.example/1.jpg", "Một"), new(null, QuestionSamples.ImageB, "Hai"), new(null, QuestionSamples.ImageC, "Ba")],
        }, "pairs[0].leftImageUrl");
        // Thư viện rỗng: không ảnh nào hợp lệ.
        Assert.False(QuestionBuilder.Build(QuestionSamples.TrueFalse(), new HashSet<string>()).IsValid);
    }

    // ---- Chọn hình đúng ----

    [Fact]
    public void Image_choice_needs_three_or_four_different_pictures_with_labels_and_one_correct_card()
    {
        var ok = QuestionSamples.ImageChoice();
        Assert.True(Build(ok with { Options = [.. ok.Options!, new("Điện khác", QuestionSamples.ImageD, false)] }).IsValid);
        AssertFails(ok with { Options = ok.Options!.Take(2).ToList() }, "options");
        AssertFails(ok with { Options = [.. ok.Options!, new("D", QuestionSamples.ImageD, false), new("E", QuestionSamples.ImageA, false)] }, "options");
        AssertFails(ok with { Options = [new("Một", QuestionSamples.ImageA, true), new("Hai", QuestionSamples.ImageA, false), new("Ba", QuestionSamples.ImageC, false)] }, "options[1].imageUrl");
        AssertFails(ok with { Options = [new("Một", null, true), new("Hai", QuestionSamples.ImageB, false), new("Ba", QuestionSamples.ImageC, false)] }, "options[0].imageUrl");
        AssertFails(ok with { Options = [new("", QuestionSamples.ImageA, true), new("Hai", QuestionSamples.ImageB, false), new("Ba", QuestionSamples.ImageC, false)] }, "options[0].content");
        AssertFails(ok with { Options = [new(new string('n', 51), QuestionSamples.ImageA, true), new("Hai", QuestionSamples.ImageB, false), new("Ba", QuestionSamples.ImageC, false)] }, "options[0].content");
        AssertFails(ok with { Options = [new("Một", QuestionSamples.ImageA, true), new("MỘT", QuestionSamples.ImageB, false), new("Ba", QuestionSamples.ImageC, false)] }, "options[1].content");
        AssertFails(ok with { Options = [new("Một", QuestionSamples.ImageA, true), new("Hai", QuestionSamples.ImageB, true), new("Ba", QuestionSamples.ImageC, false)] }, "options");
    }

    [Fact]
    public void Image_choice_rows_keep_the_picture_and_the_label()
    {
        var rows = Build(QuestionSamples.ImageChoice()).Question!.Answers.OrderBy(a => a.SortOrder).ToList();
        Assert.Equal([QuestionSamples.ImageA, QuestionSamples.ImageB, QuestionSamples.ImageC], rows.Select(a => a.ImageUrl));
        Assert.Equal(["Điện Kính Thiên", "Cổng Đoan Môn", "Khu khảo cổ"], rows.Select(a => a.Content));
        Assert.Equal([false, true, false], rows.Select(a => a.IsCorrect));
    }

    // ---- Ghép cặp ----

    [Fact]
    public void Matching_needs_three_to_five_pairs_with_distinct_sides()
    {
        var ok = QuestionSamples.Matching();
        AssertFails(ok with { Pairs = ok.Pairs!.Take(2).ToList() }, "pairs");
        AssertFails(ok with { Pairs = [.. ok.Pairs!, new("D", null, "Bốn"), new("E", null, "Năm"), new("F", null, "Sáu")] }, "pairs");
        AssertFails(ok with { Pairs = [new(null, QuestionSamples.ImageA, "Một"), new(null, QuestionSamples.ImageA, "Hai"), new("C", null, "Ba")] }, "pairs[1].left");
        AssertFails(ok with { Pairs = [new(null, QuestionSamples.ImageA, "Một"), new(null, QuestionSamples.ImageB, "một"), new("C", null, "Ba")] }, "pairs[1].right");
        AssertFails(ok with { Pairs = [new(null, null, "Một"), new(null, QuestionSamples.ImageB, "Hai"), new("C", null, "Ba")] }, "pairs[0].left");
        AssertFails(ok with { Pairs = [new(null, QuestionSamples.ImageA, ""), new(null, QuestionSamples.ImageB, "Hai"), new("C", null, "Ba")] }, "pairs[0].right");
        AssertFails(ok with { Pairs = [new(null, QuestionSamples.ImageA, new string('v', 51)), new(null, QuestionSamples.ImageB, "Hai"), new("C", null, "Ba")] }, "pairs[0].right");
        Assert.True(Build(ok with { Pairs = [.. ok.Pairs!, new("D", null, "Bốn"), new("E", null, "Năm")] }).IsValid);
    }

    [Fact]
    public void Matching_rows_hold_the_left_and_right_sides_and_are_all_marked_correct()
    {
        var rows = Build(QuestionSamples.Matching()).Question!.Answers.OrderBy(a => a.SortOrder).ToList();
        Assert.Equal([QuestionSamples.ImageA, QuestionSamples.ImageB, null], rows.Select(a => a.ImageUrl));
        Assert.Equal(["Điện Kính Thiên", "Cổng Đoan Môn", "Thủ đô"], rows.Select(a => a.MatchContent));
        Assert.Equal("Hà Nội", rows[2].Content);
        Assert.All(rows, a => Assert.True(a.IsCorrect));
    }

    // ---- Sắp xếp ----

    [Fact]
    public void Ordering_needs_three_to_six_distinct_items_and_at_most_four_when_usable_at_level_one()
    {
        AssertFails(QuestionSamples.Ordering(items: 2), "steps");
        AssertFails(QuestionSamples.Ordering(items: 5), "steps");                       // cấp 1-2 mặc định: tối đa 4
        Assert.True(Build(QuestionSamples.Ordering(levelMin: 2, levelMax: 2, items: 6)).IsValid);
        AssertFails(QuestionSamples.Ordering(levelMin: 2, levelMax: 2, items: 6) with { Steps = [.. QuestionSamples.Ordering(items: 6).Steps!, "Thêm một mục"] }, "steps");
        AssertFails(QuestionSamples.Ordering() with { Steps = ["Một mục", "Mục hai", "mục HAI"] }, "steps[2]");
        AssertFails(QuestionSamples.Ordering() with { Steps = ["Một mục", " ", "Mục ba"] }, "steps[1]");
        AssertFails(QuestionSamples.Ordering() with { Steps = ["Một mục", new string('s', 201), "Mục ba"] }, "steps[1]");
    }

    [Fact]
    public void Ordering_rows_carry_the_correct_position_of_each_item()
    {
        var rows = Build(QuestionSamples.Ordering(items: 3)).Question!.Answers.OrderBy(a => a.SortOrder).ToList();
        Assert.Equal([1, 2, 3], rows.Select(a => (int)a.CorrectPosition!.Value));
        Assert.Equal(["Năm 1010: Lý Thái Tổ dời đô", "Năm 1070: Lập Văn Miếu", "Năm 1428: Lê Lợi lên ngôi"], rows.Select(a => a.Content));
        Assert.All(rows, a => Assert.True(a.IsCorrect));
    }

    // ---- Điền từ ----

    [Fact]
    public void Fill_blank_content_has_one_to_three_blanks_matching_the_answer_groups()
    {
        Assert.True(Build(QuestionSamples.FillBlank()).IsValid);
        Assert.True(Build(QuestionSamples.TwoBlanks()).IsValid);
        AssertFails(QuestionSamples.FillBlank() with { Content = "Câu này không có chỗ trống nào cả." }, "content");
        AssertFails(QuestionSamples.FillBlank() with { Content = "Một ___ hai ___ ba ___ bốn ___ năm." , Blanks = [new(["a"]), new(["b"]), new(["c"]), new(["d"])] }, "content");
        AssertFails(QuestionSamples.TwoBlanks() with { Blanks = [new(["Lý Thái Tổ"])] }, "blanks");
        AssertFails(QuestionSamples.FillBlank() with { Blanks = null }, "blanks");
    }

    [Fact]
    public void Fill_blank_accepted_spellings_are_one_to_five_distinct_and_at_most_100_characters()
    {
        AssertFails(QuestionSamples.FillBlank() with { Blanks = [new([])] }, "blanks[0].accepted");
        AssertFails(QuestionSamples.FillBlank() with { Blanks = [new(["a", "b", "c", "d", "e", "f"])] }, "blanks[0].accepted");
        AssertFails(QuestionSamples.FillBlank() with { Blanks = [new(["Thăng Long", "thăng  long"])] }, "blanks[0].accepted[1]");
        AssertFails(QuestionSamples.FillBlank() with { Blanks = [new([" "])] }, "blanks[0].accepted[0]");
        AssertFails(QuestionSamples.FillBlank() with { Blanks = [new([new string('t', 101)])] }, "blanks[0].accepted[0]");
        Assert.True(Build(QuestionSamples.FillBlank() with { Blanks = [new(["a", "b", "c", "d", "e"])] }).IsValid);
    }

    [Fact]
    public void Fill_blank_rows_store_accepted_spellings_by_blank_and_distractors_without_a_blank()
    {
        var rows = Build(QuestionSamples.FillBlank(wordBank: true)).Question!.Answers.OrderBy(a => a.SortOrder).ToList();
        Assert.Equal(["Lý Thái Tổ", "Lý Công Uẩn", "Lê Lợi", "Trần Hưng Đạo"], rows.Select(a => a.Content));
        Assert.Equal([1, 1, null, null], rows.Select(a => (int?)a.BlankIndex));
        Assert.Equal([true, true, false, false], rows.Select(a => a.IsCorrect));
        var shape = QuestionShape.Of(Build(QuestionSamples.FillBlank(wordBank: true)).Question!);
        Assert.Equal(["Lý Thái Tổ", "Lê Lợi", "Trần Hưng Đạo"], shape.WordBank);              // từ đúng đầu tiên của mỗi chỗ trống + từ gây nhiễu
        Assert.Empty(QuestionShape.Of(Build(QuestionSamples.FillBlank()).Question!).WordBank);
    }

    [Fact]
    public void Fill_blank_word_bank_has_three_to_eight_distinct_words()
    {
        // 1 từ đúng + 1 từ gây nhiễu = 2 từ: chưa đủ kho.
        AssertFails(QuestionSamples.FillBlank() with { Distractors = ["Lê Lợi"] }, "distractors");
        // 1 từ đúng + 7 từ gây nhiễu = 8 từ: vừa đủ; 9 từ thì quá.
        Assert.True(Build(QuestionSamples.FillBlank() with { Distractors = ["a1", "a2", "a3", "a4", "a5", "a6", "a7"] }).IsValid);
        AssertFails(QuestionSamples.FillBlank() with { Distractors = ["a1", "a2", "a3", "a4", "a5", "a6", "a7", "a8"] }, "distractors");
        // Từ gây nhiễu không được trùng đáp án đúng hay trùng nhau.
        AssertFails(QuestionSamples.FillBlank() with { Distractors = ["lý thái tổ", "Lê Lợi"] }, "distractors[0]");
        AssertFails(QuestionSamples.FillBlank() with { Distractors = ["Lê Lợi", "lê  lợi"] }, "distractors[1]");
        AssertFails(QuestionSamples.FillBlank() with { Distractors = ["Lý Công Uẩn", "Lê Lợi"] }, "distractors[0]");
        AssertFails(QuestionSamples.FillBlank() with { Distractors = ["Lê Lợi", " "] }, "distractors[1]");
    }

    // ---- Nhiều mệnh đề Đúng/Sai ----

    [Fact]
    public void Multi_true_false_needs_three_to_five_statements_and_both_truth_values()
    {
        var ok = QuestionSamples.Statements();
        AssertFails(ok with { Statements = ok.Statements!.Take(2).ToList() }, "statements");
        AssertFails(ok with { Statements = [.. ok.Statements!, new("Nhận định thứ tư của câu.", true), new("Nhận định thứ năm của câu.", true), new("Nhận định thứ sáu của câu.", true)] }, "statements");
        AssertFails(ok with { Statements = ok.Statements!.Select(s => s with { IsTrue = true }).ToList() }, "statements");
        AssertFails(ok with { Statements = ok.Statements!.Select(s => s with { IsTrue = false }).ToList() }, "statements");
        AssertFails(ok with { Statements = [new("Quá ngắn", true), .. ok.Statements!.Skip(1)] }, "statements[0].text");
        AssertFails(ok with { Statements = [new(new string('m', 501), true), .. ok.Statements!.Skip(1)] }, "statements[0].text");
        AssertFails(ok with { Statements = [ok.Statements![0], ok.Statements[1] with { Text = ok.Statements[0].Text!.ToUpperInvariant() }, ok.Statements[2]] }, "statements[1].text");
        Assert.True(Build(ok with { Statements = [.. ok.Statements!, new("Nhận định thứ tư của câu.", false), new("Nhận định thứ năm của câu.", true)] }).IsValid);
    }

    [Fact]
    public void Multi_true_false_rows_keep_each_statement_with_its_truth_value()
    {
        var rows = Build(QuestionSamples.Statements()).Question!.Answers.OrderBy(a => a.SortOrder).ToList();
        Assert.Equal([true, false, true], rows.Select(a => a.IsCorrect));
        Assert.Equal("Khu di tích nằm ở thành phố Huế.", rows[1].Content);
    }

    // ---- Đọc tư liệu ----

    [Fact]
    public void Source_reading_needs_a_passage_of_at_most_150_words_a_source_and_four_options()
    {
        var ok = QuestionSamples.SourceReading();
        AssertFails(ok with { Passage = null }, "passage");
        AssertFails(ok with { PassageSource = null }, "passageSource");
        AssertFails(ok with { PassageSource = new string('n', 501) }, "passageSource");
        AssertFails(ok with { Passage = string.Join(' ', Enumerable.Repeat("chữ", 151)) }, "passage");
        Assert.True(Build(ok with { Passage = string.Join(' ', Enumerable.Repeat("chữ", 150)) }).IsValid);
        AssertFails(ok with { Options = ok.Options!.Take(3).ToList() }, "options");
        var built = Build(ok).Question!;
        Assert.Equal((ok.Passage, ok.PassageSource), (built.Passage, built.PassageSource));
    }

    [Fact]
    public void Passage_and_source_are_ignored_for_other_types()
    {
        var built = Build(QuestionSamples.SingleChoice() with { Passage = "Đoạn thừa", PassageSource = "Nguồn thừa" }).Question!;
        Assert.Null(built.Passage);
        Assert.Null(built.PassageSource);
    }

    // ---- Nhiều lỗi cùng lúc ----

    [Fact]
    public void All_errors_are_reported_together_by_field_and_the_exception_is_a_400()
    {
        var result = Build(QuestionSamples.SingleChoice() with { Content = "", Explanation = null, DifficultyLevel = null, Options = [] });
        Assert.Equal(["content", "difficultyLevel", "explanation", "options"], result.ErrorTexts.Keys.OrderBy(k => k).ToArray());
        var exception = result.ToException();
        Assert.Equal(400, exception.StatusCode);
        Assert.Equal(result.ErrorTexts, exception.Errors);
    }
}
