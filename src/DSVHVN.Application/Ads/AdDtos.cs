using DSVHVN.Domain.Enums;

namespace DSVHVN.Application.Ads;

// Dữ liệu vào và ra của quảng cáo Google có thưởng. Enum viết HOA như CSDL.

/// <summary>
/// Người xem một yêu cầu công khai: học sinh đã đăng nhập (máy chủ quyết định theo phiên đăng nhập, không theo khai báo của máy khách), mã thiết bị ở <c>X-Device-Key</c>
/// và khai báo "thiết bị không có quảng cáo" ở <c>X-Ads-Unavailable</c>.
/// </summary>
public sealed record PublicViewer(bool IsStudent, string? DeviceKey, bool DeclaresNoAds);

/// <summary>
/// Trạng thái mở khóa của một di sản trên thiết bị của yêu cầu. <see cref="GateRequired"/> false khi cổng không áp dụng (<see cref="OpenReason"/>: <c>STUDENT</c>, <c>GATE_DISABLED</c>
/// hoặc <c>NO_ADS_DEVICE</c>); ngược lại <see cref="Unlocked"/> cho biết còn hạn mở khóa không, <see cref="ExpiresAt"/> là hạn (UTC, theo giờ máy chủ).
/// </summary>
public sealed record AdUnlockStatusDto(long HeritageId, string Slug, bool GateRequired, bool Unlocked, DateTime? ExpiresAt, string? OpenReason);

/// <summary>
/// Cấu hình công khai cho giao diện: cổng đang hoạt động không, thời hạn mở khóa, mức xếp hạng nội dung tối đa của yêu cầu quảng cáo (<c>G</c>), đơn vị quảng cáo đầu tiên của từng nền tảng
/// và việc có chấp nhận khai báo "thiết bị không có quảng cáo" không.
/// </summary>
public sealed record AdConfigDto(
    bool GateActive,
    int UnlockHours,
    string MaxContentRating,
    string? AndroidAdUnitId,
    string? IosAdUnitId,
    string? WebAdUnitPath,
    bool AllowNoAdsDeclaration);

/// <summary>Bắt đầu xem quảng cáo trên web: di sản cần mở khóa.</summary>
public sealed record StartWebRewardRequest(string? Slug);

/// <summary>
/// Mã giao dịch một lần cho lượt xem quảng cáo trên web (hết hạn sau 10 phút) kèm đơn vị quảng cáo để tải. Di sản đang còn mở khóa (hoặc cổng không áp dụng) thì
/// <see cref="AlreadyUnlocked"/> true và không cấp mã.
/// </summary>
public sealed record WebRewardStartDto(bool AlreadyUnlocked, string? TransactionId, DateTime? TransactionExpiresAt, string? AdUnitPath, AdUnlockStatusDto Status);

/// <summary>Trình duyệt báo đã nhận thưởng: di sản, mã giao dịch đã được cấp lúc bắt đầu và đơn vị quảng cáo đã phát.</summary>
public sealed record WebRewardReportRequest(string? Slug, string? TransactionId, string? AdUnit);

/// <summary>
/// Kết quả ghi một lượt phần thưởng. <see cref="Recorded"/> true khi có dòng mở khóa mới; false kèm <see cref="Reason"/>: <c>DUPLICATE</c> (mã giao dịch đã ghi, gửi lại) hoặc
/// <c>ALREADY_UNLOCKED</c> (di sản đang còn mở khóa nên không kéo dài hạn). Cả hai đều là thành công, không phải bị từ chối.
/// </summary>
public sealed record AdRewardResultDto(bool Recorded, string? Reason, DateTime? ExpiresAt);

// ---- Thống kê cho quản trị hệ thống ----

public sealed record AdDayCountDto(DateOnly Date, int Count);

public sealed record AdPlatformCountDto(ClientPlatform Platform, int Count);

public sealed record AdNetworkCountDto(AdNetwork Network, string Source, string SourceLabel, int Count);

public sealed record AdHeritageCountDto(long HeritageId, string? Slug, string? Name, int Count);

public sealed record AdRejectionCountDto(string Reason, string Label, int Count);

public sealed record AdLanguageCountDto(string Language, int Devices);

public sealed record AdUnlockStatsDto(
    int Total,
    IReadOnlyList<AdDayCountDto> ByDay,
    IReadOnlyList<AdPlatformCountDto> ByPlatform,
    IReadOnlyList<AdNetworkCountDto> ByNetwork,
    IReadOnlyList<AdHeritageCountDto> TopHeritages);

public sealed record AdRejectionStatsDto(int Total, IReadOnlyList<AdRejectionCountDto> ByReason);

/// <summary>Số thiết bị khách hoạt động, ngôn ngữ dùng nhiều và di sản được xem, nghe, xem 3D nhiều (từ lịch sử của khách, tổng hợp, không tra cứu theo thiết bị).</summary>
public sealed record AdGuestStatsDto(
    int ActiveDevices,
    IReadOnlyList<AdLanguageCountDto> ByLanguage,
    IReadOnlyList<AdHeritageCountDto> MostViewed,
    IReadOnlyList<AdHeritageCountDto> MostListened,
    IReadOnlyList<AdHeritageCountDto> MostViewed3D);

/// <summary>
/// Thống kê lượt mở khóa bằng quảng cáo và khách trong khoảng ngày Việt Nam [<see cref="From"/>, <see cref="To"/>]. Không có doanh thu: doanh thu quảng cáo xem ở bảng điều khiển
/// AdMob và Ad Manager của Google (<see cref="Note"/> và hai liên kết).
/// </summary>
public sealed record AdStatsDto(
    DateOnly From,
    DateOnly To,
    AdUnlockStatsDto Unlocks,
    AdRejectionStatsDto Rejections,
    AdGuestStatsDto Guests,
    string Note,
    string AdMobConsoleUrl,
    string AdManagerConsoleUrl);
