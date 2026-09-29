using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

// Nhóm Thanh toán — chỉ 2 bảng tạo ở luồng nền tảng (plans, subscriptions, mức tối thiểu). invoices, payment_providers,
// payments thuộc luồng gói dịch vụ. Gói "Miễn phí" seed lúc khởi động theo môi trường, không nằm trong migration.

internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> b)
    {
        b.ToTable("plans");
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Price).HasPrecision(12, 2);
        // bit NOT NULL DEFAULT 1. Sentinel = true: EF bỏ cột khi giá trị là true (để CSDL tự điền 1) và vẫn gửi false.
        b.Property(x => x.IsActive).HasDefaultValue(true).HasSentinel(true);
    }
}

internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> b)
    {
        // CHECK đủ 4 giá trị, gồm PENDING, ngay trong migration đầu tiên.
        b.ToTable("subscriptions", t =>
            t.HasCheckConstraint("CK_subscriptions_status", EnumColumn.CheckSql<SubscriptionStatus>("status")));
        b.Property(x => x.Status).AsEnumText().IsRequired();

        b.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}
