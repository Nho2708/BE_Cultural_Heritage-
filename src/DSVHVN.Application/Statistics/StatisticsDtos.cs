using DSVHVN.Application.Attempts;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Statistics;

// Hợp đồng dữ liệu của thống kê cho giáo viên: kết quả bài kiểm tra của lớp, tiến độ và mức thành thạo của lớp, hồ sơ học tập của một học sinh.
// Mọi số liệu tính từ attempts, attempt_answers và attempt_answer_parts, không có bảng mới; chỉ tính lượt đã nộp (đã nộp hoặc hết giờ tự nộp)
// của học sinh đang hoạt động trong lớp. Giáo viên luôn thấy điểm số ở mọi cấp (cấp 1 kèm số sao).

/// <summary>Trạng thái của một học sinh với một bài kiểm tra.</summary>
public enum QuizStudentStatus
{
    SUBMITTED,
    EXPIRED,
    IN_PROGRESS,
    NOT_STARTED,
}

/// <summary>Một khoảng của phổ điểm (theo phần trăm tổng điểm): từ <see cref="FromPercent"/> tới dưới <see cref="ToPercent"/> (khoảng cuối gồm cả 100%).</summary>
public sealed record ScoreBinDto(int FromPercent, int ToPercent, int Count);

/// <summary>
/// Tab Tổng quan: sĩ số (học sinh đang hoạt động), số đã nộp, số hết giờ tự nộp, số đang làm, số chưa làm; điểm trung bình, cao nhất, thấp nhất
/// (điểm và phần trăm) và phổ điểm 10 khoảng. Chưa có lượt nào thì các số điểm là null kèm <see cref="Message"/>.
/// </summary>
public sealed record QuizResultsOverviewDto(
    long QuizId,
    string? Name,
    QuizStatus? Status,
    long LessonId,
    string? LessonTitle,
    long ClassId,
    string? ClassName,
    int Level,
    string LevelName,
    decimal? TotalPoint,
    int QuestionCount,
    int Enrolled,
    int Submitted,
    int AutoSubmitted,
    int Finished,
    int InProgress,
    int NotStarted,
    decimal? AverageScore,
    decimal? HighestScore,
    decimal? LowestScore,
    decimal? AveragePercent,
    decimal? HighestPercent,
    decimal? LowestPercent,
    IReadOnlyList<ScoreBinDto> Distribution,
    string? Message);

/// <summary>Một phương án của câu một đáp án đúng hoặc chọn hình đúng và số em chọn nó (<see cref="AnswerId"/> null là bỏ trống).</summary>
public sealed record ChoiceStatDto(long? AnswerId, string? Content, string? ImageUrl, bool IsCorrect, int Count, decimal Percent);

/// <summary>Một phần của câu nhiều phần (một cặp, một mục, một chỗ trống, một mệnh đề) và tỉ lệ đúng của nó.</summary>
public sealed record PartStatDto(string Label, int Answered, int Correct, decimal RatePercent);

/// <summary>Lỗi hay gặp của câu nhiều phần: phần nào sai nhiều và em hay chọn gì (cặp nối nhầm, chữ gõ sai, đáp án Đúng hay Sai).</summary>
public sealed record QuestionErrorDto(string Label, int Wrong, decimal WrongPercent, string? MostCommonWrong);

/// <summary>
/// Thống kê một câu của bài kiểm tra: tỉ lệ đúng (câu nhiều phần thêm <see cref="Parts"/> theo từng phần), phương án bị chọn nhiều nhất
/// (<see cref="MostChosen"/>, chỉ dạng một đáp án đúng, Đúng/Sai, chọn hình đúng, đọc tư liệu) và lỗi hay gặp (<see cref="CommonErrors"/>, chỉ dạng nhiều phần).
/// </summary>
public sealed record QuestionStatDto(
    long QuestionId,
    int Position,
    string? Content,
    QuestionType QuestionType,
    string TypeName,
    decimal? Point,
    long HeritageId,
    string? HeritageName,
    int Answered,
    int Correct,
    decimal RatePercent,
    IReadOnlyList<PartStatDto> Parts,
    IReadOnlyList<ChoiceStatDto> Choices,
    ChoiceStatDto? MostChosen,
    IReadOnlyList<QuestionErrorDto> CommonErrors);

/// <summary>Tỉ lệ đúng theo một dạng câu hoặc một di sản (chủ đề), kèm mức thành thạo và cờ "nên ôn" (tỉ lệ đúng dưới 50%).</summary>
public sealed record TopicStatDto(
    long? HeritageId,
    QuestionType? QuestionType,
    string Name,
    int Answered,
    int Correct,
    decimal RatePercent,
    MasteryLevel Level,
    string LevelLabel,
    bool NeedsReview);

/// <summary>Tab Từng câu: mỗi câu một dòng, theo thứ tự trong bài, và tỉ lệ đúng theo dạng câu.</summary>
public sealed record QuizQuestionStatsDto(
    long QuizId,
    string? Name,
    int Finished,
    IReadOnlyList<QuestionStatDto> Questions,
    IReadOnlyList<TopicStatDto> ByQuestionType,
    string? Message);

/// <summary>Tỉ lệ đúng của lớp theo di sản (chủ đề) và theo dạng câu trong riêng bài kiểm tra này.</summary>
public sealed record QuizTopicStatsDto(
    long QuizId,
    string? Name,
    int Finished,
    IReadOnlyList<TopicStatDto> ByHeritage,
    IReadOnlyList<TopicStatDto> ByQuestionType,
    string? Message);

/// <summary>Tab Từng học sinh: mỗi học sinh đang hoạt động một dòng, kể cả em chưa làm. <see cref="AttemptId"/> để mở chi tiết bài làm.</summary>
public sealed record QuizStudentRowDto(
    long StudentId,
    string? StudentCode,
    string? FullName,
    QuizStudentStatus Status,
    string StatusLabel,
    long? AttemptId,
    DateTime? StartedAt,
    DateTime? SubmittedAt,
    int? DurationSeconds,
    decimal? Score,
    decimal? TotalPoint,
    decimal? Percent,
    int? Stars,
    int? CorrectCount,
    int? TotalQuestions,
    IReadOnlyList<StudentBadgeDto> Badges);

public sealed record QuizStudentsDto(long QuizId, string? Name, int Level, IReadOnlyList<QuizStudentRowDto> Students);

/// <summary>
/// Chi tiết bài làm của một học sinh: từng câu như em đã thấy, câu em trả lời đặt cạnh đáp án đúng, đúng hay sai, điểm đạt (câu nhiều phần có số phần đúng)
/// và giải thích; giáo viên luôn xem được kể cả khi bài không cho em xem đáp án. Lớp cấp 1 kèm số sao.
/// </summary>
public sealed record TeacherAttemptDetailDto(
    long AttemptId,
    long StudentId,
    string? StudentCode,
    string? FullName,
    long QuizId,
    string? QuizName,
    long LessonId,
    string? LessonTitle,
    string? ClassName,
    int Level,
    AttemptStatus Status,
    string StatusLabel,
    DateTime? StartedAt,
    DateTime? SubmittedAt,
    int? DurationSeconds,
    decimal Score,
    decimal TotalPoint,
    decimal Percent,
    int? Stars,
    int CorrectCount,
    int TotalQuestions,
    IReadOnlyList<StudentBadgeDto> Badges,
    IReadOnlyList<StudentQuestionReviewDto> Questions);

// ---- Lớp ----

/// <summary>Một dòng mức thành thạo (theo di sản, tỉnh, loại di sản hoặc dạng câu): số câu đã trả lời (tính cả bỏ trống), số đúng, tỉ lệ đúng, mức và số sao.</summary>
public sealed record MasteryRowDto(
    long? Id,
    string? Name,
    int Answered,
    int Correct,
    decimal RatePercent,
    MasteryLevel Level,
    string LevelLabel,
    int Stars);

/// <summary>Một bài kiểm tra đã phát hành của lớp (đã tới giờ mở) và số em đã nộp trên sĩ số.</summary>
public sealed record ClassQuizProgressDto(
    long QuizId,
    string? Name,
    long LessonId,
    string? LessonTitle,
    QuizStatus? Status,
    DateTime? OpenAt,
    DateTime? CloseAt,
    int Submitted,
    int Enrolled,
    decimal RatePercent);

/// <summary>Số lần một huy hiệu đã đạt (theo lượt; Kiên trì chỉ có một lần).</summary>
public sealed record BadgeCountDto(BadgeCode Code, string Name, int Count);

/// <summary>
/// Một học sinh trong tab Tiến độ: số bài kiểm tra đã nộp trên số đã phát hành, bài học đã hoàn thành trên số bài có bài kiểm tra, tổng số sao (lớp cấp 1),
/// điểm trung bình (phần trăm), huy hiệu và lần đăng nhập gần nhất (null nếu chưa đăng nhập lần nào).
/// </summary>
public sealed record ClassStudentProgressDto(
    long StudentId,
    string? StudentCode,
    string? FullName,
    DateTime? LastLoginAt,
    bool NeverLoggedIn,
    int QuizzesSubmitted,
    int QuizzesPublished,
    int LessonsCompleted,
    int LessonsWithQuizzes,
    int? TotalStars,
    decimal? AveragePercent,
    IReadOnlyList<BadgeCountDto> Badges);

/// <summary>Ô của bảng học sinh × bài kiểm tra: chỉ có khi em đã bắt đầu; ô thiếu là chưa làm.</summary>
public sealed record ClassProgressCellDto(
    long StudentId,
    long QuizId,
    long AttemptId,
    QuizStudentStatus Status,
    decimal? Score,
    decimal? TotalPoint,
    decimal? Percent,
    int? Stars);

/// <summary>
/// Tab Tiến độ của lớp. Tiến độ lớp = tổng lượt đã nộp ÷ (số học sinh đang hoạt động × số bài kiểm tra đã phát hành và đã tới giờ mở của các bài học đã xuất bản).
/// </summary>
public sealed record ClassProgressDto(
    long ClassId,
    string? ClassName,
    int Grade,
    int Level,
    string LevelName,
    int Enrolled,
    int NeverLoggedIn,
    int QuizzesPublished,
    int SubmittedTotal,
    decimal ProgressPercent,
    IReadOnlyList<ClassQuizProgressDto> Quizzes,
    IReadOnlyList<ClassStudentProgressDto> Students,
    IReadOnlyList<ClassProgressCellDto> Cells,
    string? Message);

/// <summary>Ô bảng nhiệt học sinh × di sản.</summary>
public sealed record HeatmapCellDto(long StudentId, long HeritageId, int Answered, int Correct, decimal RatePercent, MasteryLevel Level);

public sealed record HeatmapStudentDto(long StudentId, string? StudentCode, string? FullName);

/// <summary>
/// Tab Thành thạo của lớp: tỉ lệ đúng của cả lớp theo di sản (lọc được theo tỉnh và loại), theo tỉnh, theo loại di sản và theo dạng câu; bảng nhiệt học sinh × di sản
/// (tối đa 60 di sản nhiều câu nhất sau khi lọc). Mức: từ 80% Thành thạo, 50% tới dưới 80% Đạt, dưới 50% Cần ôn, dưới 3 câu Chưa đủ dữ liệu.
/// </summary>
public sealed record ClassMasteryDto(
    long ClassId,
    string? ClassName,
    int? ProvinceId,
    HeritageType? HeritageType,
    IReadOnlyList<MasteryRowDto> ByHeritage,
    IReadOnlyList<MasteryRowDto> ByProvince,
    IReadOnlyList<MasteryRowDto> ByHeritageType,
    IReadOnlyList<TopicStatDto> ByQuestionType,
    IReadOnlyList<HeatmapStudentDto> HeatmapStudents,
    IReadOnlyList<HeatmapCellDto> HeatmapCells,
    string? Message);

// ---- Hồ sơ học tập của một học sinh ----

/// <summary>Một lượt trong lịch sử của học sinh, kèm huy hiệu của lượt. Cấp 1 có thêm <see cref="Stars"/>.</summary>
public sealed record StudentHistoryRowDto(
    long AttemptId,
    long QuizId,
    string? QuizName,
    long LessonId,
    string? LessonTitle,
    AttemptStatus Status,
    string StatusLabel,
    DateTime? SubmittedAt,
    decimal Score,
    decimal TotalPoint,
    decimal Percent,
    int? Stars,
    IReadOnlyList<StudentBadgeDto> Badges);

/// <summary>Một điểm của biểu đồ điểm theo thời gian (cũ nhất trước).</summary>
public sealed record ScorePointDto(long AttemptId, DateTime? SubmittedAt, string? QuizName, decimal Percent);

/// <summary>Bài học đã hoàn thành của một học sinh: em đã nộp mọi bài kiểm tra đã phát hành của bài.</summary>
public sealed record CompletedLessonDto(long LessonId, string? Title, int QuizzesDone, int QuizzesPublished);

/// <summary>
/// Hồ sơ học tập của một học sinh cho giáo viên: lịch sử làm bài, biểu đồ điểm, bài học đã hoàn thành, mức thành thạo theo di sản, tỉnh và loại di sản,
/// huy hiệu theo từng lượt và tổng hợp.
/// </summary>
public sealed record StudentProfileDto(
    long StudentId,
    string? StudentCode,
    string? FullName,
    long ClassId,
    string? ClassName,
    int Level,
    bool IsActive,
    DateTime? LastLoginAt,
    int QuizzesSubmitted,
    int QuizzesPublished,
    int FinishedAttempts,
    decimal? AveragePercent,
    int? TotalStars,
    IReadOnlyList<StudentHistoryRowDto> History,
    IReadOnlyList<ScorePointDto> ScoreTrend,
    IReadOnlyList<CompletedLessonDto> CompletedLessons,
    IReadOnlyList<MasteryRowDto> MasteryByHeritage,
    IReadOnlyList<MasteryRowDto> MasteryByProvince,
    IReadOnlyList<MasteryRowDto> MasteryByHeritageType,
    IReadOnlyList<BadgeCountDto> Badges,
    string? Message);
