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

    /// <summary>Các phần nội dung theo khung của cấp, xếp theo <see cref="LessonSection.SortOrder"/> (CSDL v4).</summary>
    public List<LessonSection> Sections { get; set; } = [];
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
/// Bảng <c>lesson_sections</c> (CSDL v4): một phần nội dung của bài học theo khung của cấp, xếp theo <see cref="SortOrder"/> thành
/// luồng học. Cấp 1: chuyện kể từng bước (STORY, mỗi bước kèm một hình) và hình (IMAGE); cấp 2: bài đọc, hình có chú thích, mốc
/// thời gian; cấp 3: bài đọc, trích tư liệu gốc có nguồn, mốc thời gian, câu hỏi gợi suy nghĩ. AI soạn, giáo viên sửa rồi xuất bản.
/// Tách bảng (thay vì một cột JSON) để sửa, xếp lại từng phần và nối mốc thời gian tới sự kiện của di sản.
/// </summary>
public class LessonSection : IHasCreatedAt, IHasUpdatedAt
{
    public long Id { get; set; }

    public long LessonId { get; set; }
    public Lesson? Lesson { get; set; }

    public LessonSectionType SectionType { get; set; }

    public string? Title { get; set; }

    /// <summary>Nội dung chữ: đoạn chuyện, đoạn bài đọc, nội dung mốc, đoạn trích, câu hỏi gợi suy nghĩ, chú thích hình.</summary>
    public string? Body { get; set; }

    /// <summary>Hình của phần; bắt buộc với IMAGE, của bước câu chuyện thì giáo viên chọn trong thư viện ảnh của các di sản của bài.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Năm của mốc, bắt buộc với MILESTONE; năm âm là trước Công nguyên.</summary>
    public int? MilestoneYear { get; set; }

    /// <summary>Sự kiện của di sản mà mốc lấy từ; NULL khi giáo viên tự viết mốc.</summary>
    public long? EventId { get; set; }
    public TimelineEvent? Event { get; set; }

    /// <summary>Nguồn của đoạn trích, bắt buộc với SOURCE_EXCERPT.</summary>
    public string? SourceCitation { get; set; }

    public int SortOrder { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Bảng <c>ai_generations</c>: nhật ký mỗi lần gọi AI. Mỗi dòng thuộc đúng một bài học (soạn nội dung bài, tạo câu hỏi; do giáo viên
/// yêu cầu) hoặc một di sản (dịch, tóm tắt; do quản trị hệ thống yêu cầu). Truy vết đầu vào, tham số, kết quả thô;
/// các dòng của giáo viên cũng là cơ sở đếm lượt AI theo gói.
/// </summary>
public class AiGeneration : IHasCreatedAt
{
    public long Id { get; set; }

    public AiTaskType? TaskType { get; set; }

    /// <summary>Bài học liên quan, với soạn nội dung bài và tạo câu hỏi; NULL khi dịch, tóm tắt di sản.</summary>
    public long? LessonId { get; set; }
    public Lesson? Lesson { get; set; }

    /// <summary>Di sản được dịch hoặc tóm tắt (CSDL v4); NULL với tác vụ của bài học.</summary>
    public long? HeritageId { get; set; }
    public Heritage? Heritage { get; set; }

    /// <summary>Người yêu cầu: giáo viên với tác vụ bài học, quản trị hệ thống với tác vụ di sản.</summary>
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

/// <summary>
/// Bảng <c>lesson_completions</c>: em ở lớp cấp 1 đã tới bước Nhận thẻ của một bài học (thẻ di sản của bài vào album của em).
/// (<c>student_id</c>, <c>lesson_id</c>) không trùng: mỗi em chỉ ghi một lần cho mỗi bài, lần ghi đầu giữ nguyên thời điểm.
/// </summary>
public class LessonCompletion
{
    public long Id { get; set; }

    public long StudentId { get; set; }
    public Student? Student { get; set; }

    public long LessonId { get; set; }
    public Lesson? Lesson { get; set; }

    /// <summary>Lúc em tới bước Nhận thẻ lần đầu (UTC).</summary>
    public DateTime CompletedAt { get; set; }
}

/// <summary>
/// Bảng <c>lesson_visits</c>: em đã mở bài học nào bằng mã, để bài vẫn nằm trong "Bài học của em" ở mọi thiết bị kể cả khi bài chưa có bài kiểm tra.
/// Mỗi em mỗi bài một dòng; mở lại thì cập nhật <see cref="LastOpenedAt"/>.
/// </summary>
public class LessonVisit
{
    public long Id { get; set; }

    public long StudentId { get; set; }
    public Student? Student { get; set; }

    public long LessonId { get; set; }
    public Lesson? Lesson { get; set; }

    /// <summary>Lúc em mở bài lần đầu (UTC).</summary>
    public DateTime FirstOpenedAt { get; set; }

    /// <summary>Lúc em mở bài gần nhất (UTC).</summary>
    public DateTime LastOpenedAt { get; set; }
}
