namespace DSVHVN.Domain.Rules;

/// <summary>Quy tắc thuần của thông báo cho học sinh.</summary>
public static class StudentNotificationRules
{
    public const int TitleMaxLength = 300;

    /// <summary>Lời nhắn của thầy cô tối đa 500 ký tự.</summary>
    public const int MessageMaxLength = 500;

    /// <summary>Lời nhắn đã bỏ khoảng trắng hai đầu, để trống thì lấy câu mặc định.</summary>
    public static string BodyOr(string? message, string fallback) => string.IsNullOrWhiteSpace(message) ? fallback : message.Trim();
}
