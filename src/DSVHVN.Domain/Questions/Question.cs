using DSVHVN.Domain.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Lessons;

namespace DSVHVN.Domain.Questions;

/// <summary>
/// Bảng <c>questions</c>: ngân hàng câu hỏi 8 dạng, dùng chung mọi trường. Mỗi câu gắn một di sản và có cấp phù hợp; chỉ câu APPROVED
/// mới được thêm vào quiz. Câu tự tạo mặc định APPROVED, câu AI tạo mặc định PENDING.
/// </summary>
public class Question : IHasCreatedAt, IHasUpdatedAt, ISoftDeletable
{
    public long Id { get; set; }

    public long HeritageId { get; set; }
    public Heritage? Heritage { get; set; }

    /// <summary>Giáo viên tạo câu, hoặc giáo viên đã yêu cầu AI tạo.</summary>
    public long CreatedBy { get; set; }
    public User? Creator { get; set; }

    /// <summary>Lần gọi AI đã sinh câu; NULL nếu giáo viên tự tạo.</summary>
    public long? AiGenerationId { get; set; }
    public AiGeneration? AiGeneration { get; set; }

    public string? Content { get; set; }

    public QuestionType? QuestionType { get; set; }

    /// <summary>Hình minh họa của câu (Đúng/Sai có hình, câu hỏi về hình); phải là ảnh trong thư viện của di sản được gắn. NULL nếu không có.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Đoạn tư liệu học sinh đọc trước khi trả lời; bắt buộc với SOURCE_READING, NULL với dạng khác.</summary>
    public string? Passage { get; set; }

    /// <summary>Nguồn của đoạn tư liệu (tên văn bản, tác giả, năm).</summary>
    public string? PassageSource { get; set; }

    /// <summary>Cấp học thấp nhất câu dùng được (1-3). Quiz của lớp cấp c chỉ nhận câu có LevelMin &lt;= c &lt;= LevelMax.</summary>
    public byte LevelMin { get; set; } = 1;

    /// <summary>Cấp học cao nhất câu dùng được (1-3).</summary>
    public byte LevelMax { get; set; } = 3;

    public DifficultyLevel? DifficultyLevel { get; set; }

    public string? Explanation { get; set; }

    public QuestionSource? Source { get; set; }

    public ReviewStatus? ReviewStatus { get; set; }

    /// <summary>Người duyệt (giáo viên), hoặc quản trị hệ thống đã ẩn câu; NULL khi chưa duyệt.</summary>
    public long? ReviewedBy { get; set; }
    public User? Reviewer { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public List<Answer> Answers { get; set; } = [];
}

/// <summary>
/// Bảng <c>answers</c>: các phần trả lời của một câu, đủ cho 8 dạng (CSDL v4). Cột của dạng khác để NULL.
/// <list type="bullet">
/// <item>SINGLE_CHOICE, TRUE_FALSE, IMAGE_CHOICE, SOURCE_READING: mỗi dòng một phương án, đúng một dòng <see cref="IsCorrect"/>.</item>
/// <item>MATCHING: mỗi dòng một cặp đúng, vế trái <see cref="Content"/>/<see cref="ImageUrl"/>, vế phải <see cref="MatchContent"/>.</item>
/// <item>ORDERING: mỗi dòng một mục, thứ tự đúng ở <see cref="CorrectPosition"/>.</item>
/// <item>FILL_BLANK: dòng có <see cref="BlankIndex"/> là cách viết được chấp nhận của chỗ trống; dòng không có là từ gây nhiễu.</item>
/// <item>MULTI_TRUE_FALSE: mỗi dòng một mệnh đề, <see cref="IsCorrect"/> là mệnh đề đúng.</item>
/// </list>
/// </summary>
public class Answer
{
    public long Id { get; set; }

    public long QuestionId { get; set; }
    public Question? Question { get; set; }

    public string? Content { get; set; }

    /// <summary>Hình của phương án (chọn hình đúng) hoặc vế trái (ghép cặp hình - tên).</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Vế phải của cặp đúng (ghép cặp).</summary>
    public string? MatchContent { get; set; }

    /// <summary>Vị trí đúng 1..n (sắp xếp).</summary>
    public byte? CorrectPosition { get; set; }

    /// <summary>Chỗ trống mà đáp án này điền vào (điền từ, 1..n); NULL với từ gây nhiễu và với dạng khác.</summary>
    public byte? BlankIndex { get; set; }

    /// <summary>bit NOT NULL DEFAULT 0.</summary>
    public bool IsCorrect { get; set; }

    /// <summary>Thứ tự hiển thị và thứ tự lưu cố định (A, B, C, D; mục của ghép cặp, sắp xếp, mệnh đề).</summary>
    public int? SortOrder { get; set; }
}
