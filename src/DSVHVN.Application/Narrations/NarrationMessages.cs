using DSVHVN.Application.Common;

namespace DSVHVN.Application.Narrations;

/// <summary>Thông điệp của thuyết minh tạo sẵn. Chỉ dành cho công cụ tạo giọng và quản trị hệ thống (tiếng Việt, xưng "bạn"); khách không thấy câu nào ở đây.</summary>
public static class NarrationMessages
{
    /// <summary>Băm gửi lên khác băm của bản tóm tắt ngắn hiện tại: chữ đã đổi sau khi tạo giọng, cần lấy lại danh sách và tạo lại.</summary>
    public static readonly AppMessage SummaryChanged = new(MessageCodes.NarrationStale,
        "Bản tóm tắt đã đổi sau khi tạo giọng. Hãy lấy lại danh sách còn thiếu và tạo lại thuyết minh cho bản mới.");

    /// <summary>Hai lần tải lên cùng một (di sản, ngôn ngữ) chạm nhau ở ràng buộc không trùng.</summary>
    public static readonly AppMessage Concurrent = new(MessageCodes.Blocked,
        "Thuyết minh này vừa được một lần tải lên khác ghi. Hãy lấy lại danh sách còn thiếu rồi thử lại nếu vẫn cần.");

    public static AppMessage Saved(string language) => Messages.Success($"Lưu thuyết minh {language}");
}
