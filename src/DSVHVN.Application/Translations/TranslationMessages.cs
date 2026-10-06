using DSVHVN.Application.Common;

namespace DSVHVN.Application.Translations;

/// <summary>
/// Thông điệp của luồng Đa ngôn ngữ. Câu cho khách ở phần công khai (chưa có tóm tắt) có mã riêng để web học tập và app tự hiện bản đủ 5 ngôn ngữ từ tệp chuỗi;
/// câu cho quản trị hệ thống chỉ có tiếng Việt, xưng "bạn".
/// </summary>
public static class TranslationMessages
{
    /// <summary>Khách bấm "Tóm tắt" hoặc "Nghe" ở di sản chưa có tóm tắt tiếng Việt đã duyệt: không hiện cổng quảng cáo, không tính lượt.</summary>
    public static readonly AppMessage NoSummary = new(MessageCodes.SummaryUnavailable,
        "Di sản này chưa có tóm tắt. Bạn vẫn có thể đọc nội dung đầy đủ bên dưới.");

    /// <summary>Dịch và tóm tắt xong: bản tiếng Việt, tiếng Anh chờ duyệt, bản Hàn, Trung, Nhật hiện ngay kèm nhãn "bản dịch máy".</summary>
    public static readonly AppMessage Generated = new(MessageCodes.TranslationGenerated,
        "Đã dịch và tóm tắt di sản. Bản tiếng Việt và tiếng Anh đang chờ duyệt; bản tiếng Hàn, Trung, Nhật hiện ngay kèm nhãn \"bản dịch máy\".");

    /// <summary>Duyệt xong.</summary>
    public static AppMessage Approved(string what) => new(MessageCodes.TranslationApproved, $"Đã duyệt {what}. Nội dung này đã hiện cho khách.");

    /// <summary>Từ chối xong (yêu cầu dịch lại).</summary>
    public static AppMessage Rejected(string what) => new(MessageCodes.TranslationRejected,
        $"Đã từ chối {what}. Nội dung này không hiện cho khách; hãy bấm \"Dịch và tóm tắt bằng AI\" để dịch lại.");

    /// <summary>Dịch vụ AI lỗi sau các lần thử: không lưu gì, bản dịch và tóm tắt hiện có giữ nguyên, không tính lượt AI của trường.</summary>
    public static readonly AppMessage AiUnavailable = new(MessageCodes.AiUnavailable,
        "Dịch vụ AI đang bận. Bản dịch và tóm tắt hiện có được giữ nguyên và lần này không tính lượt AI của trường. Bạn có thể thử lại sau.");

    /// <summary>Di sản đã có một yêu cầu dịch đang chạy.</summary>
    public static readonly AppMessage Busy = new(MessageCodes.TranslationBusy,
        "Di sản này đang được dịch và tóm tắt. Vui lòng chờ lần chạy hiện tại xong rồi thử lại.");

    /// <summary>Số bản cần dịch lại sau khi sửa nội dung tiếng Việt: dải nhắc ở tab Bản dịch và tóm tắt.</summary>
    public static string Outdated(int count) =>
        $"Nội dung tiếng Việt đã được sửa sau khi dịch. {count} bản dịch và tóm tắt cần dịch lại; các bản cũ vẫn hiện cho khách cho tới khi bạn bấm dịch lại " +
        "(bản mới thay bản cũ; tiếng Anh và tóm tắt tiếng Việt chờ duyệt rồi mới hiện).";

    /// <summary>Duyệt bản tiếng Anh xong nhưng chưa dịch lại được bản Hàn, Trung, Nhật vì Dịch vụ AI lỗi.</summary>
    public const string MachineRefreshFailed =
        "Đã duyệt bản tiếng Anh nhưng chưa dịch lại được bản tiếng Hàn, Trung, Nhật vì Dịch vụ AI đang bận. Bản dịch máy cũ vẫn hiện; hãy bấm \"Dịch và tóm tắt bằng AI\" để thử lại.";
}
