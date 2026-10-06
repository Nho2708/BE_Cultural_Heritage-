using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;

namespace DSVHVN.Domain.Guests;

/// <summary>
/// Bảng <c>guest_activities</c> (CSDL v4): một lượt tìm, xem, đọc tóm tắt, nghe hoặc xem 3D của khách. Đầu vào của gợi ý cho khách và của thống kê tổng hợp.
/// Xóa hẳn (không xóa mềm): khách xóa từng mục hoặc xóa hết, tác vụ nền xóa lượt quá 90 ngày, mỗi thiết bị giữ tối đa 500 lượt. Không có vị trí, không có địa chỉ IP.
/// </summary>
public class GuestActivity
{
    public long Id { get; set; }

    public long GuestDeviceId { get; set; }
    public GuestDevice? GuestDevice { get; set; }

    public GuestActivityType ActivityType { get; set; }

    /// <summary>Di sản được xem, đọc tóm tắt, nghe, xem 3D; null với tìm kiếm.</summary>
    public long? HeritageId { get; set; }
    public Heritage? Heritage { get; set; }

    /// <summary>Từ khóa tìm (tối đa 100 ký tự), bắt buộc với tìm kiếm; null với loại khác.</summary>
    public string? SearchQuery { get; set; }

    /// <summary>Ngôn ngữ đang dùng lúc thực hiện.</summary>
    public LanguageCode LanguageCode { get; set; } = LanguageCode.vi;

    public DateTime CreatedAt { get; set; }
}
