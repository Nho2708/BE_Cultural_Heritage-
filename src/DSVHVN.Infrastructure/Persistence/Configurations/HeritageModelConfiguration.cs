using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

/// <summary>
/// Bảng <c>heritage_models</c> (CSDL v4, nhóm Đa ngôn ngữ và 3D): mô hình 3D của di sản. Chỉ mục duy nhất có lọc bảo đảm mỗi di sản có tối đa một mô hình
/// đang hiển thị (PUBLISHED, chưa gỡ); mô hình nháp, đã ẩn hay đã gỡ không chiếm chỗ.
/// </summary>
internal sealed class HeritageModelConfiguration : IEntityTypeConfiguration<HeritageModel>
{
    public void Configure(EntityTypeBuilder<HeritageModel> b)
    {
        b.ToTable("heritage_models", t =>
        {
            t.HasEnumCheck<HeritageModel, ModelFormat>("heritage_models", "file_format");
            t.HasEnumCheck<HeritageModel, ContentStatus>("heritage_models", "status");
            t.HasCheckConstraint("CK_heritage_models_file_size", "[file_size_bytes] > 0");
        });
        b.Property(x => x.Title).HasMaxLength(255);
        b.Property(x => x.FileUrl).HasMaxLength(500).IsRequired();
        b.Property(x => x.FileFormat).AsEnumText();
        b.Property(x => x.PosterUrl).HasMaxLength(500);
        b.Property(x => x.LicenseName).HasMaxLength(100).IsRequired();
        b.Property(x => x.LicenseUrl).HasMaxLength(500);
        b.Property(x => x.Author).HasMaxLength(255).IsRequired();
        b.Property(x => x.SourceUrl).HasMaxLength(500);
        b.Property(x => x.Credit).HasMaxLength(500).IsRequired();
        b.Property(x => x.Status).AsEnumText();
        b.Property(x => x.CreatedAt).IsRequired();

        b.HasIndex(x => new { x.HeritageId, x.Status }).HasDatabaseName("IX_heritage_models_heritage_status");
        b.HasIndex(x => x.HeritageId).IsUnique().HasDatabaseName("UX_heritage_models_visible")
            .HasFilter("[status] = 'PUBLISHED' AND [deleted_at] IS NULL");

        b.HasOne(x => x.Heritage).WithMany().HasForeignKey(x => x.HeritageId).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}
