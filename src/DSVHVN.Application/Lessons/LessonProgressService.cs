using DSVHVN.Application.Attempts;
using DSVHVN.Application.Common;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Lessons;

/// <summary>
/// Hai thao tác của em ở bài học cấp 1 ngoài việc mở bài: ghi em đã tới bước Nhận thẻ (thẻ di sản của bài vào album) và lấy các câu ôn của bước Nhớ lại.
/// Cả hai kiểm mã bài học như mở bài bằng mã (cùng thông báo 404 khi mã sai, bài chưa xuất bản hoặc khác lớp, và cùng giới hạn nhập sai).
/// Không phụ thuộc cấp của lớp; giao diện chỉ gọi ở cấp 1.
/// </summary>
public sealed class LessonProgressService(IAppDbContext db, StudentLessonService lessons, AlbumService albums, TimeProvider clock)
{
    private DateTime Now
    {
        get
        {
            var now = clock.GetUtcNow().UtcDateTime;
            return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
        }
    }

    /// <summary>
    /// Ghi em đã học xong bài (đến bước Nhận thẻ). Chưa có dòng thì thêm; đã có thì giữ nguyên (gọi lại nhiều lần vẫn chỉ một dòng). Hai yêu cầu đến cùng lúc:
    /// ràng buộc không trùng chặn yêu cầu sau, yêu cầu đó coi như đã có. Trả các thẻ di sản của bài (đã vào album), số thẻ em đã có và một câu mang về nhà.
    /// </summary>
    public async Task<LessonCompletionDto> CompleteAsync(Actor actor, string? rawCode, CancellationToken ct)
    {
        var resolved = await lessons.ResolveByCodeAsync(actor, rawCode, includeSections: false, ct);
        var lesson = resolved.Lesson;

        var now = Now;
        var firstTime = false;
        if (!await db.LessonCompletions.AnyAsync(c => c.StudentId == resolved.StudentId && c.LessonId == lesson.Id, ct))
        {
            var completion = new LessonCompletion { StudentId = resolved.StudentId, LessonId = lesson.Id, CompletedAt = now };
            db.LessonCompletions.Add(completion);
            try
            {
                await db.SaveChangesAsync(ct);
                firstTime = true;
            }
            catch (DbUpdateException)
            {
                // Hai yêu cầu cùng ghi một lúc: ràng buộc không trùng chỉ cho một dòng, yêu cầu còn lại coi như đã có.
                db.LessonCompletions.Entry(completion).State = EntityState.Detached;
                if (!await db.LessonCompletions.AnyAsync(c => c.StudentId == resolved.StudentId && c.LessonId == lesson.Id, ct)) throw;
            }
        }

        var album = await albums.LoadAsync(resolved.StudentId, resolved.ClassId, ct);
        var cards = await albums.LessonCardsAsync(album, lesson.Id, lesson.Title, now, ct);
        var takeHome = await albums.TakeHomeAsync(lesson.Id, ct);
        return new LessonCompletionDto(lesson.Id, firstTime, cards, album.Collected, album.Total, takeHome);
    }

    /// <summary>
    /// Các câu ôn của bước Nhớ lại: tối đa 3 câu từ các lượt em đã nộp ở bài khác của lớp (lượt nộp gần nhất trước), chỉ dạng Đúng/Sai và Chọn hình đúng, câu chưa xóa,
    /// ưu tiên câu em làm chưa đúng rồi câu đúng, không trùng câu. Thẻ của Chọn hình đúng xáo tất định theo mã học sinh và mã câu; Đúng/Sai giữ thứ tự "Đúng", "Sai".
    /// Chỉ đọc, không lưu gì, không tính sao. Em chưa nộp bài nào thì danh sách rỗng.
    /// </summary>
    public async Task<StudentRecallDto> RecallAsync(Actor actor, string? rawCode, CancellationToken ct)
    {
        var resolved = await lessons.ResolveByCodeAsync(actor, rawCode, includeSections: false, ct);

        var recallTypes = AlbumRules.RecallTypes;
        var rows = await db.AttemptAnswers.AsNoTracking()
            .Where(a => a.Attempt!.StudentId == resolved.StudentId && a.Attempt.Status != AttemptStatus.IN_PROGRESS
                        && a.Attempt.Quiz!.Lesson!.ClassId == resolved.ClassId && a.Attempt.Quiz.LessonId != resolved.Lesson.Id
                        && a.Attempt.Quiz.Lesson.DeletedAt == null
                        && a.Question!.DeletedAt == null && a.Question.QuestionType != null && recallTypes.Contains(a.Question.QuestionType.Value))
            .Select(a => new
            {
                a.QuestionId, a.AttemptId, a.IsCorrect, a.Attempt!.SubmittedAt, LessonTitle = a.Attempt.Quiz!.Lesson!.Title,
            })
            .ToListAsync(ct);

        var pickedIds = AlbumRules.PickRecall(rows.Select(r => new AlbumRules.RecallCandidate(r.QuestionId, r.AttemptId, r.SubmittedAt, r.IsCorrect)));
        if (pickedIds.Count == 0) return new StudentRecallDto([]);

        var questions = await db.Questions.AsNoTracking().Include(q => q.Answers).Where(q => pickedIds.Contains(q.Id)).ToListAsync(ct);
        var heritageIds = questions.Select(q => q.HeritageId).Distinct().ToList();
        var heritageNames = await db.Heritages.AsNoTracking().Where(h => heritageIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, h => h.Name, ct);
        // Tên bài của lượt được chọn (lượt đã xếp ưu tiên) cho từng câu.
        var lessonTitles = rows.GroupBy(r => r.QuestionId).ToDictionary(g => g.Key,
            g => g.OrderBy(r => r.IsCorrect).ThenByDescending(r => r.SubmittedAt).ThenByDescending(r => r.AttemptId).First().LessonTitle);

        var result = new List<RecallQuestionDto>();
        foreach (var id in pickedIds)
        {
            var question = questions.FirstOrDefault(q => q.Id == id);
            if (question is null) continue;
            var layout = QuestionLayout.Build(question, resolved.StudentId);
            var shown = AttemptViews.Question(layout, result.Count + 1, null, null);
            var correctIndex = layout.OptionOrder.ToList().FindIndex(i => layout.Rows[i].IsCorrect);
            if (correctIndex < 0) continue;
            result.Add(new RecallQuestionDto(
                question.Id, layout.Type, question.Content ?? string.Empty, question.ImageUrl, shown.Options, correctIndex, question.Explanation,
                heritageNames.GetValueOrDefault(question.HeritageId), lessonTitles.GetValueOrDefault(question.Id)));
        }
        return new StudentRecallDto(result);
    }
}
