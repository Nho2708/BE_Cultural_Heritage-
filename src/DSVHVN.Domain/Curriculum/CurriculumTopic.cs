using DSVHVN.Domain.Common;
using DSVHVN.Domain.Heritages;

namespace DSVHVN.Domain.Curriculum;

/// <summary>
/// Bảng <c>curriculum_topics</c>: chủ đề của chương trình môn học (Chương trình giáo dục phổ thông 2018, đã cập nhật theo Thông tư 17/2025/TT-BGDĐT)
/// mà giáo viên dùng để chọn di sản khi tạo bài. Mỗi chủ đề thuộc một khối và một môn; (khối, môn, chủ đề lớn, nội dung) không trùng.
/// Chủ đề "Địa phương em" (<see cref="LocalProvince"/>) không gắn di sản cố định vì phụ thuộc tỉnh của trường. Không xóa cứng:
/// chủ đề ngừng dùng (<see cref="IsActive"/> = false) ẩn với giáo viên nhưng vẫn giữ liên kết với di sản.
/// </summary>
public class CurriculumTopic : IHasCreatedAt, IHasUpdatedAt
{
    public int Id { get; set; }

    /// <summary>Tên môn đúng văn bản chương trình, ví dụ "Lịch sử và Địa lí", "Lịch sử".</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Khối 1-12.</summary>
    public byte Grade { get; set; }

    /// <summary>Chủ đề lớn (nhóm).</summary>
    public string GroupTitle { get; set; } = string.Empty;

    /// <summary>Nội dung của chủ đề.</summary>
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Chủ đề theo tỉnh, thành phố của trường: giáo viên chọn tỉnh để lọc di sản, không có di sản cố định. bit NOT NULL DEFAULT 0.</summary>
    public bool LocalProvince { get; set; }

    /// <summary>Thứ tự trong chương trình.</summary>
    public int SortOrder { get; set; }

    public string? SourceUrl { get; set; }

    /// <summary>Còn dùng không. bit NOT NULL DEFAULT 1.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public ICollection<HeritageCurriculumTopic> Heritages { get; set; } = [];
}

/// <summary>Bảng <c>heritage_curriculum_topics</c>: di sản thuộc chủ đề chương trình nào. Khóa chính (di sản, chủ đề); xóa chủ đề thì xóa liên kết.</summary>
public class HeritageCurriculumTopic
{
    public long HeritageId { get; set; }
    public Heritage? Heritage { get; set; }

    public int TopicId { get; set; }
    public CurriculumTopic? Topic { get; set; }
}
