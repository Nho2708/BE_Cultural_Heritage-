using DSVHVN.Application.Common;

namespace DSVHVN.Application.Models;

/// <summary>Thông điệp của luồng Mô hình 3D di sản. Câu cho quản trị hệ thống chỉ có tiếng Việt, xưng "bạn"; câu cho khách có mã riêng để web học tập và app hiện đủ 5 ngôn ngữ từ tệp chuỗi.</summary>
public static class ModelMessages
{
    /// <summary>Di sản không có mô hình đang hiển thị, hoặc mô hình vừa bị ẩn, hoặc trình xem không tải được: quay về bộ ảnh của di sản.</summary>
    public static readonly AppMessage Unavailable = new(MessageCodes.ModelUnavailable,
        "Không tải được mô hình 3D. Bạn có thể xem bộ ảnh của di sản.");

    /// <summary>Câu cho trình xem hiện khi tải lỗi hoặc thiết bị không hỗ trợ.</summary>
    public static string ViewerFallback => Unavailable.Text;

    public static readonly AppMessage AnotherVisible = new(MessageCodes.Blocked,
        "Không thể hiện mô hình 3D. Di sản đã có một mô hình đang hiển thị, hãy ẩn mô hình đó trước.");

    public static AppMessage NotFound(string action) => Messages.NotFound(action, "mô hình 3D");
}
