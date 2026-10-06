using DSVHVN.Application.Common;

namespace DSVHVN.Application.Ads;

/// <summary>Thông điệp của cổng quảng cáo Google có thưởng (tiếng Việt; các ngôn ngữ khác hiện theo mã ở giao diện).</summary>
public static class AdMessages
{
    public const string UnlockRequiredCode = "AD_UNLOCK_REQUIRED";
    public const string RewardRejectedCode = "AD_REWARD_REJECTED";
    public const string RateLimitedCode = "AD_REWARD_RATE_LIMITED";
    public const string UnavailableCode = "AD_UNAVAILABLE";

    /// <summary>Khách bấm "Tóm tắt" hoặc "Nghe" ở di sản chưa mở khóa: giao diện hiện lời mời xem quảng cáo.</summary>
    public static AppMessage UnlockRequired(string heritageName) => new(UnlockRequiredCode,
        $"Xem một quảng cáo ngắn để mở tóm tắt và nghe thuyết minh của \"{heritageName}\" trong 24 giờ? Các nội dung khác vẫn miễn phí.");

    /// <summary>Phần thưởng không hợp lệ; không nói rõ lý do cho người gọi (lý do nằm ở nhật ký thao tác).</summary>
    public static readonly AppMessage RewardRejected = new(RewardRejectedCode,
        "Chưa mở được tóm tắt. Bạn hãy xem hết quảng cáo hoặc thử lại sau ít phút.");

    /// <summary>Web vượt giới hạn mở khóa mỗi giờ theo thiết bị hoặc theo địa chỉ IP.</summary>
    public static AppMessage RateLimited(int minutes) => new(RateLimitedCode,
        $"Bạn đã xem quá nhiều quảng cáo trong thời gian ngắn. Hãy thử lại sau {Math.Max(1, minutes)} phút.");

    /// <summary>Môi trường chưa cấu hình đơn vị quảng cáo cho nền tảng của yêu cầu.</summary>
    public static readonly AppMessage Unavailable = new(UnavailableCode,
        "Hiện chưa có quảng cáo để hiện. Bạn hãy thử lại sau ít phút.");
}
