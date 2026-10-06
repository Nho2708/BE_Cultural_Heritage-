using DSVHVN.Domain.Enums;
using DSVHVN.Application.Translations;

namespace DSVHVN.Application.Heritages;

// Dữ liệu vào và ra của kho di sản (chỉ quản trị hệ thống). Tên trường JSON là camelCase của các thuộc tính dưới đây.
// Giá trị enum là tên viết HOA đúng như CSDL (TANGIBLE, PUBLISHED...); giao diện hiện nhãn tiếng Việt tương ứng.

/// <summary>Tỉnh/thành (34 đơn vị theo Quyết định 19/2025): chỉ đọc, không có chức năng sửa.</summary>
public sealed record ProvinceDto(int Id, string? Code, string? Name, string? Region);

/// <summary>Bộ lọc màn hình Quản lý di sản; các điều kiện kết hợp theo VÀ. Từ khóa tìm theo tên, không phân biệt hoa thường và dấu.</summary>
public sealed record HeritageListQuery(
    string? Keyword,
    int? ProvinceId,
    HeritageType? HeritageType,
    RecognitionLevel? RecognitionLevel,
    ContentStatus? Status,
    int? Page,
    int? PageSize,
    TranslationState? TranslationState = null,
    bool? HasModel = null);

/// <summary>Dòng của màn hình Quản lý di sản: ảnh, tên, tỉnh, loại, cấp xếp hạng, trạng thái, số giai đoạn, số nguồn, tình trạng bản dịch, có mô hình 3D đang hiển thị.</summary>
public sealed record HeritageSummaryDto(
    long Id,
    string? Name,
    string? Slug,
    string? ThumbnailUrl,
    int? ProvinceId,
    string? ProvinceName,
    HeritageType? HeritageType,
    RecognitionLevel? RecognitionLevel,
    ContentStatus Status,
    int TimelineCount,
    int MediaCount,
    int ReferenceCount,
    DateTime? CreatedAt,
    DateTime? UpdatedAt,
    TranslationState TranslationState = TranslationState.NONE,
    bool HasModel = false);

/// <summary>
/// Thông tin chung của di sản (tab Thông tin chung). <c>Slug</c> bỏ trống khi tạo thì sinh từ tên, khi sửa thì giữ đường dẫn hiện tại.
/// Ảnh đại diện không nằm ở đây: chọn trong thư viện ảnh qua <c>PUT /heritages/{id}/thumbnail</c>.
/// </summary>
public sealed record HeritageRequest(
    string? Name,
    string? Slug,
    string? ShortDescription,
    string? Content,
    HeritageType? HeritageType,
    RecognitionLevel? RecognitionLevel,
    int? RecognizedYear,
    int? ProvinceId,
    string? Address,
    decimal? Latitude,
    decimal? Longitude,
    string? PlaceId);

/// <summary>Chọn hoặc bỏ ảnh đại diện; <c>MediaId</c> phải là ảnh (IMAGE) trong thư viện của chính di sản, null là bỏ chọn.</summary>
public sealed record ThumbnailRequest(long? MediaId);

/// <summary>Điều kiện xuất bản còn thiếu: <c>Field</c> là khóa trường để giao diện đánh dấu tab, <c>Label</c> là tên hiển thị.</summary>
public sealed record MissingRequirementDto(string Field, string Label);

/// <summary>
/// Trạng thái sẵn sàng xuất bản. <c>Missing</c> chặn xuất bản; <c>Warnings</c> chỉ nhắc theo quy trình của nhóm
/// (tối thiểu một văn bản nhà nước và một bài của Cục Di sản văn hóa hoặc UNESCO), hệ thống không cưỡng chế.
/// </summary>
public sealed record PublishReadinessDto(
    bool IsReady,
    IReadOnlyList<MissingRequirementDto> Missing,
    IReadOnlyList<string> Warnings);

public sealed record HeritageDetailDto(
    long Id,
    string? Name,
    string? Slug,
    string? ShortDescription,
    string? Content,
    HeritageType? HeritageType,
    RecognitionLevel? RecognitionLevel,
    int? RecognizedYear,
    ProvinceDto? Province,
    string? Address,
    decimal? Latitude,
    decimal? Longitude,
    string? PlaceId,
    string? ThumbnailUrl,
    ContentStatus Status,
    DateTime? CreatedAt,
    DateTime? UpdatedAt,
    PublishReadinessDto Readiness,
    IReadOnlyList<TimelineDto> Timelines,
    IReadOnlyList<MediaDto> Media,
    IReadOnlyList<ReferenceDto> References,
    TranslationState TranslationState = TranslationState.NONE);

/// <summary>Giai đoạn của di sản kèm sự kiện đã xếp theo năm-tháng-ngày rồi thứ tự. Năm âm là trước Công nguyên.</summary>
public sealed record TimelineDto(
    long Id,
    long HeritageId,
    string? Name,
    string? Description,
    int? StartYear,
    int? EndYear,
    int? SortOrder,
    IReadOnlyList<EventDto> Events);

public sealed record EventDto(
    long Id,
    long TimelineId,
    string? Name,
    string? Description,
    int StartYear,
    short? StartMonth,
    short? StartDay,
    int? EndYear,
    short? EndMonth,
    short? EndDay,
    int? SortOrder,
    IReadOnlyList<MediaDto> Media);

/// <summary>
/// Giai đoạn: tên bắt buộc, năm bắt đầu/kết thúc tùy chọn (kết thúc không nhỏ hơn bắt đầu).
/// <c>SortOrder</c> bỏ trống khi tạo thì xếp cuối, khi sửa thì giữ nguyên.
/// </summary>
public sealed record TimelineRequest(string? Name, string? Description, int? StartYear, int? EndYear, int? SortOrder);

/// <summary>
/// Sự kiện: tên và năm bắt đầu bắt buộc; tháng, ngày tùy chọn (chỉ nhập ngày khi có tháng); thời điểm kết thúc tùy chọn
/// và không trước thời điểm bắt đầu. <c>SortOrder</c> bỏ trống khi tạo thì xếp cuối, khi sửa thì giữ nguyên.
/// </summary>
public sealed record EventRequest(
    string? Name,
    string? Description,
    int? StartYear,
    short? StartMonth,
    short? StartDay,
    int? EndYear,
    short? EndMonth,
    short? EndDay,
    int? SortOrder);

/// <summary>Thứ tự mới: danh sách id đủ và đúng các phần tử hiện có, theo thứ tự hiển thị từ trên xuống.</summary>
public sealed record OrderRequest(IReadOnlyList<long>? Ids);

/// <summary>
/// Ảnh, video, audio minh họa (đường dẫn, không tải tệp lên). <c>Author</c>, <c>License</c>, <c>Source</c> bắt buộc và được ghép
/// thành ghi công "tác giả · giấy phép · nguồn" trong <c>Credit</c>. <c>SetAsThumbnail</c> chỉ áp cho ảnh của di sản.
/// </summary>
public sealed record MediaRequest(
    string? MediaType,
    string? Url,
    string? Caption,
    string? Author,
    string? License,
    string? Source,
    int? SortOrder,
    bool? SetAsThumbnail);

public sealed record MediaDto(
    long Id,
    long? HeritageId,
    long? EventId,
    string? MediaType,
    string? Url,
    string? Caption,
    string? Credit,
    string? Author,
    string? License,
    string? Source,
    int? SortOrder,
    bool IsThumbnail,
    string? PreviewUrl = null);

/// <summary>
/// Thông tin kèm tệp ảnh tải lên (ghi công đủ ba phần như thêm ảnh bằng đường dẫn); tệp gửi riêng dạng multipart.
/// Loại media luôn là IMAGE, đường dẫn do hệ thống sinh.
/// </summary>
public sealed record UploadMediaRequest(
    string? Caption,
    string? Author,
    string? License,
    string? Source,
    int? SortOrder,
    bool? SetAsThumbnail);

/// <summary>Nguồn tham chiếu: tiêu đề bắt buộc, loại nguồn GOV_DOC | WEBSITE | BOOK, nguồn WEBSITE bắt buộc có đường dẫn.</summary>
public sealed record ReferenceRequest(string? Title, string? Url, string? SourceType);

public sealed record ReferenceDto(long Id, long HeritageId, string? Title, string? Url, string? SourceType, string SourceTypeLabel);
