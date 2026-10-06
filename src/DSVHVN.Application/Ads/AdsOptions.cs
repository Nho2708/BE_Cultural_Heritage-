using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Ads;

/// <summary>
/// Cấu hình mục <c>Ads</c> của quảng cáo Google có thưởng. Mã đơn vị quảng cáo nằm ở đây (cấu hình môi trường, không viết cứng trong mã): khi phát triển, kiểm thử và demo
/// chỉ dùng đơn vị quảng cáo thử do Google công bố; chỉ đổi sang mã thật khi nhóm có tài khoản AdMob và Ad Manager đã được Google duyệt, và mã thật không commit vào kho mã.
/// </summary>
public sealed class AdsOptions
{
    public const string Section = "Ads";

    /// <summary>Danh sách khóa công khai của Google để kiểm chữ ký xác minh phía máy chủ của AdMob.</summary>
    public const string GoogleVerifierKeysUrl = "https://www.gstatic.com/admob/reward/verifier-keys.json";

    /// <summary>
    /// Bật cổng quảng cáo. Tắt (mặc định) thì tóm tắt và nghe thuyết minh mở thẳng cho mọi khách. Cổng chỉ hoạt động khi bật và đã cấu hình ít nhất một đơn vị quảng cáo
    /// (môi trường chưa có đơn vị nào thì cổng tự tắt).
    /// </summary>
    public bool GateEnabled { get; set; }

    /// <summary>
    /// Cho phép app khai báo "thiết bị này không có quảng cáo" bằng tiêu đề <c>X-Ads-Unavailable: true</c> (bản demo iOS bằng Expo Go, không có mô-đun quảng cáo).
    /// Chỉ bật ở môi trường phát triển và demo; môi trường thật để tắt (mặc định) thì khai báo bị bỏ qua và khách không tự tắt được cổng.
    /// </summary>
    public bool AllowNoAdsDeclaration { get; set; }

    public AdMobOptions AdMob { get; set; } = new();

    public AdManagerOptions AdManager { get; set; } = new();

    /// <summary>Có ít nhất một đơn vị quảng cáo (AdMob hoặc Ad Manager) đã cấu hình.</summary>
    public bool HasAnyAdUnit =>
        AdMob.AndroidAdUnitIds.Any(u => AdRules.NormalizeAdMobUnit(u) is not null)
        || AdMob.IosAdUnitIds.Any(u => AdRules.NormalizeAdMobUnit(u) is not null)
        || AdManager.AdUnitPaths.Any(p => !string.IsNullOrWhiteSpace(p));

    /// <summary>Cổng quảng cáo đang hoạt động: đã bật và có đơn vị quảng cáo.</summary>
    public bool GateActive => GateEnabled && HasAnyAdUnit;

    /// <summary>Nền tảng của một đơn vị quảng cáo AdMob (so theo phần số sau dấu gạch chéo); không thuộc cấu hình thì null.</summary>
    public ClientPlatform? PlatformOfAdMobUnit(string? unit)
    {
        var key = AdRules.NormalizeAdMobUnit(unit);
        if (key is null) return null;
        if (AdMob.AndroidAdUnitIds.Any(u => AdRules.NormalizeAdMobUnit(u) == key)) return ClientPlatform.ANDROID;
        if (AdMob.IosAdUnitIds.Any(u => AdRules.NormalizeAdMobUnit(u) == key)) return ClientPlatform.IOS;
        return null;
    }

    /// <summary>Đường dẫn đơn vị Ad Manager có trong cấu hình (so chính xác, bỏ khoảng trắng đầu cuối).</summary>
    public bool IsAdManagerUnit(string? path) =>
        !string.IsNullOrWhiteSpace(path) && AdManager.AdUnitPaths.Any(p => string.Equals(p?.Trim(), path.Trim(), StringComparison.Ordinal));
}

public sealed class AdMobOptions
{
    /// <summary>Đơn vị quảng cáo có thưởng của app Android (<c>ca-app-pub-xxxxxxxx/1234567890</c> hoặc chỉ phần số sau dấu gạch chéo).</summary>
    public string[] AndroidAdUnitIds { get; set; } = [];

    public string[] IosAdUnitIds { get; set; } = [];

    public string VerifierKeysUrl { get; set; } = AdsOptions.GoogleVerifierKeysUrl;

    /// <summary>Danh sách khóa của Google được làm mới sau số giờ này (mặc định 24).</summary>
    public int KeyCacheHours { get; set; } = 24;

    /// <summary>Gặp mã khóa lạ thì tải lại danh sách ngay, nhưng cách lần tải trước ít nhất số giây này (mặc định 60) để lời gọi giả mạo không bắt máy chủ tải liên tục.</summary>
    public int KeyMinRefreshSeconds { get; set; } = 60;
}

public sealed class AdManagerOptions
{
    /// <summary>Đường dẫn đơn vị quảng cáo có thưởng cho web của Ad Manager (<c>/22639388115/rewarded_web_example</c> là đơn vị thử của Google).</summary>
    public string[] AdUnitPaths { get; set; } = [];
}
