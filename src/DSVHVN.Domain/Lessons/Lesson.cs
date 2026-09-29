using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Identity;

namespace DSVHVN.Domain.Lessons;

/// <summary>
/// Bảng <c>lessons</c>: bài học do giáo viên tạo cho đúng một lớp, dựa trên một hoặc nhiều di sản.
/// Xuất bản lần đầu thì hệ thống sinh <see cref="AccessCode"/>: học sinh của lớp (đã đăng nhập) nhập mã để mở
/// cả bài học lẫn quiz của bài. Dùng cho lớp khác thì nhân bản bài học.
/// </summary>
public class Lesson : IHasCreatedAt, IHasUpdatedAt, ISoftDeletable
{
    public long Id { get; set; }

    public long TeacherId { get; set; }
    public User? Teacher { get; set; }

    public long ClassId { get; set; }
    public SchoolClass? Class { get; set; }

    public string? Title { get; set; }

    /// <summary>Tóm tắt; ban đầu do AI sinh, giáo viên chỉnh sửa rồi lưu.</summary>
    public string? Summary { get; set; }

    /// <summary>Danh sách ý chính, lưu chuỗi JSON dạng mảng.</summary>
    public string? KeyPoints { get; set; }

    public ContentStatus? Status { get; set; }

    /// <summary>Mã bài học, không trùng khi có giá trị; bài chưa xuất bản chưa có mã.</summary>
    public string? AccessCode { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public List<LessonHeritage> Heritages { get; set; } = [];
}

/// <summary>Bảng <c>lesson_heritages</c>: nối bài học với di sản (nhiều - nhiều), khóa chính kép.</summary>
public class LessonHeritage
{
    public long LessonId { get; set; }
    public Lesson? Lesson { get; set; }

    public long HeritageId { get; set; }
    public Heritage? Heritage { get; set; }
}

/// <summary>
/// Bảng <c>ai_generations</c>: nhật ký mỗi lần gọi AI (tóm tắt bài học, tạo câu hỏi). Truy vết đầu vào, tham số, kết quả thô;
/// cũng là cơ sở đếm lượt AI theo gói.
/// </summary>
public class AiGeneration : IHasCreatedAt
{
    public long Id { get; set; }

    public AiTaskType? TaskType { get; set; }

    public long LessonId { get; set; }
    public Lesson? Lesson { get; set; }

    /// <summary>Giáo viên yêu cầu.</summary>
    public long RequestedBy { get; set; }
    public User? Requester { get; set; }

    public string? InputSnapshot { get; set; }

    /// <summary>Tham số yêu cầu (chuỗi JSON).</summary>
    public string? Params { get; set; }

    public string? Model { get; set; }

    public string? RawOutput { get; set; }

    public AiTaskStatus? Status { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}
