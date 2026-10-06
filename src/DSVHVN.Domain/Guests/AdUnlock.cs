using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;

namespace DSVHVN.Domain.Guests;

/// <summary>
/// Bảng <c>ad_unlocks</c> (CSDL v4, nhóm Khách tham quan và quảng cáo): một lượt mở khóa tóm tắt và nghe thuyết minh của MỘT di sản trên MỘT thiết bị
/// bằng quảng cáo có thưởng thật của Google, có hạn (24 giờ). Không có bảng chiến dịch và không lưu doanh thu: quảng cáo do Google phân phối, doanh thu xem ở
/// bảng điều khiển AdMob hoặc Ad Manager. Chỉ ghi khi Google xác nhận đã trao thưởng; phần thưởng không hợp lệ không tạo dòng mà ghi nhật ký thao tác.
/// Không lưu địa chỉ IP, mã quảng cáo của người xem hay nội dung quảng cáo.
/// </summary>
public class AdUnlock
{
    public long Id { get; set; }

    /// <summary>Thiết bị của khách đã xem hết quảng cáo.</summary>
    public long GuestDeviceId { get; set; }
    public GuestDevice? GuestDevice { get; set; }

    /// <summary>Di sản được mở tóm tắt và nghe thuyết minh.</summary>
    public long HeritageId { get; set; }
    public Heritage? Heritage { get; set; }

    /// <summary>ANDROID, IOS (AdMob) hoặc WEB (Ad Manager).</summary>
    public ClientPlatform Platform { get; set; }

    /// <summary>ADMOB hoặc AD_MANAGER, khớp nền tảng; cũng cho biết nguồn báo (AdMob: máy chủ xác minh, Ad Manager: trình duyệt báo).</summary>
    public AdNetwork AdNetwork { get; set; }

    /// <summary>
    /// Mã giao dịch phần thưởng, không trùng trong một mạng quảng cáo: một giao dịch chỉ mở khóa một lần. AdMob: <c>transaction_id</c> Google gửi;
    /// Ad Manager: mã một lần có chữ ký do máy chủ cấp khi khách bắt đầu xem.
    /// </summary>
    public string TransactionId { get; set; } = string.Empty;

    /// <summary>Mã đơn vị quảng cáo đã phát (đơn vị thử của Google khi phát triển); có thể trống.</summary>
    public string? AdUnitId { get; set; }

    /// <summary>Thời điểm mở khóa (UTC).</summary>
    public DateTime UnlockedAt { get; set; }

    /// <summary>Hạn mở khóa (UTC) = thời điểm mở khóa cộng 24 giờ.</summary>
    public DateTime ExpiresAt { get; set; }
}
