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
/// không trùng: mỗi câu một dòng. <see cref="AnswerId"/> chỉ dùng cho dạng chọn một phương án (NULL là bỏ trống); bốn dạng nhiều phần
/// lưu từng phần ở <see cref="Parts"/>.
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

    /// <summary>bit NOT NULL DEFAULT 0. Đúng hoàn toàn: mọi phần đều đúng.</summary>
    public bool IsCorrect { get; set; }

    /// <summary>Số phần được chấm của câu (smallint NOT NULL DEFAULT 1): 1 với dạng chọn một phương án.</summary>
    public short PartsTotal { get; set; } = 1;

    /// <summary>Số phần học sinh làm đúng (smallint NOT NULL DEFAULT 0); bỏ trống là 0.</summary>
    public short PartsCorrect { get; set; }

    /// <summary>Điểm câu = điểm của câu trong quiz × <see cref="PartsCorrect"/> ÷ <see cref="PartsTotal"/>, làm tròn 2 chữ số.</summary>
    public decimal? PointEarned { get; set; }

    public DateTime? AnsweredAt { get; set; }

    /// <summary>
    /// Bài chơi cấp 1 dạng luyện tập (tinyint NOT NULL DEFAULT 0): số lần em đã kiểm câu này. Từ 1 trở lên thì câu trả lời đã lưu là lần trả lời
    /// đầu tiên, đã chốt để chấm và không ghi đè nữa. Luôn 0 ở cấp 2, 3.
    /// </summary>
    public byte TryCount { get; set; }

    public List<AttemptAnswerPart> Parts { get; set; } = [];
}

/// <summary>
/// Bảng <c>attempt_answer_parts</c> (CSDL v4): câu trả lời từng phần của bốn dạng nhiều phần (ghép cặp, sắp xếp, điền từ, nhiều mệnh đề
/// Đúng/Sai). Mỗi dòng trỏ tới đúng một trong hai: một dòng <c>answers</c> (<see cref="AnswerId"/>) hoặc một chỗ trống
/// (<see cref="BlankIndex"/>). Chỉ các cột của dạng tương ứng có giá trị.
/// </summary>
public class AttemptAnswerPart
{
    public long Id { get; set; }

    public long AttemptAnswerId { get; set; }
    public AttemptAnswer? AttemptAnswer { get; set; }

    /// <summary>Phần được trả lời: vế trái của cặp, bước, mệnh đề. NULL với chỗ trống của điền từ.</summary>
    public long? AnswerId { get; set; }
    public Answer? Answer { get; set; }

    /// <summary>Chỗ trống được trả lời (điền từ, 1..n). NULL với dạng khác.</summary>
    public byte? BlankIndex { get; set; }

    /// <summary>Ghép cặp: dòng <c>answers</c> có vế phải học sinh ghép vào; điền từ bằng kho từ: dòng học sinh chọn. NULL nếu bỏ trống.</summary>
    public long? SelectedAnswerId { get; set; }
    public Answer? SelectedAnswer { get; set; }

    /// <summary>Sắp xếp: vị trí (1..n) học sinh đặt bước này. NULL nếu bỏ trống.</summary>
    public byte? SelectedPosition { get; set; }

    /// <summary>Nhiều mệnh đề: học sinh chọn Đúng (true) hay Sai (false); NULL nếu chưa chọn.</summary>
    public bool? SelectedBool { get; set; }

    /// <summary>Điền từ: chữ học sinh gõ hoặc chọn (tối đa 255 ký tự). NULL nếu bỏ trống.</summary>
    public string? ResponseText { get; set; }

    /// <summary>Phần này đúng, chấm và lưu cố định.</summary>
    public bool IsCorrect { get; set; }
}
