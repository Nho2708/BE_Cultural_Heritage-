using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Questions;

namespace DSVHVN.Domain.Quizzes;

/// <summary>
/// Bảng <c>quizzes</c>: quiz thuộc một bài học. Học sinh mở quiz bằng mã của bài học; lớp được làm quiz là lớp của bài học.
/// </summary>
public class Quiz : IHasCreatedAt, IHasUpdatedAt, ISoftDeletable
{
    public long Id { get; set; }

    public long LessonId { get; set; }
    public Lesson? Lesson { get; set; }

    public long CreatedBy { get; set; }
    public User? Creator { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public DifficultyLevel? DifficultyLevel { get; set; }

    /// <summary>Thời gian làm bài tối đa (phút); NULL là không giới hạn.</summary>
    public int? TimeLimitMinutes { get; set; }

    public DateTime? OpenAt { get; set; }

    public DateTime? CloseAt { get; set; }

    /// <summary>bit NOT NULL DEFAULT 0.</summary>
    public bool ShuffleQuestions { get; set; }

    /// <summary>bit NOT NULL DEFAULT 0.</summary>
    public bool ShowAnswersAfterSubmit { get; set; }

    /// <summary>Tổng điểm = tổng <c>quiz_questions.point</c>, decimal(6,2).</summary>
    public decimal? TotalPoint { get; set; }

    public QuizStatus? Status { get; set; }

    public DateTime? PublishedAt { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public List<QuizQuestion> Questions { get; set; } = [];
}

/// <summary>Bảng <c>quiz_questions</c>: câu hỏi trong quiz (nhiều - nhiều), điểm và thứ tự; khóa chính kép.</summary>
public class QuizQuestion
{
    public long QuizId { get; set; }
    public Quiz? Quiz { get; set; }

    public long QuestionId { get; set; }
    public Question? Question { get; set; }

    /// <summary>decimal(5,2).</summary>
    public decimal? Point { get; set; }

    public int? SortOrder { get; set; }
}

/// <summary>
/// Bảng <c>attempts</c>: một lượt làm bài của một học sinh cho một quiz. (<c>quiz_id</c>, <c>student_id</c>) không trùng:
/// mỗi học sinh chỉ làm một lần.
/// </summary>
public class Attempt
{
    public long Id { get; set; }

    public long QuizId { get; set; }
    public Quiz? Quiz { get; set; }

    public long StudentId { get; set; }
    public Student? Student { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? SubmittedAt { get; set; }

    public int? DurationSeconds { get; set; }

    public decimal? Score { get; set; }

    /// <summary>Bản sao tổng điểm của quiz lúc làm bài.</summary>
    public decimal? TotalPoint { get; set; }

    public int? CorrectCount { get; set; }

    public int? TotalQuestions { get; set; }

    public AttemptStatus? Status { get; set; }

    public List<AttemptAnswer> Answers { get; set; } = [];
}

/// <summary>
/// Bảng <c>attempt_answers</c>: câu trả lời trong một lượt, chấm cố định lúc nộp. (<c>attempt_id</c>, <c>question_id</c>)
/// không trùng: mỗi câu một dòng. <see cref="AnswerId"/> NULL là bỏ trống.
/// </summary>
public class AttemptAnswer
{
    public long Id { get; set; }

    public long AttemptId { get; set; }
    public Attempt? Attempt { get; set; }

    public long QuestionId { get; set; }
    public Question? Question { get; set; }

    public long? AnswerId { get; set; }
    public Answer? Answer { get; set; }

    /// <summary>bit NOT NULL DEFAULT 0.</summary>
    public bool IsCorrect { get; set; }

    public decimal? PointEarned { get; set; }

    public DateTime? AnsweredAt { get; set; }
}
