using DSVHVN.Application.Common;

namespace DSVHVN.Application.Guests;

/// <summary>Thông điệp của khách tham quan (tiếng Việt; các ngôn ngữ khác hiện theo mã ở giao diện).</summary>
public static class GuestMessages
{
    /// <summary>Học sinh đã đăng nhập gọi chức năng riêng của khách (gợi ý, lịch sử, "Gần bạn", cài đặt thiết bị).</summary>
    public static readonly AppMessage GuestOnly = new(MessageCodes.GuestOnly,
        "Chức năng này chỉ dành cho khách tham quan. Học sinh dùng gợi ý trong khu học.");

    /// <summary>Khách chưa có lịch sử hoặc vừa xóa hết: hiện cạnh mục di sản nổi bật thay cho gợi ý.</summary>
    public const string NoHistoryYet = "Hãy xem thử vài di sản, mục \"Gợi ý cho bạn\" sẽ xuất hiện ở đây.";

    /// <summary>"Gần bạn" không dùng được vì vị trí ngoài Việt Nam.</summary>
    public const string NearbyOutsideVietnam = "Chưa dùng được \"Gần bạn\": bạn đang ở ngoài Việt Nam.";

    public static AppMessage DeviceKeyRequired => Messages.Required("mã thiết bị");

    public static AppMessage DeviceKeyInvalid =>
        Messages.Invalid("Mã thiết bị", "phải là UUID phiên bản 4 dạng xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx");
}
