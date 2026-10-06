using DSVHVN.Domain.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Lessons;

namespace DSVHVN.Domain.Heritages;

// Nhóm Đa ngôn ngữ của CSDL v4. Tiếng Việt là bản gốc nằm ngay trong heritages, timelines, events, media nên không có dòng bản dịch nào cho
// tiếng Việt (chỉ bản tóm tắt có dòng tiếng Việt). Đơn vị duyệt là (di sản, ngôn ngữ): trạng thái nằm ở heritage_translations và áp cho cả
// bản dịch giai đoạn, sự kiện, chú thích ảnh của di sản cùng ngôn ngữ.

/// <summary>
/// Bảng <c>heritage_translations</c>: bản dịch các trường chữ của di sản sang en, ko, zh, ja; đơn vị duyệt của bản dịch. Khách xem tiếng Anh chỉ khi APPROVED;
/// tiếng Hàn, Trung, Nhật hiện ngay (MACHINE) kèm nhãn "bản dịch máy". Mỗi (di sản, ngôn ngữ) đúng một dòng: dịch lại thì bản mới thay bản cũ.
/// </summary>
public class HeritageTranslation : IHasCreatedAt, IHasUpdatedAt
{
    public long Id { get; set; }

    public long HeritageId { get; set; }
    public Heritage? Heritage { get; set; }

    public LanguageCode LanguageCode { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? ShortDescription { get; set; }

    public string? Content { get; set; }

    /// <summary>Địa chỉ đã dịch hoặc phiên âm. Quy tắc nghiệp vụ giữ địa chỉ như văn bản xếp hạng nên AI không dịch; cột để quản trị nhập tay nếu cần.</summary>
    public string? Address { get; set; }

    public TranslationStatus Status { get; set; }

    /// <summary>Cần dịch lại: nội dung tiếng Việt đã sửa sau khi dịch. Vẫn hiện bản cũ tới khi dịch lại.</summary>
    public bool IsOutdated { get; set; }

    /// <summary>Lần gọi AI đã dịch; NULL nếu quản trị nhập tay.</summary>
    public long? AiGenerationId { get; set; }
    public AiGeneration? AiGeneration { get; set; }

    /// <summary>Quản trị hệ thống đã duyệt hoặc từ chối; NULL khi chưa duyệt và với bản dịch máy.</summary>
    public long? ReviewedBy { get; set; }
    public User? Reviewer { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>Bảng <c>timeline_translations</c>: tên và mô tả giai đoạn đã dịch. Hiện theo trạng thái của <see cref="HeritageTranslation"/> cùng di sản, cùng ngôn ngữ. Xóa giai đoạn thì xóa luôn bản dịch.</summary>
public class TimelineTranslation : IHasCreatedAt, IHasUpdatedAt
{
    public long Id { get; set; }

    public long TimelineId { get; set; }
    public Timeline? Timeline { get; set; }

    public LanguageCode LanguageCode { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>Bảng <c>event_translations</c>: tên và mô tả sự kiện đã dịch. Xóa sự kiện thì xóa luôn bản dịch.</summary>
public class EventTranslation : IHasCreatedAt, IHasUpdatedAt
{
    public long Id { get; set; }

    public long EventId { get; set; }
    public TimelineEvent? Event { get; set; }

    public LanguageCode LanguageCode { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>Bảng <c>media_translations</c>: chú thích ảnh đã dịch (ghi công không dịch). Xóa ảnh thì xóa luôn bản dịch.</summary>
public class MediaTranslation : IHasCreatedAt, IHasUpdatedAt
{
    public long Id { get; set; }

    public long MediaId { get; set; }
    public Media? Media { get; set; }

    public LanguageCode LanguageCode { get; set; }

    public string Caption { get; set; } = string.Empty;

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Bảng <c>heritage_summaries</c>: tóm tắt di sản theo ngôn ngữ (vi, en, ko, zh, ja), mỗi dòng gồm bản ngắn (nghe thuyết minh) và bản chi tiết (đọc),
/// duyệt một lần cho cả hai. Lưu sẵn, không gọi AI khi khách xem.
/// </summary>
public class HeritageSummary : IHasCreatedAt, IHasUpdatedAt
{
    public long Id { get; set; }

    public long HeritageId { get; set; }
    public Heritage? Heritage { get; set; }

    public LanguageCode LanguageCode { get; set; }

    public string ShortText { get; set; } = string.Empty;

    public string DetailedText { get; set; } = string.Empty;

    public TranslationStatus Status { get; set; }

    public bool IsOutdated { get; set; }

    public long? AiGenerationId { get; set; }
    public AiGeneration? AiGeneration { get; set; }

    public long? ReviewedBy { get; set; }
    public User? Reviewer { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
