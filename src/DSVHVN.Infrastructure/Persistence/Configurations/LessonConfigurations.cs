using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Lessons;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

// Nhóm Bài học + AI của CSDL v3; bảng lesson_sections và các cột ai_generations.heritage_id... thêm ở phiên bản 4 bằng migration riêng.

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
        // Mỗi dòng thuộc đúng một bài học (soạn nội dung bài, tạo câu hỏi) hoặc một di sản (dịch, tóm tắt) — CSDL v4.
        b.ToTable("ai_generations", t =>
        {
            t.HasEnumCheck<AiGeneration, AiTaskType>("ai_generations", "task_type");
            t.HasEnumCheck<AiGeneration, AiTaskStatus>("ai_generations", "status");
            t.HasCheckConstraint("CK_ai_generations_subject",
                "([lesson_id] IS NOT NULL AND [heritage_id] IS NULL AND [task_type] IN ('LESSON_SUMMARY', 'QUIZ_GENERATION')) " +
                "OR ([lesson_id] IS NULL AND [heritage_id] IS NOT NULL AND [task_type] IN ('HERITAGE_TRANSLATION', 'HERITAGE_SUMMARY'))");
        });
        b.Property(x => x.TaskType).AsEnumText();
        b.Property(x => x.InputSnapshot);
        b.Property(x => x.Params);
        b.Property(x => x.Model).HasMaxLength(100);
        b.Property(x => x.RawOutput);
        b.Property(x => x.Status).AsEnumText();
        b.Property(x => x.ErrorMessage).HasMaxLength(1000);

        b.HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Heritage).WithMany().HasForeignKey(x => x.HeritageId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Requester).WithMany().HasForeignKey(x => x.RequestedBy).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Bảng <c>lesson_sections</c> (CSDL v4): các phần nội dung của bài học theo khung của cấp.</summary>
internal sealed class LessonSectionConfiguration : IEntityTypeConfiguration<LessonSection>
{
    public void Configure(EntityTypeBuilder<LessonSection> b)
    {
        b.ToTable("lesson_sections", t =>
        {
            t.HasEnumCheck<LessonSection, LessonSectionType>("lesson_sections", "section_type");
            // Mốc phải có năm, đoạn trích phải có nguồn, hình phải có đường dẫn.
            t.HasCheckConstraint("CK_lesson_sections_required",
                "([section_type] <> 'MILESTONE' OR [milestone_year] IS NOT NULL) "
                + "AND ([section_type] <> 'SOURCE_EXCERPT' OR [source_citation] IS NOT NULL) "
                + "AND ([section_type] <> 'IMAGE' OR [image_url] IS NOT NULL)");
        });
        b.Property(x => x.SectionType).AsEnumText();
        b.Property(x => x.Title).HasMaxLength(255);
        b.Property(x => x.Body);
        b.Property(x => x.ImageUrl).HasMaxLength(500);
        b.Property(x => x.SourceCitation).HasMaxLength(500);
        b.Property(x => x.SortOrder).IsRequired();
        b.Property(x => x.CreatedAt).IsRequired();

        b.HasIndex(x => new { x.LessonId, x.SortOrder }).HasDatabaseName("IX_lesson_sections_lesson_order");

        b.HasOne(x => x.Lesson).WithMany(l => l.Sections).HasForeignKey(x => x.LessonId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Event).WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
    }
}
