namespace DSVHVN.Infrastructure.Background;

/// <summary>Mục <c>Quizzes</c> của cấu hình: chu kỳ của tác vụ nền tự đóng bài kiểm tra quá giờ đóng. Bằng 0 hoặc âm thì tắt tác vụ nền.</summary>
public sealed class QuizAutoCloseOptions
{
    public const string Section = "Quizzes";

    public int AutoCloseIntervalSeconds { get; set; } = 60;
}
