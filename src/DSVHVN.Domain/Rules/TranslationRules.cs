namespace DSVHVN.Domain.Rules;

/// <summary>Giới hạn độ dài của bản dịch và tóm tắt di sản (theo độ rộng cột của CSDL v4) và các hằng của quy trình duyệt.</summary>
public static class TranslationRules
{
    public const int NameMaxLength = 255;
    public const int ShortDescriptionMaxLength = 1000;
    public const int AddressMaxLength = 500;
    public const int CaptionMaxLength = 500;

    /// <summary>Bản tóm tắt ngắn để nghe, khoảng 80-120 từ tiếng Việt; cột nvarchar(2000).</summary>
    public const int SummaryShortMaxLength = 2000;

    /// <summary>Lý do từ chối (không bắt buộc), ghi vào nhật ký thao tác.</summary>
    public const int RejectReasonMaxLength = 500;

    /// <summary>Mục tiêu độ dài tóm tắt tiếng Việt (số từ): bản ngắn đọc to trong khoảng một phút, bản chi tiết để đọc.</summary>
    public const int ShortSummaryMinWords = 80;
    public const int ShortSummaryMaxWords = 120;
    public const int DetailedSummaryMinWords = 250;
    public const int DetailedSummaryMaxWords = 400;

    /// <summary>Một di sản chỉ có một yêu cầu dịch đang xử lý; dòng nhật ký treo quá thời gian này coi như đã chết và không chặn yêu cầu mới.</summary>
    public static readonly TimeSpan StaleProcessing = TimeSpan.FromMinutes(10);

    /// <summary>Chờ tối đa 90 giây mỗi lần gọi (nội dung dài), thử lại tối đa 2 lần.</summary>
    public static readonly TimeSpan AiAttemptTimeout = TimeSpan.FromSeconds(90);
}
