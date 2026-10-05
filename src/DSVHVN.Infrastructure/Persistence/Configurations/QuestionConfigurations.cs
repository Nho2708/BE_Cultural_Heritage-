using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Questions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

// Nhóm Ngân hàng câu hỏi của CSDL v3, thêm cột và ràng buộc cho 8 dạng câu của CSDL v4.

internal sealed class QuestionConfiguration : IEntityTypeConfiguration<Question>
{
    public void Configure(EntityTypeBuilder<Question> b)
    {
        b.ToTable("questions", t =>
        {
            t.HasEnumCheck<Question, QuestionType>("questions", "question_type")
                .HasEnumCheck<Question, DifficultyLevel>("questions", "difficulty_level")
                .HasEnumCheck<Question, QuestionSource>("questions", "source")
                .HasEnumCheck<Question, ReviewStatus>("questions", "review_status");
            t.HasCheckConstraint("CK_questions_levels",
                "[level_min] BETWEEN 1 AND 3 AND [level_max] BETWEEN 1 AND 3 AND [level_min] <= [level_max]");
            t.HasCheckConstraint("CK_questions_passage", "[question_type] <> 'SOURCE_READING' OR [passage] IS NOT NULL");
        });
        b.Property(x => x.Content);
        b.Property(x => x.QuestionType).AsEnumText();
        b.Property(x => x.ImageUrl).HasMaxLength(500);
        b.Property(x => x.Passage);
        b.Property(x => x.PassageSource).HasMaxLength(500);
        b.Property(x => x.LevelMin).IsRequired().HasDefaultValue((byte)1).ValueGeneratedNever();
        b.Property(x => x.LevelMax).IsRequired().HasDefaultValue((byte)3).ValueGeneratedNever();
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
        b.ToTable("answers", t => t.HasCheckConstraint("CK_answers_positions",
            "([correct_position] IS NULL OR [correct_position] BETWEEN 1 AND 20) AND ([blank_index] IS NULL OR [blank_index] BETWEEN 1 AND 20)"));
        b.Property(x => x.Content).HasMaxLength(1000);
        b.Property(x => x.ImageUrl).HasMaxLength(500);
        b.Property(x => x.MatchContent).HasMaxLength(1000);
        b.Property(x => x.CorrectPosition);
        b.Property(x => x.BlankIndex);
        b.Property(x => x.IsCorrect).BitWithDefault(false);

        b.HasOne(x => x.Question).WithMany(q => q.Answers).HasForeignKey(x => x.QuestionId).IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
    }
}
