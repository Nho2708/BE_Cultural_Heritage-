using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Tests.Support;

/// <summary>Câu hỏi mẫu hợp lệ cho từng dạng, dùng chung cho kiểm thử đơn vị và tích hợp.</summary>
public static class QuestionSamples
{
    public const string ImageA = "https://upload.wikimedia.org/anh-a.jpg";
    public const string ImageB = "https://upload.wikimedia.org/anh-b.jpg";
    public const string ImageC = "https://upload.wikimedia.org/anh-c.jpg";
    public const string ImageD = "https://upload.wikimedia.org/anh-d.jpg";

    public static readonly IReadOnlySet<string> Library = new HashSet<string> { ImageA, ImageB, ImageC, ImageD };

    public static SaveQuestionRequest SingleChoice(long heritageId = 1) => new(
        QuestionType.SINGLE_CHOICE, heritageId, "Hoàng thành Thăng Long được UNESCO ghi danh vào năm nào?",
        "UNESCO ghi danh Hoàng thành Thăng Long là Di sản văn hóa thế giới vào năm 2010.", DifficultyLevel.EASY,
        Options: [new("Năm 1999", null, false), new("Năm 2010", null, true), new("Năm 2003", null, false), new("Năm 2015", null, false)]);

    public static SaveQuestionRequest TrueFalse(long heritageId = 1, bool answer = true, string? image = ImageA, int? levelMin = null, int? levelMax = null) => new(
        QuestionType.TRUE_FALSE, heritageId, "Em hãy cho biết: Hoàng thành Thăng Long nằm giữa lòng Hà Nội.",
        "Khu di tích nằm ở trung tâm thành phố Hà Nội.", DifficultyLevel.EASY, levelMin, levelMax, image, AnswerIsTrue: answer);

    public static SaveQuestionRequest ImageChoice(long heritageId = 1) => new(
        QuestionType.IMAGE_CHOICE, heritageId, "Em hãy chọn hình có tên: Cổng Đoan Môn.", "Hình thứ hai là cổng Đoan Môn.", DifficultyLevel.EASY,
        Options: [new("Điện Kính Thiên", ImageA, false), new("Cổng Đoan Môn", ImageB, true), new("Khu khảo cổ", ImageC, false)]);

    public static SaveQuestionRequest Matching(long heritageId = 1) => new(
        QuestionType.MATCHING, heritageId, "Em hãy ghép mỗi hình với tên của hình đó.", "Mỗi tên là chú thích của hình.", DifficultyLevel.MEDIUM,
        Pairs: [new(null, ImageA, "Điện Kính Thiên"), new(null, ImageB, "Cổng Đoan Môn"), new("Hà Nội", null, "Thủ đô")]);

    public static SaveQuestionRequest Ordering(long heritageId = 1, int? levelMin = null, int? levelMax = null, int items = 4) => new(
        QuestionType.ORDERING, heritageId, "Hãy sắp xếp các sự kiện của Hoàng thành theo thứ tự thời gian.", "Xếp theo năm diễn ra.", DifficultyLevel.MEDIUM,
        levelMin, levelMax, Steps: new[] { "Năm 1010: Lý Thái Tổ dời đô", "Năm 1070: Lập Văn Miếu", "Năm 1428: Lê Lợi lên ngôi", "Năm 2010: UNESCO ghi danh", "Năm 2011: Mở cửa khu khảo cổ", "Năm 2012: Mở rộng trưng bày" }.Take(items).ToList());

    public static SaveQuestionRequest FillBlank(long heritageId = 1, bool wordBank = false) => new(
        QuestionType.FILL_BLANK, heritageId, "Vua ___ dời đô từ Hoa Lư về Thăng Long vào năm 1010.", "Lý Thái Tổ là người dời đô.", DifficultyLevel.MEDIUM,
        Blanks: [new(["Lý Thái Tổ", "Lý Công Uẩn"])], Distractors: wordBank ? ["Lê Lợi", "Trần Hưng Đạo"] : null);

    public static SaveQuestionRequest TwoBlanks(long heritageId = 1) => new(
        QuestionType.FILL_BLANK, heritageId, "Vua ___ dời đô về ___ vào năm 1010.", "Lý Thái Tổ dời đô về Thăng Long.", DifficultyLevel.MEDIUM,
        Blanks: [new(["Lý Thái Tổ"]), new(["Thăng Long"])]);

    public static SaveQuestionRequest Statements(long heritageId = 1) => new(
        QuestionType.MULTI_TRUE_FALSE, heritageId, "Hoàng thành Thăng Long là trung tâm quyền lực của nhiều triều đại phong kiến Việt Nam.",
        "Chỉ nhận định thứ hai sai vì khu di tích nằm ở Hà Nội.", DifficultyLevel.HARD,
        Statements:
        [
            new("Khu di tích được UNESCO ghi danh năm 2010.", true),
            new("Khu di tích nằm ở thành phố Huế.", false),
            new("Khu di tích có nhiều lớp văn hóa chồng lên nhau.", true),
        ]);

    public static SaveQuestionRequest SourceReading(long heritageId = 1) => new(
        QuestionType.SOURCE_READING, heritageId, "Theo đoạn tư liệu, vua nào dời đô về Thăng Long?", "Đoạn tư liệu nêu rõ Lý Thái Tổ.", DifficultyLevel.MEDIUM,
        Passage: "Mùa thu năm Canh Tuất, Lý Thái Tổ dời đô từ Hoa Lư về thành Đại La và đổi tên là Thăng Long.",
        PassageSource: "Đại Việt sử ký toàn thư, Ngô Sĩ Liên, thế kỷ 15",
        Options: [new("Lý Thái Tổ", null, true), new("Trần Thái Tông", null, false), new("Lê Thái Tổ", null, false), new("Đinh Tiên Hoàng", null, false)]);

    /// <summary>Một câu mẫu hợp lệ cho mỗi dạng, theo thứ tự của enum.</summary>
    public static IEnumerable<SaveQuestionRequest> OfEveryType(long heritageId = 1)
    {
        yield return SingleChoice(heritageId);
        yield return TrueFalse(heritageId);
        yield return ImageChoice(heritageId);
        yield return Matching(heritageId);
        yield return Ordering(heritageId);
        yield return FillBlank(heritageId);
        yield return Statements(heritageId);
        yield return SourceReading(heritageId);
    }

    public static string NameOf(QuestionType type) => type.ToString();
}
