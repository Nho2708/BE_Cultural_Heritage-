namespace DSVHVN.Infrastructure.Background;

/// <summary>
/// Mục <c>Guests</c> của cấu hình: chu kỳ (giờ) của tác vụ nền xóa thật lịch sử khách tham quan quá 90 ngày. Mặc định 24 giờ (chạy một lần lúc khởi động rồi hằng ngày).
/// Bằng 0 hoặc âm thì tắt tác vụ nền. Thời hạn 90 ngày là quy tắc cố định trong mã, không cấu hình.
/// </summary>
public sealed class GuestCleanupOptions
{
    public const string Section = "Guests";

    public int CleanupIntervalHours { get; set; } = 24;
}
