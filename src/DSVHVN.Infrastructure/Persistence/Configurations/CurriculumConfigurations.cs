using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Curriculum;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

// Chủ đề chương trình môn học (bản gọn). Dữ liệu 40 chủ đề và liên kết với di sản theo slug nạp sẵn bằng chính migration tạo bảng.

internal sealed class CurriculumTopicConfiguration : IEntityTypeConfiguration<CurriculumTopic>
{
    public void Configure(EntityTypeBuilder<CurriculumTopic> b)
    {
        b.ToTable("curriculum_topics", t => t.HasCheckConstraint("CK_curriculum_topics_grade",
            $"[grade] BETWEEN {SchoolClass.MinGrade} AND {SchoolClass.MaxGrade}"));
        b.Property(x => x.Subject).HasMaxLength(100).IsRequired();
        b.Property(x => x.Grade).IsRequired();
        b.Property(x => x.GroupTitle).HasMaxLength(255).IsRequired();
        b.Property(x => x.Title).HasMaxLength(255).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.LocalProvince).BitWithDefault(false);
        b.Property(x => x.SortOrder).IsRequired();
        b.Property(x => x.SourceUrl).HasMaxLength(500);
        b.Property(x => x.IsActive).BitWithDefault(true);

        // Mỗi (khối, môn, chủ đề lớn, nội dung) chỉ có một dòng.
        b.HasIndex(x => new { x.Grade, x.Subject, x.GroupTitle, x.Title }).IsPlainUnique("UX_curriculum_topics_grade_subject_group_title");
    }
}

internal sealed class HeritageCurriculumTopicConfiguration : IEntityTypeConfiguration<HeritageCurriculumTopic>
{
    public void Configure(EntityTypeBuilder<HeritageCurriculumTopic> b)
    {
        b.ToTable("heritage_curriculum_topics");
        b.HasKey(x => new { x.HeritageId, x.TopicId });

        b.HasOne(x => x.Heritage).WithMany().HasForeignKey(x => x.HeritageId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        // Xóa chủ đề (chỉ qua CSDL, ứng dụng không xóa cứng) thì xóa liên kết theo.
        b.HasOne(x => x.Topic).WithMany(t => t.Heritages).HasForeignKey(x => x.TopicId).IsRequired().OnDelete(DeleteBehavior.Cascade);
    }
}
