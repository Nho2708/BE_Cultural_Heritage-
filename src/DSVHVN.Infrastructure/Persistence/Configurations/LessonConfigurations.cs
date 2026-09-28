using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Lessons;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

// Nhóm Bài học + AI của CSDL v3.

internal sealed class LessonConfiguration : IEntityTypeConfiguration<Lesson>
{
    public void Configure(EntityTypeBuilder<Lesson> b)
    {
        b.ToTable("lessons", t => t.HasEnumCheck<Lesson, ContentStatus>("lessons", "status"));
        b.Property(x => x.Title).HasMaxLength(255);
        b.Property(x => x.Summary);
        b.Property(x => x.KeyPoints);
        b.Property(x => x.Status).AsEnumText();
        b.Property(x => x.AccessCode).HasMaxLength(10);

        // UNIQUE có lọc: bài chưa xuất bản chưa có mã.
        b.HasIndex(x => x.AccessCode).IsUnique().HasFilter("[access_code] IS NOT NULL")
            .HasDatabaseName("UX_lessons_access_code");

        b.HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Class).WithMany().HasForeignKey(x => x.ClassId).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LessonHeritageConfiguration : IEntityTypeConfiguration<LessonHeritage>
{
    public void Configure(EntityTypeBuilder<LessonHeritage> b)
    {
        b.ToTable("lesson_heritages");
        b.HasKey(x => new { x.LessonId, x.HeritageId });

        b.HasOne(x => x.Lesson).WithMany(l => l.Heritages).HasForeignKey(x => x.LessonId).IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Heritage).WithMany().HasForeignKey(x => x.HeritageId).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AiGenerationConfiguration : IEntityTypeConfiguration<AiGeneration>
{
    public void Configure(EntityTypeBuilder<AiGeneration> b)
    {
        b.ToTable("ai_generations", t => t
            .HasEnumCheck<AiGeneration, AiTaskType>("ai_generations", "task_type")
            .HasEnumCheck<AiGeneration, AiTaskStatus>("ai_generations", "status"));
        b.Property(x => x.TaskType).AsEnumText();
        b.Property(x => x.InputSnapshot);
        b.Property(x => x.Params);
        b.Property(x => x.Model).HasMaxLength(100);
        b.Property(x => x.RawOutput);
        b.Property(x => x.Status).AsEnumText();
        b.Property(x => x.ErrorMessage).HasMaxLength(1000);

        b.HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Requester).WithMany().HasForeignKey(x => x.RequestedBy).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}
