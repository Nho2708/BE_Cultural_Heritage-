using DSVHVN.Domain.Common;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Guests;

/// <summary>
/// Bảng <c>guest_devices</c> (CSDL v4, nhóm Khách tham quan): khách không có tài khoản, nhận diện bằng mã thiết bị ẩn danh (UUID phiên bản 4 do app hoặc trình duyệt sinh).
/// Chỉ lưu mã, nền tảng, ngôn ngữ đang dùng, cờ đã bật "Gần bạn" và hai mốc thời gian; không lưu địa chỉ IP, vị trí hay bất cứ thông tin nào cho biết khách là ai,
/// và không bao giờ ghép với tài khoản học sinh. Dòng được tạo ở yêu cầu đầu tiên cần lưu của thiết bị.
/// </summary>
public class GuestDevice : IHasCreatedAt
{
    public long Id { get; set; }

    /// <summary>Mã thiết bị ẩn danh dạng chữ thường 36 ký tự; không trùng.</summary>
    public string DeviceKey { get; set; } = string.Empty;

    public ClientPlatform Platform { get; set; }

    /// <summary>Ngôn ngữ khách dùng gần nhất, chỉ để thống kê; máy chủ không dùng cột này để chọn ngôn ngữ hiển thị.</summary>
    public LanguageCode LanguageCode { get; set; } = LanguageCode.vi;

    /// <summary>Khách đã bật "Gần bạn" (nhớ lựa chọn và thống kê tỉ lệ bật); không dùng để tính khoảng cách, không chứa tọa độ.</summary>
    public bool LocationAllowed { get; set; }

    public DateTime? CreatedAt { get; set; }

    /// <summary>Lần gần nhất thiết bị gọi API có mang mã.</summary>
    public DateTime? LastSeenAt { get; set; }
}
