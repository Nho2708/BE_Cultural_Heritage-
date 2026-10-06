namespace DSVHVN.Api.Common;

/// <summary>
/// Tiêu đề của khách tham quan. <c>X-Device-Key</c>: mã thiết bị ẩn danh (UUID phiên bản 4) do app hoặc trình duyệt sinh; gửi kèm yêu cầu công khai thì máy chủ ghi lịch sử
/// (tìm, xem, đọc tóm tắt, xem 3D) của thiết bị đó, trừ khi yêu cầu mang phiên đăng nhập học sinh. <c>X-Client-Platform</c>: ANDROID, IOS hoặc WEB (mặc định WEB).
/// </summary>
public static class GuestHeaders
{
    public const string DeviceKey = "X-Device-Key";
    public const string ClientPlatform = "X-Client-Platform";

    /// <summary>
    /// <c>X-Ads-Unavailable: true</c>: app khai báo thiết bị không có quảng cáo (bản demo iOS bằng Expo Go). Máy chủ chỉ chấp nhận khi cấu hình <c>Ads:AllowNoAdsDeclaration</c> bật
    /// (môi trường phát triển và demo); môi trường thật bỏ qua nên khách không tự tắt được cổng quảng cáo.
    /// </summary>
    public const string AdsUnavailable = "X-Ads-Unavailable";
}
