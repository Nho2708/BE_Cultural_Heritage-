using DSVHVN.Domain.Classes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

// Nhóm Lớp / Học sinh của CSDL v3.

internal sealed class SchoolClassConfiguration : IEntityTypeConfiguration<SchoolClass>
{
    public void Configure(EntityTypeBuilder<SchoolClass> b)
    {
        b.ToTable("classes", t => t.HasCheckConstraint("CK_classes_grade",
            $"[grade] BETWEEN {SchoolClass.MinGrade} AND {SchoolClass.MaxGrade}"));
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Grade).IsRequired();
        b.Property(x => x.SchoolYear).HasMaxLength(20);

        b.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StudentConfiguration : IEntityTypeConfiguration<Student>
{
    public void Configure(EntityTypeBuilder<Student> b)
    {
        b.ToTable("students");
        b.Property(x => x.StudentCode).HasMaxLength(20);
        b.Property(x => x.FullName).HasMaxLength(255);
        b.Property(x => x.IsActive).BitWithDefault(true);

        // Mỗi tài khoản đúng một học sinh (quan hệ một-một nhờ không trùng); mã học sinh không trùng toàn hệ thống,
        // không lọc deleted_at nên mã không bao giờ được cấp lại.
        b.HasIndex(x => x.UserId).IsPlainUnique("UX_students_user_id");
        b.HasIndex(x => x.StudentCode).IsPlainUnique("UX_students_student_code");

        b.HasOne(x => x.Class).WithMany().HasForeignKey(x => x.ClassId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}
