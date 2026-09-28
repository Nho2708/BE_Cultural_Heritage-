using DSVHVN.Domain.Common;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Heritages;

/// <summary>Bảng <c>provinces</c>: 34 tỉnh/thành, dùng lọc di sản theo địa phương và theo vùng miền trên bản đồ.</summary>
public class Province
{
    public int Id { get; set; }

    /// <summary>Mã tỉnh/thành theo mã hành chính nhà nước, không trùng.</summary>
    public string? Code { get; set; }

    public string? Name { get; set; }

    /// <summary>Vùng miền: Bắc, Trung, Nam.</summary>
    public string? Region { get; set; }
}

/// <summary>
/// Bảng <c>heritages</c>: bảng trung tâm của kho di sản. Tọa độ để ghim bản đồ, nội dung cho trang chi tiết
/// và là tài liệu nguồn cho AI. Chỉ di sản PUBLISHED hiện công khai.
/// </summary>
public class Heritage : IHasCreatedAt, IHasUpdatedAt, ISoftDeletable
{
    public long Id { get; set; }

    /// <summary>Tỉnh chính; NULL với di sản phi vật thể trải rộng nhiều tỉnh.</summary>
    public int? ProvinceId { get; set; }
    public Province? Province { get; set; }

    public string? Name { get; set; }

    /// <summary>Chuỗi làm URL thân thiện, không trùng.</summary>
    public string? Slug { get; set; }

    public string? ShortDescription { get; set; }

    /// <summary>Nội dung chi tiết đầy đủ (nvarchar(max)).</summary>
    public string? Content { get; set; }

    public HeritageType? HeritageType { get; set; }

    public RecognitionLevel? RecognitionLevel { get; set; }

    public int? RecognizedYear { get; set; }

    public string? Address { get; set; }

    /// <summary>decimal(9,6). NULL với di sản không ghim được trên bản đồ.</summary>
    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    /// <summary>Mã tham chiếu bên ngoài (Wikidata hoặc Google Place ID).</summary>
    public string? PlaceId { get; set; }

    public string? ThumbnailUrl { get; set; }

    public ContentStatus? Status { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public List<Timeline> Timelines { get; set; } = [];
    public List<Media> Media { get; set; } = [];
    public List<HeritageReference> References { get; set; } = [];
}

/// <summary>Bảng <c>timelines</c>: giai đoạn lịch sử của một di sản; mỗi giai đoạn chứa nhiều sự kiện.</summary>
public class Timeline
{
    public long Id { get; set; }

    public long HeritageId { get; set; }
    public Heritage? Heritage { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Năm âm là trước Công nguyên.</summary>
    public int? StartYear { get; set; }

    public int? EndYear { get; set; }

    public int? SortOrder { get; set; }

    public List<TimelineEvent> Events { get; set; } = [];
}

/// <summary>
/// Bảng <c>events</c>: sự kiện trong một giai đoạn. Năm bắt đầu bắt buộc (NOT NULL) để xếp lên timeline;
/// tháng, ngày được NULL vì nhiều sự kiện lịch sử chỉ biết năm.
/// </summary>
public class TimelineEvent
{
    public long Id { get; set; }

    public long TimelineId { get; set; }
    public Timeline? Timeline { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public int StartYear { get; set; }

    public short? StartMonth { get; set; }

    public short? StartDay { get; set; }

    public int? EndYear { get; set; }

    public short? EndMonth { get; set; }

    public short? EndDay { get; set; }

    public int? SortOrder { get; set; }

    public List<Media> Media { get; set; } = [];
}

/// <summary>
/// Bảng <c>media</c>: ảnh, video, audio minh họa. Mỗi dòng thuộc đúng một trong hai: di sản hoặc sự kiện (CHECK trong CSDL).
/// </summary>
public class Media
{
    public long Id { get; set; }

    public long? HeritageId { get; set; }
    public Heritage? Heritage { get; set; }

    public long? EventId { get; set; }
    public TimelineEvent? Event { get; set; }

    /// <summary>IMAGE, VIDEO, AUDIO (chuỗi, không CHECK).</summary>
    public string? MediaType { get; set; }

    public string? Url { get; set; }

    public string? Caption { get; set; }

    /// <summary>Nguồn / tác giả / giấy phép.</summary>
    public string? Credit { get; set; }

    public int? SortOrder { get; set; }
}

/// <summary>Bảng <c>heritage_references</c>: nguồn trích dẫn của nội dung di sản (Cục Di sản văn hóa, văn bản xếp hạng, UNESCO).</summary>
public class HeritageReference
{
    public long Id { get; set; }

    public long HeritageId { get; set; }
    public Heritage? Heritage { get; set; }

    public string? Title { get; set; }

    public string? Url { get; set; }

    /// <summary>BOOK, WEBSITE, GOV_DOC... (chuỗi, không CHECK).</summary>
    public string? SourceType { get; set; }
}
