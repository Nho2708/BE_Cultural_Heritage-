using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

// Nhóm Thanh toán của CSDL v3. Gói "Miễn phí" seed lúc khởi động theo môi trường, không nằm trong migration.

internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> b)
    {
        b.ToTable("plans");
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Price).HasPrecision(12, 2);
        b.Property(x => x.IsActive).BitWithDefault(true);

        // Tên gói không trùng; theo collation Vietnamese_CI_AI nên không phân biệt hoa thường và dấu. Cột được NULL nên chỉ xét dòng có tên.
        b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("UX_plans_name");
    }
}

internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> b)
    {
        // CHECK đủ 4 giá trị, gồm PENDING.
        b.ToTable("subscriptions", t => t.HasEnumCheck<Subscription, SubscriptionStatus>("subscriptions", "status"));
        b.Property(x => x.Status).AsEnumText();

        b.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId).IsRequired().OnDelete(DeleteBehavior.Restrict);

        // Mỗi trường có tối đa một yêu cầu mua chưa thanh toán.
        b.HasIndex(x => x.OrganizationId).IsUnique().HasFilter("[status] = 'PENDING'").HasDatabaseName("UX_subscriptions_one_pending");
        // Tra gói hiệu lực, gói chờ bắt đầu, quá hạn của từng trường.
        b.HasIndex(x => new { x.OrganizationId, x.Status }).HasDatabaseName("IX_subscriptions_organization_status");
    }
}

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> b)
    {
        b.ToTable("invoices", t => t.HasEnumCheck<Invoice, InvoiceStatus>("invoices", "status"));
        b.Property(x => x.InvoiceNumber).HasMaxLength(50);
        b.Property(x => x.Amount).HasPrecision(12, 2);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Status).AsEnumText();
        b.HasIndex(x => x.InvoiceNumber).IsPlainUnique("UX_invoices_invoice_number");
        // Tác vụ nền tìm hóa đơn chưa trả quá hạn.
        b.HasIndex(x => new { x.Status, x.DueAt }).HasDatabaseName("IX_invoices_status_due_at");

        b.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Subscription).WithMany().HasForeignKey(x => x.SubscriptionId).IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PaymentProviderConfiguration : IEntityTypeConfiguration<PaymentProvider>
{
    public void Configure(EntityTypeBuilder<PaymentProvider> b)
    {
        b.ToTable("payment_providers");
        b.Property(x => x.Code).HasMaxLength(50);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.IsActive).BitWithDefault(true);
        b.HasIndex(x => x.Code).IsPlainUnique("UX_payment_providers_code");
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("payments", t => t.HasEnumCheck<Payment, PaymentStatus>("payments", "status"));
        b.Property(x => x.ProviderTransactionId).HasMaxLength(255);
        b.Property(x => x.Amount).HasPrecision(12, 2);
        b.Property(x => x.Status).AsEnumText();
        b.Property(x => x.RawResponse);

        // UNIQUE có lọc: nhiều giao dịch PENDING chưa có mã cổng; có mã rồi thì chống ghi nhận trùng giao dịch.
        b.HasIndex(x => new { x.ProviderId, x.ProviderTransactionId }).IsUnique()
            .HasFilter("[provider_transaction_id] IS NOT NULL").HasDatabaseName("UX_payments_provider_txn");

        // Mỗi hóa đơn có tối đa một giao dịch chờ tại một thời điểm.
        b.HasIndex(x => x.InvoiceId).IsUnique().HasFilter("[status] = 'PENDING'").HasDatabaseName("UX_payments_one_pending_per_invoice");
        // Tác vụ nền tìm giao dịch chờ quá lâu.
        b.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("IX_payments_status_created_at");

        b.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Provider).WithMany().HasForeignKey(x => x.ProviderId).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}
