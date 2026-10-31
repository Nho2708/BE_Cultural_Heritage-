using DSVHVN.Domain.Enums;

namespace DSVHVN.Application.Notifications;

/// <summary>Thầy cô gửi thông báo cho lớp; <see cref="Message"/> để trống thì dùng câu mặc định.</summary>
public sealed record NotifyClassRequest(string? Message);

/// <summary>Kết quả gửi: số em nhận được và câu báo.</summary>
public sealed record NotifyClassResultDto(int SentTo, string Message);

/// <summary>
/// Một thông báo của em. <see cref="LessonCode"/> là mã bài học để mở bài ngay; thông báo về bài kiểm tra có thêm <see cref="QuizId"/>.
/// </summary>
public sealed record StudentNotificationDto(
    long Id,
    StudentNotificationKind Kind,
    string Title,
    string Body,
    long LessonId,
    string? LessonCode,
    long? QuizId,
    DateTime? CreatedAt,
    bool IsRead);

public sealed record StudentNotificationPageDto(
    IReadOnlyList<StudentNotificationDto> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages,
    int UnreadCount);
