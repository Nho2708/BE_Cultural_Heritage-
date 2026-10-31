using DSVHVN.Application.Lessons;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Attempts;

// Hợp đồng dữ liệu của khu làm bài của học sinh: mở bài kiểm tra, làm bài, nộp, kết quả theo cấp, lịch sử, tiến độ và trang học.
// Lời nhắn với học sinh xưng "em". Mọi chỉ số vị trí trong câu trả lời tính theo thứ tự đang hiện trên màn hình của em (xem QuestionSubmission).

/// <summary>Việc em làm được với một bài kiểm tra ở lúc này; giao diện đặt nhãn nút theo giá trị này.</summary>
public enum StudentQuizAction
{
    /// <summary>Bắt đầu làm bài (tạo lượt mới).</summary>
    START,

    /// <summary>Làm tiếp lượt đang làm dở.</summary>
    CONTINUE,

    /// <summary>Xem kết quả của lượt đã nộp, kể cả khi bài đã đóng.</summary>
    VIEW_RESULT,

    /// <summary>Chưa tới giờ mở.</summary>
    NOT_OPEN,

    /// <summary>Bài đã đóng và em chưa làm.</summary>
    CLOSED,
}

/// <summary>
/// Màn hình trước khi bắt đầu: tên, số câu, thời gian làm, giờ mở và đóng, nhắc "em chỉ được làm một lần". <see cref="TotalPoint"/> và
/// <see cref="TimeLimitMinutes"/> là null với lớp cấp 1 (không điểm, không đồng hồ).
/// </summary>
public sealed record StudentQuizIntroDto(
    long Id,
    string? Name,
    string? Description,
    long LessonId,
    string? LessonTitle,
    int Level,
    string LevelName,
    string Layout,
    int QuestionCount,
    decimal? TotalPoint,
    int? TimeLimitMinutes,
    bool ShowsClock,
    DateTime? OpenAt,
    DateTime? CloseAt,
    StudentQuizState State,
    string StateLabel,
    StudentQuizAction Action,
    string ActionLabel,
    long? AttemptId,
    AttemptStatus? AttemptStatus,
    string? Notice);

/// <summary>
/// Một câu trong lượt làm bài, đúng như em thấy: không có đáp án đúng; các cột của ghép cặp, các mục của sắp xếp, thẻ hình và kho từ đã xáo theo
/// cách cố định của lượt. <see cref="Answer"/> là câu trả lời em đã lưu (null nếu chưa trả lời); <see cref="Point"/> là null với lớp cấp 1.
/// <see cref="Checked"/> chỉ bật ở cấp 1: em đã kiểm câu này ít nhất một lần, nên câu trả lời đã lưu là lần đầu đã chốt để chấm.
/// </summary>
public sealed record StudentAttemptQuestionDto(
    long QuestionId,
    int Position,
    decimal? Point,
    QuestionType QuestionType,
    string TypeName,
    string Content,
    string? ImageUrl,
    string? Passage,
    string? PassageSource,
    IReadOnlyList<QuestionPreviewOptionDto> Options,
    IReadOnlyList<QuestionPreviewOptionDto> LeftItems,
    IReadOnlyList<string> RightItems,
    IReadOnlyList<string> Items,
    int BlankCount,
    IReadOnlyList<string> WordBank,
    IReadOnlyList<string> Statements,
    int PartsTotal,
    bool Answered,
    QuestionSubmission? Answer,
    bool Checked = false);

/// <summary>
/// Lượt làm bài đang làm dở. <see cref="Deadline"/> là hạn nộp (lúc bắt đầu + thời gian làm, không muộn hơn giờ đóng; không có thì null);
/// <see cref="RemainingSeconds"/> tính tại <see cref="ServerTime"/>. <see cref="ShowsClock"/> cho biết có hiện đồng hồ không (cấp 1 không,
/// cấp 2 khi bài đặt thời gian, cấp 3 luôn có). Máy chủ nhận bài tới hạn nộp cộng 30 giây. <see cref="PracticeMode"/> bật ở cấp 1: bài chơi dạng
/// luyện tập, kiểm từng câu ngay và cho thử lại; lần trả lời đầu tiên của mỗi câu là lần được chấm.
/// </summary>
public sealed record StudentAttemptDto(
    long Id,
    long QuizId,
    string? QuizName,
    long LessonId,
    string? LessonTitle,
    int Level,
    string Layout,
    bool ReadAloud,
    bool PracticeMode,
    bool ShowsClock,
    AttemptStatus Status,
    DateTime StartedAt,
    DateTime? Deadline,
    int? RemainingSeconds,
    DateTime ServerTime,
    int? TimeLimitMinutes,
    int QuestionCount,
    int AnsweredCount,
    decimal? TotalPoint,
    IReadOnlyList<StudentAttemptQuestionDto> Questions);

/// <summary>Kết quả lưu một câu trả lời.</summary>
public sealed record SavedAnswerDto(
    long QuestionId,
    bool Answered,
    DateTime SavedAt,
    int AnsweredCount,
    int QuestionCount,
    int? RemainingSeconds);

/// <summary>
/// Kết quả kiểm một câu của bài chơi cấp 1. Lần kiểm đầu tiên được lưu làm câu trả lời để chấm; các lần sau chỉ cho biết đúng hay sai để em thử lại.
/// Mọi vị trí tính theo thứ tự đang hiện trên màn hình của em: <see cref="Parts"/> có một phần với dạng chọn, một phần cho mỗi hình bên trái với ghép cặp,
/// một phần cho mỗi mục của sắp xếp (mục đó đang ở đúng vị trí hay không).
/// </summary>
public sealed record AnswerCheckDto(
    long QuestionId,
    bool IsCorrect,
    bool FirstTryCorrect,
    int TryCount,
    IReadOnlyList<bool> Parts,
    QuestionSubmission CorrectAnswer,
    string? Explanation,
    int AnsweredCount,
    int QuestionCount);

/// <summary>
/// Một thẻ trong album di sản của em ở cấp 1: mỗi di sản của bài em học xong là một thẻ. <see cref="LessonId"/> là bài sớm nhất của lớp có di sản này.
/// </summary>
public sealed record StudentAlbumCardDto(
    long HeritageId,
    string Slug,
    string Name,
    string? ShortDescription,
    string? ThumbnailUrl,
    HeritageType? HeritageType,
    string GroupName,
    bool Collected,
    DateTime? CollectedAt,
    long LessonId,
    string? LessonTitle);

/// <summary>Album di sản của em: thẻ đã có trước (mới nhất trước), rồi thẻ chưa có theo thứ tự bài học.</summary>
public sealed record StudentAlbumDto(int Collected, int Total, IReadOnlyList<StudentAlbumCardDto> Cards);

public sealed record StudentBadgeDto(BadgeCode Code, string Name, string Description);

/// <summary>Một mục của phân tích theo chủ đề (di sản) hoặc theo dạng câu: số câu em đã trả lời, số đúng và tỉ lệ đúng.</summary>
public sealed record StudentTopicDto(
    long? HeritageId,
    QuestionType? QuestionType,
    string Name,
    int Answered,
    int Correct,
    decimal RatePercent);

/// <summary>Phân tích của lớp cấp 3: tỉ lệ đúng theo di sản và theo dạng câu, các di sản nên ôn (tỉ lệ đúng dưới 50%), số phần đúng của câu nhiều phần.</summary>
public sealed record StudentAnalysisDto(
    IReadOnlyList<StudentTopicDto> ByHeritage,
    IReadOnlyList<StudentTopicDto> ByQuestionType,
    IReadOnlyList<StudentTopicDto> HeritagesToReview,
    int PartsCorrect,
    int PartsTotal);

/// <summary>
/// Một câu trong phần xem lại (chỉ khi bài kiểm tra cho xem đáp án): câu như em đã thấy (<see cref="Question"/>, kèm câu em trả lời),
/// đáp án đúng cùng thứ tự đang hiện, giải thích. Cấp 1 không có đúng sai, số phần, điểm (các trường đó là null).
/// </summary>
public sealed record StudentQuestionReviewDto(
    StudentAttemptQuestionDto Question,
    QuestionSubmission CorrectAnswer,
    string? Explanation,
    bool? IsCorrect,
    int? PartsCorrect,
    int? PartsTotal,
    decimal? PointEarned);

/// <summary>
/// Kết quả một lượt đã nộp, trình bày theo cấp hiện tại của lớp. Cấp 1 chỉ có <see cref="Stars"/> và <see cref="Praise"/> (điểm, phần trăm, số câu
/// đúng sai đều null); cấp 2 có điểm, số câu đúng, sai, bỏ trống, thời gian làm; cấp 3 thêm <see cref="Analysis"/>.
/// <see cref="Questions"/> chỉ có khi bài kiểm tra cho xem đáp án sau khi nộp. Riêng cấp 1 có thêm: số câu đúng ngay lần đầu, các thẻ di sản của bài
/// (đã vào album), số thẻ em đã có trên tổng số thẻ của lớp và một câu mang về nhà hỏi bố mẹ; cấp khác để null.
/// </summary>
public sealed record StudentAttemptResultDto(
    long AttemptId,
    long QuizId,
    string? QuizName,
    long LessonId,
    string? LessonTitle,
    int Level,
    string LevelName,
    string ResultView,
    AttemptStatus Status,
    string StatusLabel,
    DateTime? StartedAt,
    DateTime? SubmittedAt,
    int? DurationSeconds,
    int? Stars,
    string? Praise,
    decimal? Score,
    decimal? TotalPoint,
    decimal? Percent,
    int? CorrectCount,
    int? WrongCount,
    int? BlankCount,
    int TotalQuestions,
    IReadOnlyList<StudentBadgeDto> Badges,
    bool AnswersShown,
    IReadOnlyList<StudentQuestionReviewDto>? Questions,
    StudentAnalysisDto? Analysis,
    int? FirstTryCorrect = null,
    IReadOnlyList<StudentAlbumCardDto>? Cards = null,
    int? AlbumCollected = null,
    int? AlbumTotal = null,
    string? TakeHome = null);

/// <summary>Một dòng lịch sử bài làm. Cấp 1 hiện <see cref="Stars"/> thay điểm (điểm và phần trăm là null).</summary>
public sealed record StudentAttemptRowDto(
    long AttemptId,
    long QuizId,
    string? QuizName,
    long LessonId,
    string? LessonTitle,
    AttemptStatus Status,
    string StatusLabel,
    DateTime? SubmittedAt,
    int? Stars,
    decimal? Score,
    decimal? TotalPoint,
    decimal? Percent,
    IReadOnlyList<StudentBadgeDto> Badges);

/// <summary>Mức thành thạo của em với một di sản; cấp 1 hiện <see cref="Stars"/> (thanh sao), <see cref="Percent"/> là null.</summary>
public sealed record StudentMasteryDto(
    long HeritageId,
    string? Name,
    int Answered,
    int Correct,
    decimal? Percent,
    MasteryLevel Level,
    string Label,
    int Stars);

/// <summary>Huy hiệu em đã đạt: số lần và lần gần nhất.</summary>
public sealed record StudentEarnedBadgeDto(
    BadgeCode Code,
    string Name,
    string Description,
    int Count,
    DateTime? LastEarnedAt,
    long? LastAttemptId);

/// <summary>
/// Tiến độ của em: số quiz đã nộp trên số quiz đã phát hành (đã tới giờ mở) của các bài học của lớp, số bài đã hoàn thành (đã nộp mọi quiz
/// đã phát hành của bài), mức thành thạo theo di sản và huy hiệu đã đạt. <see cref="TotalStars"/> chỉ có ở cấp 1.
/// </summary>
public sealed record StudentProgressDto(
    int Level,
    string LevelName,
    int QuizzesSubmitted,
    int QuizzesPublished,
    decimal ProgressPercent,
    int LessonsCompleted,
    int LessonsWithQuiz,
    int FinishedAttempts,
    int? TotalStars,
    IReadOnlyList<StudentMasteryDto> Mastery,
    IReadOnlyList<StudentEarnedBadgeDto> Badges);

/// <summary>Lượt em đang làm dở, hiện ở trang học để làm tiếp.</summary>
public sealed record StudentInProgressDto(
    long AttemptId,
    long QuizId,
    string? QuizName,
    long LessonId,
    string? LessonTitle,
    DateTime StartedAt,
    DateTime? Deadline,
    int? RemainingSeconds);

/// <summary>Bài kiểm tra còn mở em chưa làm. <see cref="NeedsLessonCode"/> bật (nhãn "Cần mã bài học") khi em chưa mở bài học này và chưa có lượt nào ở bài.</summary>
public sealed record StudentOpenQuizDto(
    long QuizId,
    string? Name,
    long LessonId,
    string? LessonTitle,
    DateTime? OpenAt,
    DateTime? CloseAt,
    int? TimeLimitMinutes,
    int QuestionCount,
    bool NeedsLessonCode,
    string? Label);

/// <summary>Bài học của em: bài em đã mở bằng mã hoặc đã có lượt làm bài, kể cả bài chưa có bài kiểm tra; kèm mã để mở lại.</summary>
public sealed record StudentLessonRefDto(
    long LessonId,
    string? Title,
    string? AccessCode,
    int QuizzesDone,
    int QuizzesPublished,
    DateTime? LastAttemptAt);

/// <summary>
/// Cấp 1: một bài học thầy cô đã xuất bản cho lớp em, hiện thẳng trên con đường (em không phải gõ mã). Xếp theo thứ tự thầy cô tạo bài.
/// <see cref="Stars"/> là số sao cao nhất (0-3) của các lượt em đã nộp ở bài, null khi em chưa nộp lượt nào.
/// <see cref="Completed"/> bật khi em đã tới bước Nhận thẻ của bài hoặc đã làm hết các bài chơi đã phát hành của bài.
/// </summary>
public sealed record StudentClassLessonDto(
    long LessonId,
    string? Title,
    string AccessCode,
    DateTime? CreatedAt,
    string? ThumbnailUrl,
    IReadOnlyList<string> HeritageNames,
    int QuizzesPublished,
    int QuizzesDone,
    int? Stars,
    bool Completed);

/// <summary>Em tới bước Nhận thẻ của một bài cấp 1: <see cref="FirstTime"/> chỉ bật ở lần ghi đầu; thẻ di sản của bài đã vào album.</summary>
public sealed record LessonCompletionDto(
    long LessonId,
    bool FirstTime,
    IReadOnlyList<StudentAlbumCardDto> Cards,
    int AlbumCollected,
    int AlbumTotal,
    string? TakeHome);

/// <summary>
/// Một câu ôn ở bước Nhớ lại, lấy từ các bài chơi em đã nộp ở bài khác. Có sẵn đáp án đúng (<see cref="CorrectIndex"/> tính theo thứ tự thẻ đã xáo
/// cố định theo mã học sinh và mã câu) vì bài đó đã nộp; không tính sao, không lưu gì.
/// </summary>
public sealed record RecallQuestionDto(
    long QuestionId,
    QuestionType QuestionType,
    string Content,
    string? ImageUrl,
    IReadOnlyList<QuestionPreviewOptionDto> Options,
    int CorrectIndex,
    string? Explanation,
    string? HeritageName,
    string? LessonTitle);

/// <summary>Các câu ôn của bước Nhớ lại: tối đa 3; rỗng khi em chưa nộp bài chơi nào của bài khác (giao diện bỏ bước).</summary>
public sealed record StudentRecallDto(IReadOnlyList<RecallQuestionDto> Questions);

public sealed record StudentRecentBadgeDto(
    BadgeCode Code,
    string Name,
    long AttemptId,
    string? QuizName,
    DateTime? EarnedAt);

/// <summary>
/// Trang học của em: thông tin của em, lớp, giáo viên, bố cục theo cấp, lượt đang làm dở, bài kiểm tra còn mở, bài học của em và huy hiệu gần đây.
/// <see cref="TotalStars"/> chỉ có ở cấp 1. Phần "Gợi ý cho em" do dịch vụ gợi ý tính riêng. Riêng cấp 1: <see cref="ClassLessons"/> là các bài thầy cô đã
/// xuất bản cho lớp (cấp khác là danh sách rỗng), <see cref="AlbumCollected"/> và <see cref="AlbumTotal"/> là số thẻ album em đã có và tổng số thẻ (cấp khác null);
/// <see cref="StreakDays"/> là tổng số ngày em có nộp bài, hiện là "Ngày đã học".
/// </summary>
public sealed record StudentHomeDto(
    long StudentId,
    string? StudentCode,
    string? FullName,
    string? ClassName,
    int Grade,
    int Level,
    string LevelName,
    string Layout,
    string? TeacherName,
    int? TotalStars,
    IReadOnlyList<StudentInProgressDto> InProgress,
    IReadOnlyList<StudentOpenQuizDto> OpenQuizzes,
    IReadOnlyList<StudentLessonRefDto> MyLessons,
    IReadOnlyList<StudentRecentBadgeDto> RecentBadges,
    IReadOnlyList<StudentClassLessonDto> ClassLessons,
    int? AlbumCollected,
    int? AlbumTotal,
    int? StreakDays = null,
    string? Cheer = null);
