using DSVHVN.Application.Discovery;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Guests;

// Dữ liệu vào và ra của khách tham quan (không tài khoản). Khách nhận diện bằng mã thiết bị ẩn danh gửi ở tiêu đề X-Device-Key (UUID phiên bản 4).
// Enum viết HOA như CSDL; <c>Language</c> là mã BCP 47 của giao diện (vi, en, ko, zh-Hans, ja).

/// <summary>
/// Đăng ký hoặc cập nhật thiết bị. Thiếu <see cref="Platform"/> thì lấy tiêu đề X-Client-Platform, không có nữa thì WEB.
/// Thiếu trường nào thì giữ nguyên giá trị đang lưu của trường đó.
/// </summary>
public sealed record GuestDeviceRequest(ClientPlatform? Platform, string? Language, bool? LocationAllowed);

/// <summary>
/// Thiết bị của khách. <see cref="IsRegistered"/> false khi máy chủ chưa từng thấy mã này (chưa có bản ghi; các giá trị là mặc định).
/// <see cref="LocationAllowed"/> chỉ là cờ nhớ lựa chọn "Gần bạn", không chứa tọa độ.
/// </summary>
public sealed record GuestDeviceDto(
    string DeviceKey,
    bool IsRegistered,
    ClientPlatform? Platform,
    string Language,
    bool LocationAllowed,
    DateTime? CreatedAt,
    DateTime? LastSeenAt);

/// <summary>Ghi một lượt: <see cref="Type"/> bắt buộc; tìm kiếm cần <see cref="Query"/> (2-100 ký tự), các loại còn lại cần <see cref="Slug"/> của di sản đã xuất bản.</summary>
public sealed record RecordGuestActivityRequest(GuestActivityType? Type, string? Slug, string? Query);

/// <summary>
/// Kết quả ghi một lượt. <see cref="Recorded"/> false khi lượt không được ghi: lặp lại trong 10 phút (<c>DUPLICATE</c>) hoặc yêu cầu của học sinh đã đăng nhập,
/// học sinh không bị ghi lịch sử (<c>STUDENT_SESSION</c>).
/// </summary>
public sealed record GuestActivityResultDto(bool Recorded, string? Reason);

/// <summary>Một mục của lịch sử (mới nhất trước). Mục tìm kiếm có <see cref="Query"/>; mục khác có <see cref="Heritage"/> theo ngôn ngữ yêu cầu.</summary>
public sealed record GuestHistoryItemDto(
    long Id,
    GuestActivityType Type,
    string TypeLabel,
    string? Query,
    HeritageCardDto? Heritage,
    string Language,
    DateTime CreatedAt);

/// <summary>Kết quả xóa lịch sử: số mục đã xóa thật trên máy chủ.</summary>
public sealed record GuestDeletedDto(int Deleted);

/// <summary>
/// Một di sản được gợi ý. <see cref="Reasons"/> là các điểm chung với di sản khách quan tâm (cùng loại, cùng tỉnh, cùng vùng, cùng cấp xếp hạng, cùng thời kỳ, chữ giống nhau)
/// để giao diện tự ghép câu theo ngôn ngữ; <see cref="BecauseOfHeritageId"/> và <see cref="BecauseOfName"/> là di sản giống nhất (tên theo ngôn ngữ yêu cầu).
/// Trống khi là di sản nổi bật thay thế.
/// </summary>
public sealed record GuestRecommendationDto(
    HeritageCardDto Heritage,
    IReadOnlyList<SimilarityFactor> Reasons,
    long? BecauseOfHeritageId,
    string? BecauseOfName);

/// <summary>
/// "Gợi ý cho bạn": tối đa 6 di sản. <see cref="IsPersonalized"/> false khi khách chưa có lịch sử (hoặc vừa xóa hết) hoặc chưa tìm được di sản giống: hiện di sản nổi bật như trang chủ.
/// <see cref="UsedFallback"/> true khi Dịch vụ AI lỗi và máy chủ tự chọn di sản cùng loại hoặc cùng vùng với di sản xem gần nhất.
/// <see cref="Message"/> là lời mời xem thử vài di sản khi khách chưa có lịch sử.
/// </summary>
public sealed record GuestRecommendationsDto(
    IReadOnlyList<GuestRecommendationDto> Items,
    bool IsPersonalized,
    bool UsedFallback,
    string? Message);

/// <summary>Tọa độ khách gửi lên chỉ để tính khoảng cách (vĩ độ, kinh độ); máy chủ làm tròn 3 chữ số thập phân và không lưu.</summary>
public sealed record NearbyRequest(double? Latitude, double? Longitude);

/// <summary>Một di sản gần khách: thẻ di sản theo ngôn ngữ yêu cầu, khoảng cách tính bằng mét và chữ khoảng cách đã định dạng ("350 m", "1,2 km").</summary>
public sealed record NearbyHeritageDto(HeritageCardDto Heritage, int DistanceMeters, string DistanceText);

/// <summary>
/// "Gần bạn": tối đa 6 di sản đã xuất bản có tọa độ, gần nhất trước. <see cref="UnavailableReason"/> là <c>OUTSIDE_VIETNAM</c> khi vị trí ngoài khung tọa độ Việt Nam
/// (nới 1 độ mỗi phía): danh sách rỗng và <see cref="Message"/> là lời báo.
/// </summary>
public sealed record NearbyDto(IReadOnlyList<NearbyHeritageDto> Items, string? UnavailableReason, string? Message);

/// <summary>Lý do "Gần bạn" không dùng được do máy chủ xác định (lý do "chưa cho phép dùng vị trí" nằm ở thiết bị).</summary>
public static class NearbyUnavailable
{
    public const string OutsideVietnam = "OUTSIDE_VIETNAM";
}
