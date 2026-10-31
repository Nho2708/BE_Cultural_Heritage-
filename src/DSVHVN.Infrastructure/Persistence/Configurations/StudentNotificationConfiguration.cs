using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Notifications;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

internal sealed class StudentNotificationConfiguration : IEntityTypeConfiguration<StudentNotification>
{
    public void Configure(EntityTypeBuilder<StudentNotification> b)
    {
        b.ToTable("student_notifications", t => t.HasEnumCheck<StudentNotification, StudentNotificationKind>("student_notifications", "kind"));
        b.Property(x => x.Kind).AsEnumText();
        b.Property(x => x.Title).HasMaxLength(StudentNotificationRules.TitleMaxLength).IsRequired();
        b.Property(x => x.Body).HasMaxLength(StudentNotificationRules.TitleMaxLength + StudentNotificationRules.MessageMaxLength).IsRequired();

        // Trang thông báo của em: mới nhất trước; đếm chưa đọc.
        b.HasIndex(x => new { x.StudentId, x.ReadAt }).HasDatabaseName("IX_student_notifications_student_read");

        b.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Quiz).WithMany().HasForeignKey(x => x.QuizId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Sender).WithMany().HasForeignKey(x => x.SentBy).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}
