using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

/// <summary>
/// Bảng <c>audit_logs</c>. <c>actor_role</c>, <c>action</c> lưu dạng chuỗi KHÔNG có CHECK:
/// thêm mã hành động mới ở luồng sau không cần sửa CSDL; danh sách hợp lệ nằm ở enum trong code.
/// </summary>
internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("audit_logs");
        b.Property(x => x.ActorRole).HasConversion<string>().HasMaxLength(20).IsUnicode().IsRequired();
        b.Property(x => x.Action).HasConversion<string>().HasMaxLength(50).IsUnicode().IsRequired();
        b.Property(x => x.TargetType).HasMaxLength(50).IsRequired();
        b.Property(x => x.OldValue);
        b.Property(x => x.NewValue);
        b.Property(x => x.IpAddress).HasMaxLength(45);

        // Màn hình Nhật ký kiểm toán lọc và sắp theo thời gian; bảng chỉ lớn lên.
        b.HasIndex(x => x.CreatedAt).HasDatabaseName("IX_audit_logs_created_at");

        // Không xóa dây chuyền: nhật ký giữ tối thiểu 12 tháng.
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
