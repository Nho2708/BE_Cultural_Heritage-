using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Quizzes;

namespace DSVHVN.Domain.Notifications;

/// <summary>
/// Bảng <c>student_notifications</c>: thông báo thầy cô gửi cho từng em trong lớp về một bài học hoặc bài kiểm tra đã phát hành (giao bài về nhà).
/// Mỗi lần gửi tạo một dòng cho mỗi em; em đọc rồi thì <see cref="ReadAt"/> có giá trị.
/// </summary>
public class StudentNotification : IHasCreatedAt
{
    public long Id { get; set; }

    public long StudentId { get; set; }
    public Student? Student { get; set; }

    public StudentNotificationKind Kind { get; set; }

    public long LessonId { get; set; }
    public Lesson? Lesson { get; set; }

    /// <summary>Bài kiểm tra được giao; NULL với thông báo về bài học.</summary>
    public long? QuizId { get; set; }
    public Quiz? Quiz { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>Lời nhắn của thầy cô, hoặc câu mặc định khi thầy cô để trống.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Thầy cô đã gửi.</summary>
    public long SentBy { get; set; }
    public User? Sender { get; set; }

    public DateTime? CreatedAt { get; set; }

    /// <summary>Lúc em đọc (UTC); NULL là chưa đọc.</summary>
    public DateTime? ReadAt { get; set; }
}
