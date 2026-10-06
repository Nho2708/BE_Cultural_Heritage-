using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Guests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

// Nhóm Khách tham quan của CSDL v4: guest_devices và guest_activities. Khách không có tài khoản nên không có khóa ngoại tới users.

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
