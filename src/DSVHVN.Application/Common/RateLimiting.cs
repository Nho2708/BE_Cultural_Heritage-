namespace DSVHVN.Application.Common;

/// <summary>
/// Bộ đếm tần suất theo khóa, đếm trong bộ nhớ máy chủ (khởi động lại thì về 0, không có bảng lưu). Mỗi khóa có một cửa sổ trượt riêng.
/// Dùng cho các giới hạn theo người dùng, theo mã thiết bị và theo địa chỉ IP mà chính sách giới hạn theo IP của lớp Api không đếm được.
/// </summary>
public interface IRateCounter
{
    /// <summary>
    /// Xin một lượt cho <paramref name="key"/>. Trong <paramref name="window"/> chưa đủ <paramref name="limit"/> lượt thì ghi nhận và trả null;
    /// đã đủ thì không ghi nhận và trả thời gian phải chờ (ít nhất 1 giây) cho tới khi lượt cũ nhất rời cửa sổ.
    /// </summary>
    TimeSpan? TryHit(string key, int limit, TimeSpan window);
}

/// <summary>
/// Ngưỡng giới hạn tần suất, bind từ mục <c>RateLimiting</c> của cấu hình để chỉnh được mà không sửa mã. Hai ngưỡng đầu thuộc chính sách giới hạn theo IP của lớp Api
/// (<c>PublicRateLimit</c>); các ngưỡng còn lại đếm bằng <see cref="IRateCounter"/>, cửa sổ luôn là một phút.
/// </summary>
public sealed class RateLimitOptions
{
    public const string Section = "RateLimiting";

    /// <summary>API công khai: số yêu cầu mỗi phút mỗi địa chỉ IP.</summary>
    public int PublicRequestsPerMinute { get; set; } = 300;

    /// <summary>AI soạn bài học, AI sinh câu hỏi: số yêu cầu mỗi phút mỗi giáo viên.</summary>
    public int TeacherAiRequestsPerMinute { get; set; } = 10;

    /// <summary>Ghi lịch sử xem của khách: số sự kiện mỗi phút mỗi mã thiết bị (ghi tay và ghi tự động khi xem).</summary>
    public int GuestHistoryEventsPerMinute { get; set; } = 120;

    /// <summary>Đọc tóm tắt di sản của khách: số yêu cầu mỗi phút mỗi mã thiết bị.</summary>
    public int GuestSummaryRequestsPerMinute { get; set; } = 60;

    /// <summary>Xác minh phần thưởng của AdMob: số yêu cầu sai chữ ký mỗi phút mỗi địa chỉ IP (yêu cầu đúng chữ ký không bị đếm hay chặn).</summary>
    public int AdMobBadSignaturesPerMinute { get; set; } = 300;

    /// <summary>AI dịch và tóm tắt di sản của quản trị hệ thống: số yêu cầu mỗi phút mỗi người.</summary>
    public int AdminTranslationAiRequestsPerMinute { get; set; } = 5;
}

public static class RateCounterExtensions
{
    /// <summary>Cửa sổ chung của các giới hạn tính theo phút.</summary>
    public static readonly TimeSpan PerMinute = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Xin một lượt trong cửa sổ một phút; vượt ngưỡng thì ném 429 theo khuôn phản hồi chung kèm <c>Retry-After</c> (giây).
    /// Ngưỡng nhỏ hơn 1 được nâng lên 1 để cấu hình sai không chặn hết mọi yêu cầu.
    /// </summary>
    public static void EnforcePerMinute(this IRateCounter counter, string key, int limit)
    {
        if (counter.TryHit(key, Math.Max(1, limit), PerMinute) is { } wait)
            throw AppException.TooManyRequests(Messages.TooManyRequests((int)Math.Ceiling(wait.TotalSeconds)), wait);
    }
}
