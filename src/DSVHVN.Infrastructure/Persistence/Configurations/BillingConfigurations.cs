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

        b.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Provider).WithMany().HasForeignKey(x => x.ProviderId).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}
