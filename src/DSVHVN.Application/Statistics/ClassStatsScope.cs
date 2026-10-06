using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Quizzes;

namespace DSVHVN.Application.Statistics;

/// <summary>
/// Phạm vi tính thống kê của một lớp: học sinh đang hoạt động (chưa xóa mềm, chưa vô hiệu) của lớp, và lượt đã nộp của các em ở bài kiểm tra của bài học
/// (chưa xóa) thuộc lớp. Lượt đang làm dở không tính vào điểm và mức thành thạo.
/// </summary>
internal static class ClassStatsScope
{
    public static IQueryable<Domain.Classes.Student> ActiveStudents(this IAppDbContext db, long classId) =>
        db.Students.Where(s => s.ClassId == classId && s.DeletedAt == null && s.IsActive);

    /// <summary>Mọi lượt (kể cả đang làm) của học sinh đang hoạt động ở bài kiểm tra của lớp.</summary>
    public static IQueryable<Attempt> AllAttempts(this IAppDbContext db, long classId) =>
        db.Attempts.Where(a => a.Student!.ClassId == classId && a.Student.DeletedAt == null && a.Student.IsActive
                               && a.Quiz!.DeletedAt == null && a.Quiz.Lesson!.DeletedAt == null && a.Quiz.Lesson.ClassId == classId);

    /// <summary>Lượt đã nộp hoặc hết giờ tự nộp của học sinh đang hoạt động ở bài kiểm tra của lớp.</summary>
    public static IQueryable<Attempt> FinishedAttempts(this IAppDbContext db, long classId) =>
        db.AllAttempts(classId).Where(a => a.Status != AttemptStatus.IN_PROGRESS);

    /// <summary>Câu trả lời của các lượt đã nộp ở <see cref="FinishedAttempts"/>.</summary>
    public static IQueryable<AttemptAnswer> FinishedAnswers(this IAppDbContext db, long classId) =>
        db.AttemptAnswers.Where(a => a.Attempt!.Status != AttemptStatus.IN_PROGRESS
                                     && a.Attempt.Student!.ClassId == classId && a.Attempt.Student.DeletedAt == null && a.Attempt.Student.IsActive
                                     && a.Attempt.Quiz!.DeletedAt == null && a.Attempt.Quiz.Lesson!.DeletedAt == null && a.Attempt.Quiz.Lesson.ClassId == classId);
}
