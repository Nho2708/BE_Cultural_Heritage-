using DSVHVN.Application.Heritages;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Discovery;

// Dữ liệu ra của phần khám phá di sản công khai (web học tập, không cần đăng nhập). Chỉ gồm di sản đã xuất bản (PUBLISHED)
// chưa xóa mềm; không có trạng thái, điều kiện xuất bản hay mốc tạo/sửa. Giá trị enum viết HOA như CSDL; nhãn tiếng Việt lấy ở danh mục bộ lọc.

/// <summary>
/// Bộ lọc dùng chung cho tìm kiếm và bản đồ; các điều kiện kết hợp theo VÀ. <c>Keyword</c> 2-100 ký tự, không phân biệt hoa thường và dấu,
/// tìm trên tên, mô tả ngắn, địa chỉ và nội dung (nhiều từ thì di sản phải có đủ các từ). <c>Region</c> là Bắc, Trung hoặc Nam
/// (gõ có hoặc không dấu đều nhận). <c>Period</c> là thời kỳ theo khoảng năm cố định, so với giai đoạn và sự kiện của timeline.
/// </summary>
public sealed record HeritageFilter(
    string? Keyword,
    int? ProvinceId,
    string? Region,
    HeritageType? HeritageType,
    RecognitionLevel? RecognitionLevel,
    HeritagePeriod? Period);

/// <summary>
/// Thẻ di sản ở trang chủ và kết quả tìm kiếm: ảnh đại diện, tên, mô tả ngắn, tỉnh, loại, cấp xếp hạng. Tên và mô tả ngắn theo ngôn ngữ yêu cầu:
/// <c>IsFallback</c> bật khi chưa có bản hiện hành nên đang hiện tiếng Việt (giao diện hiện thông báo một lần ở đầu danh sách), <c>IsMachineTranslated</c> bật với bản dịch máy.
/// </summary>
public sealed record HeritageCardDto(
    long Id,
    string Slug,
    string Name,
    string? ShortDescription,
    string? ThumbnailUrl,
    int? ProvinceId,
    string? ProvinceName,
    string? Region,
    HeritageType? HeritageType,
    RecognitionLevel? RecognitionLevel,
    int? RecognizedYear,
    bool IsFallback = false,
    bool IsMachineTranslated = false);

/// <summary>Một mục đếm ở trang chủ: <c>Value</c> là giá trị dùng làm bộ lọc, <c>Label</c> là nhãn hiển thị.</summary>
public sealed record CountItemDto(string Value, string Label, int Count);

/// <summary>Trang chủ: tối đa 8 di sản nổi bật (UNESCO hoặc quốc gia đặc biệt, mới cập nhật trước), tổng số và số theo loại, theo vùng.</summary>
public sealed record HomeDto(
    IReadOnlyList<HeritageCardDto> Featured,
    int TotalHeritages,
    IReadOnlyList<CountItemDto> ByType,
    IReadOnlyList<CountItemDto> ByRegion);

/// <summary>
/// Ghim trên bản đồ (chỉ di sản có tọa độ): đủ để hiện popup ảnh, tên, tỉnh, loại, mô tả ngắn;
/// <c>HasTimeline</c> cho biết có nút "Mở dòng thời gian" hay không.
/// </summary>
public sealed record HeritagePinDto(
    long Id,
    string Slug,
    string Name,
    decimal Latitude,
    decimal Longitude,
    string? ShortDescription,
    string? ThumbnailUrl,
    int? ProvinceId,
    string? ProvinceName,
    HeritageType? HeritageType,
    RecognitionLevel? RecognitionLevel,
    bool HasTimeline,
    bool IsFallback = false,
    bool IsMachineTranslated = false);

/// <summary>
/// Kết quả bản đồ theo bộ lọc. <c>TotalMatched</c> là số di sản khớp bộ lọc; <c>WithoutLocation</c> là số di sản khớp nhưng không có
/// tọa độ (thường là di sản phi vật thể) để giao diện ghi chú; <c>Pins</c> tối đa 500 ghim, cấp xếp hạng cao trước, và
/// <c>IsTruncated</c> báo còn ghim chưa trả (nên thu hẹp bộ lọc).
/// </summary>
public sealed record HeritageMapDto(
    IReadOnlyList<HeritagePinDto> Pins,
    int TotalMatched,
    int WithoutLocation,
    bool IsTruncated);

/// <summary>Ảnh, video, audio của di sản hoặc sự kiện kèm ghi công tách ba phần (tác giả, giấy phép, nguồn) để hiện dưới ảnh.</summary>
public sealed record PublicMediaDto(
    long Id,
    string? MediaType,
    string? Url,
    string? Caption,
    string? Credit,
    string? Author,
    string? License,
    string? Source,
    bool IsThumbnail,
    string? PreviewUrl = null);

/// <summary>Giai đoạn rút gọn ở trang chi tiết: tên, khoảng năm (chữ hiển thị sẵn) và số sự kiện.</summary>
public sealed record TimelinePhaseSummaryDto(
    long Id,
    string? Name,
    int? StartYear,
    int? EndYear,
    string RangeLabel,
    int EventCount);

/// <summary>
/// Chi tiết di sản: nội dung, thư viện ảnh kèm ghi công, vị trí, danh sách nguồn ("Nguồn: Cục Di sản văn hóa; …") và timeline rút gọn.
/// <c>HasTimeline</c> bật nút "Mở dòng thời gian". <c>Localization</c> cho biết ngôn ngữ đang hiện (nhãn "bản dịch máy", thông báo thiếu bản dịch);
/// <c>HasSummary</c> bật khi di sản có tóm tắt tiếng Việt đã duyệt (nút "Tóm tắt" và "Nghe"; không có thì báo chưa có tóm tắt, không hiện cổng quảng cáo).
/// </summary>
public sealed record PublicHeritageDetailDto(
    long Id,
    string Slug,
    string Name,
    string? ShortDescription,
    string? Content,
    HeritageType? HeritageType,
    RecognitionLevel? RecognitionLevel,
    int? RecognizedYear,
    ProvinceDto? Province,
    string? Address,
    decimal? Latitude,
    decimal? Longitude,
    string? ThumbnailUrl,
    bool HasTimeline,
    IReadOnlyList<PublicMediaDto> Media,
    IReadOnlyList<ReferenceDto> References,
    IReadOnlyList<TimelinePhaseSummaryDto> Timeline,
    LocalizationDto? Localization = null,
    bool HasSummary = false);

/// <summary>
/// Sự kiện của giai đoạn. Năm âm là trước Công nguyên. <c>DateLabel</c> là chữ hiển thị sẵn ("15/03/1010", "1010",
/// "1945 – 1954", "179 TCN") theo quy tắc năm-tháng-ngày của timeline.
/// </summary>
public sealed record PublicEventDto(
    long Id,
    string? Name,
    string? Description,
    int StartYear,
    short? StartMonth,
    short? StartDay,
    int? EndYear,
    short? EndMonth,
    short? EndDay,
    string DateLabel,
    IReadOnlyList<PublicMediaDto> Media);

/// <summary>Giai đoạn theo thứ tự hiển thị, kèm sự kiện xếp theo năm-tháng-ngày rồi thứ tự. <c>RangeLabel</c> vd "1010 – 1821", "1962 – nay".</summary>
public sealed record PublicPhaseDto(
    long Id,
    string? Name,
    string? Description,
    int? StartYear,
    int? EndYear,
    string RangeLabel,
    IReadOnlyList<PublicEventDto> Events);

/// <summary>Timeline đầy đủ của một di sản. Di sản chưa có giai đoạn nào vẫn trả 200 với <c>Phases</c> rỗng.</summary>
public sealed record PublicTimelineDto(
    long HeritageId,
    string Slug,
    string Name,
    string? ThumbnailUrl,
    string? ProvinceName,
    IReadOnlyList<PublicPhaseDto> Phases,
    LocalizationDto? Localization = null);

/// <summary>Một lựa chọn của danh mục bộ lọc (giá trị gửi lên và nhãn hiển thị).</summary>
public sealed record FilterOptionDto(string Value, string Label);

/// <summary>Thời kỳ của bộ lọc: khoảng năm cố định, <c>FromYear</c> hoặc <c>ToYear</c> null là không giới hạn phía đó (năm âm là trước Công nguyên).</summary>
public sealed record PeriodOptionDto(string Value, string Label, int? FromYear, int? ToYear);

/// <summary>Toàn bộ danh mục cho các ô lọc: 34 tỉnh (kèm vùng), vùng, loại, cấp xếp hạng, thời kỳ.</summary>
public sealed record FilterCatalogDto(
    IReadOnlyList<ProvinceDto> Provinces,
    IReadOnlyList<FilterOptionDto> Regions,
    IReadOnlyList<FilterOptionDto> HeritageTypes,
    IReadOnlyList<FilterOptionDto> RecognitionLevels,
    IReadOnlyList<PeriodOptionDto> Periods);
