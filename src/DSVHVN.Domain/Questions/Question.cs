using DSVHVN.Domain.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Lessons;

namespace DSVHVN.Domain.Questions;

/// <summary>
/// Bảng <c>questions</c>: ngân hàng câu hỏi. Mỗi câu gắn một di sản; chỉ câu APPROVED mới được thêm vào quiz.
/// Câu tự tạo mặc định APPROVED, câu AI tạo mặc định PENDING.
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

    public DifficultyLevel? DifficultyLevel { get; set; }

    public string? Explanation { get; set; }

    public QuestionSource? Source { get; set; }

    public ReviewStatus? ReviewStatus { get; set; }

    /// <summary>Người duyệt; NULL khi chưa duyệt.</summary>
    public long? ReviewedBy { get; set; }
    public User? Reviewer { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public List<Answer> Answers { get; set; } = [];
}

/// <summary>Bảng <c>answers</c>: phương án trả lời của câu hỏi; mỗi câu có đúng một phương án đúng (kiểm ở tầng code).</summary>
public class Answer
{
    public long Id { get; set; }

    public long QuestionId { get; set; }
    public Question? Question { get; set; }

    public string? Content { get; set; }

    /// <summary>bit NOT NULL DEFAULT 0.</summary>
    public bool IsCorrect { get; set; }

    /// <summary>Thứ tự hiển thị (A, B, C, D).</summary>
    public int? SortOrder { get; set; }
}
