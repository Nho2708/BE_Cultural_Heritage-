using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Quizzes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

// Nhóm Quiz và nhóm Làm bài / Kết quả của CSDL v3.

internal sealed class QuizConfiguration : IEntityTypeConfiguration<Quiz>
{
    public void Configure(EntityTypeBuilder<Quiz> b)
    {
        b.ToTable("quizzes", t => t
            .HasEnumCheck<Quiz, DifficultyLevel>("quizzes", "difficulty_level")
            .HasEnumCheck<Quiz, QuizStatus>("quizzes", "status"));
        b.Property(x => x.Name).HasMaxLength(255);
        b.Property(x => x.Description);
        b.Property(x => x.DifficultyLevel).AsEnumText();
        b.Property(x => x.ShuffleQuestions).BitWithDefault(false);
        b.Property(x => x.ShowAnswersAfterSubmit).BitWithDefault(false);
        b.Property(x => x.TotalPoint).HasPrecision(6, 2);
        b.Property(x => x.Status).AsEnumText();

        b.HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Creator).WithMany().HasForeignKey(x => x.CreatedBy).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class QuizQuestionConfiguration : IEntityTypeConfiguration<QuizQuestion>
{
    public void Configure(EntityTypeBuilder<QuizQuestion> b)
    {
        b.ToTable("quiz_questions");
        b.HasKey(x => new { x.QuizId, x.QuestionId });
        b.Property(x => x.Point).HasPrecision(5, 2);

        b.HasOne(x => x.Quiz).WithMany(q => q.Questions).HasForeignKey(x => x.QuizId).IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Question).WithMany().HasForeignKey(x => x.QuestionId).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AttemptConfiguration : IEntityTypeConfiguration<Attempt>
{
    public void Configure(EntityTypeBuilder<Attempt> b)
    {
        b.ToTable("attempts", t => t.HasEnumCheck<Attempt, AttemptStatus>("attempts", "status"));
        b.Property(x => x.Score).HasPrecision(6, 2);
        b.Property(x => x.TotalPoint).HasPrecision(6, 2);
        b.Property(x => x.Status).AsEnumText();

        // Mỗi học sinh chỉ làm một lần mỗi quiz; chặn cả hai yêu cầu bắt đầu gửi đồng thời.
        b.HasIndex(x => new { x.QuizId, x.StudentId }).IsUnique().HasDatabaseName("UX_attempts_quiz_student");

        b.HasOne(x => x.Quiz).WithMany().HasForeignKey(x => x.QuizId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AttemptAnswerConfiguration : IEntityTypeConfiguration<AttemptAnswer>
{
    public void Configure(EntityTypeBuilder<AttemptAnswer> b)
    {
        b.ToTable("attempt_answers");
        b.Property(x => x.IsCorrect).BitWithDefault(false);
        b.Property(x => x.PointEarned).HasPrecision(5, 2);

        // Mỗi câu một dòng trong mỗi lượt; chặn ghi trùng khi nộp hai lần.
        b.HasIndex(x => new { x.AttemptId, x.QuestionId }).IsUnique().HasDatabaseName("UX_attempt_answers_attempt_question");

        b.HasOne(x => x.Attempt).WithMany(a => a.Answers).HasForeignKey(x => x.AttemptId).IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Question).WithMany().HasForeignKey(x => x.QuestionId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Answer).WithMany().HasForeignKey(x => x.AnswerId).OnDelete(DeleteBehavior.Restrict);
    }
}
