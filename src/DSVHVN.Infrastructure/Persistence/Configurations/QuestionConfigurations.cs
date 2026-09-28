using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Questions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

// Nhóm Ngân hàng câu hỏi của CSDL v3.

internal sealed class QuestionConfiguration : IEntityTypeConfiguration<Question>
{
    public void Configure(EntityTypeBuilder<Question> b)
    {
        b.ToTable("questions", t => t
            .HasEnumCheck<Question, QuestionType>("questions", "question_type")
            .HasEnumCheck<Question, DifficultyLevel>("questions", "difficulty_level")
            .HasEnumCheck<Question, QuestionSource>("questions", "source")
            .HasEnumCheck<Question, ReviewStatus>("questions", "review_status"));
        b.Property(x => x.Content);
        b.Property(x => x.QuestionType).AsEnumText();
        b.Property(x => x.DifficultyLevel).AsEnumText();
        b.Property(x => x.Explanation);
        b.Property(x => x.Source).AsEnumText();
        b.Property(x => x.ReviewStatus).AsEnumText();

        b.HasOne(x => x.Heritage).WithMany().HasForeignKey(x => x.HeritageId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Creator).WithMany().HasForeignKey(x => x.CreatedBy).IsRequired().OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.AiGeneration).WithMany().HasForeignKey(x => x.AiGenerationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Reviewer).WithMany().HasForeignKey(x => x.ReviewedBy).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AnswerConfiguration : IEntityTypeConfiguration<Answer>
{
    public void Configure(EntityTypeBuilder<Answer> b)
    {
        b.ToTable("answers");
        b.Property(x => x.Content).HasMaxLength(1000);
        b.Property(x => x.IsCorrect).BitWithDefault(false);

        b.HasOne(x => x.Question).WithMany(q => q.Answers).HasForeignKey(x => x.QuestionId).IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
    }
}
