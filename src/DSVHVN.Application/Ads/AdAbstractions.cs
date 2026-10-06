namespace DSVHVN.Application.Ads;

/// <summary>
/// Khóa công khai của Google để kiểm chữ ký xác minh phía máy chủ của AdMob. Bản thật tải danh sách từ gstatic.com, làm mới sau 24 giờ hoặc khi gặp mã khóa lạ;
/// kiểm thử thay bằng khóa tự sinh.
/// </summary>
public interface IAdMobKeyStore
{
    /// <summary>Khóa công khai ở dạng SubjectPublicKeyInfo (DER) theo mã khóa; không có mã này thì null.</summary>
    Task<byte[]?> FindPublicKeyAsync(long keyId, CancellationToken ct);
}

/// <summary>Mã giao dịch một lần của web: gắn thiết bị và di sản, ký bằng khóa bí mật của máy chủ, hết hạn sau 10 phút, không lưu bảng nào.</summary>
public sealed record AdRewardTicket(string Code, string DeviceKey, long HeritageId, DateTime IssuedAt, DateTime ExpiresAt);

public interface IAdRewardTokenCodec
{
    AdRewardTicket Issue(string deviceKey, long heritageId);

    /// <summary>Đọc và kiểm mã: sai chữ ký, sai định dạng, hết hạn hoặc cấp ở tương lai thì null.</summary>
    AdRewardTicket? Read(string? code);
}

/// <summary>Kết quả xin một lượt báo thưởng trên web; <see cref="RetryAfter"/> là thời gian chờ khi bị từ chối.</summary>
public readonly record struct AdRateDecision(bool Allowed, TimeSpan RetryAfter);

/// <summary>
/// Giới hạn tần suất báo thưởng của web: 10 lượt mỗi giờ cho một mã thiết bị và 60 lượt mỗi giờ cho một địa chỉ IP. Bộ đếm nằm trong bộ nhớ máy chủ:
/// khởi động lại thì về 0; địa chỉ IP chỉ nằm ở đây, không ghi vào cơ sở dữ liệu hay nhật ký.
/// </summary>
public interface IAdRewardThrottle
{
    /// <summary>Xin một lượt: được thì ghi nhận và trả <c>Allowed</c>; vượt ngưỡng của thiết bị hoặc của IP thì từ chối và không ghi nhận.</summary>
    AdRateDecision TryAcquire(string deviceKey, string? ipAddress);

    /// <summary>True ở lần từ chối đầu tiên của thiết bị trong giờ hiện tại (nhật ký thao tác chỉ ghi tối đa một dòng mỗi thiết bị mỗi giờ để kẻ lạm dụng không làm tràn nhật ký).</summary>
    bool ShouldLogRejection(string deviceKey);
}
