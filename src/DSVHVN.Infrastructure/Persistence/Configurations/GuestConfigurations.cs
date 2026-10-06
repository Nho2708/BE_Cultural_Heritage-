using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Guests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

// Nhóm Khách tham quan và quảng cáo của CSDL v4: guest_devices, guest_activities và ad_unlocks. Khách không có tài khoản nên không có khóa ngoại tới users.

/// <summary>Bảng <c>guest_devices</c>: mã thiết bị ẩn danh (không trùng), nền tảng, ngôn ngữ dùng gần nhất, cờ "Gần bạn", mốc đầu và cuối thấy.</summary>
internal sealed class GuestDeviceConfiguration : IEntityTypeConfiguration<GuestDevice>
{
    public void Configure(EntityTypeBuilder<GuestDevice> b)
    {
        b.ToTable("guest_devices", t =>
        {
            t.HasEnumCheck<GuestDevice, ClientPlatform>("guest_devices", "platform");
            t.HasEnumCheck<GuestDevice, LanguageCode>("guest_devices", "language_code");
        });
        b.Property(x => x.DeviceKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.Platform).AsEnumText();
        b.Property(x => x.LanguageCode).AsEnumText().HasDefaultValue(LanguageCode.vi);
        b.Property(x => x.LocationAllowed).BitWithDefault(false);
        b.Property(x => x.CreatedAt).IsRequired();

        b.HasIndex(x => x.DeviceKey).IsPlainUnique("UX_guest_devices_device_key");
    }
}

/// <summary>
/// Bảng <c>guest_activities</c>: lịch sử tìm, xem, đọc tóm tắt, nghe, xem 3D của một thiết bị. CHECK <c>CK_guest_activities_target</c>: tìm kiếm cần từ khóa, các loại khác cần di sản.
/// Chỉ mục theo thiết bị và thời gian cho màn Lịch sử, cho gợi ý và cho việc xóa lượt quá hạn.
/// </summary>
internal sealed class GuestActivityConfiguration : IEntityTypeConfiguration<GuestActivity>
{
    public void Configure(EntityTypeBuilder<GuestActivity> b)
    {
        b.ToTable("guest_activities", t =>
        {
            t.HasEnumCheck<GuestActivity, GuestActivityType>("guest_activities", "activity_type");
            t.HasEnumCheck<GuestActivity, LanguageCode>("guest_activities", "language_code");
            t.HasCheckConstraint("CK_guest_activities_target",
                "([activity_type] = 'SEARCH' AND [search_query] IS NOT NULL) OR ([activity_type] <> 'SEARCH' AND [heritage_id] IS NOT NULL)");
        });
        b.Property(x => x.ActivityType).AsEnumText();
        b.Property(x => x.SearchQuery).HasMaxLength(255); // nvarchar(255) như CSDL v4; mã chỉ nhận từ khóa đến 100 ký tự
        b.Property(x => x.LanguageCode).AsEnumText();
        b.Property(x => x.CreatedAt).IsRequired();

        b.HasIndex(x => new { x.GuestDeviceId, x.CreatedAt }).HasDatabaseName("IX_guest_activities_device_time");

        b.HasOne(x => x.GuestDevice).WithMany().HasForeignKey(x => x.GuestDeviceId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Heritage).WithMany().HasForeignKey(x => x.HeritageId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// Bảng <c>ad_unlocks</c>: lượt mở khóa tóm tắt và nghe thuyết minh của một di sản trên một thiết bị bằng quảng cáo có thưởng của Google. Ba ràng buộc của CSDL:
/// <c>UX_ad_unlocks_network_transaction</c> (một mã giao dịch chỉ mở khóa một lần trong một mạng), <c>CK_ad_unlocks_network_platform</c> (AdMob chỉ ở Android hoặc iOS,
/// Ad Manager chỉ ở web) và <c>CK_ad_unlocks_expiry</c> (hạn sau thời điểm mở khóa). Chỉ mục theo thiết bị, di sản và hạn cho việc kiểm còn hạn và đếm lượt trong giờ.
/// </summary>
internal sealed class AdUnlockConfiguration : IEntityTypeConfiguration<AdUnlock>
{
    public void Configure(EntityTypeBuilder<AdUnlock> b)
    {
        b.ToTable("ad_unlocks", t =>
        {
            t.HasEnumCheck<AdUnlock, ClientPlatform>("ad_unlocks", "platform");
            t.HasEnumCheck<AdUnlock, AdNetwork>("ad_unlocks", "ad_network");
            t.HasCheckConstraint("CK_ad_unlocks_network_platform",
                "([ad_network] = 'ADMOB' AND [platform] IN ('ANDROID', 'IOS')) OR ([ad_network] = 'AD_MANAGER' AND [platform] = 'WEB')");
            t.HasCheckConstraint("CK_ad_unlocks_expiry", "[expires_at] > [unlocked_at]");
        });
        b.Property(x => x.Platform).AsEnumText();
        b.Property(x => x.AdNetwork).AsEnumText();
        b.Property(x => x.TransactionId).HasMaxLength(255).IsRequired();
        b.Property(x => x.AdUnitId).HasMaxLength(100);
        b.Property(x => x.UnlockedAt).IsRequired();
        b.Property(x => x.ExpiresAt).IsRequired();

        b.HasIndex(x => new { x.AdNetwork, x.TransactionId }).IsPlainUnique("UX_ad_unlocks_network_transaction");
        b.HasIndex(x => new { x.GuestDeviceId, x.HeritageId, x.ExpiresAt }).HasDatabaseName("IX_ad_unlocks_device_heritage");

        b.HasOne(x => x.GuestDevice).WithMany().HasForeignKey(x => x.GuestDeviceId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Heritage).WithMany().HasForeignKey(x => x.HeritageId).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}
