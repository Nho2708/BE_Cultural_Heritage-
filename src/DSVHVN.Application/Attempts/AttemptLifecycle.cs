using DSVHVN.Application.Common;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Attempts;

/// <summary>
/// Chốt một lượt làm bài đã quá hạn: chấm các câu đã lưu và chuyển sang hết giờ (tự nộp), lúc nộp là đúng hạn nộp. Dùng khi em mở lại lượt đã quá
/// hạn nhận bài (hạn nộp cộng 30 giây ân hạn) và ở tác vụ nền mỗi phút. Chạy lặp được: lượt đã chốt không bị chốt lần nữa.
/// </summary>
public sealed class AttemptLifecycle(IAppDbContext db, TimeProvider clock)
{
    private const int BatchSize = 200;

    public DateTime Now
    {
        get
        {
            var now = clock.GetUtcNow().UtcDateTime;
            return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
        }
    }

    /// <summary>
    /// Lượt đang làm dở mà đã quá hạn nhận bài thì chốt thành hết giờ. Trả true nếu vừa chốt. <paramref name="level"/> là cấp hiện tại của lớp
    /// (cấp 1 không có thời gian làm).
    /// </summary>
    public async Task<bool> ExpireIfOverdueAsync(long attemptId, int level, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockAttemptAsync(attemptId, ct);
        var attempt = await db.Attempts.Include(a => a.Quiz).SingleOrDefaultAsync(a => a.Id == attemptId, ct);
        if (attempt?.Quiz is null || attempt.Status != AttemptStatus.IN_PROGRESS) return false;

        var now = Now;
        var deadline = AttemptFinisher.DeadlineOf(attempt, attempt.Quiz, level);
        if (!AttemptRules.IsOverdue(deadline, now)) return false;

        var items = await AttemptFinisher.LoadQuizQuestionsAsync(db, attempt.QuizId, ct);
        var answers = await AttemptFinisher.LoadAnswersAsync(db, attempt.Id, ct);
        AttemptFinisher.Finish(db, attempt.Quiz, items, attempt, answers, AttemptStatus.EXPIRED, AttemptFinisher.SubmitTime(now, deadline), level);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>Tác vụ nền: chốt các lượt đã quá hạn nhận bài. Trả số lượt đã chốt.</summary>
    public async Task<int> ExpireOverdueAsync(CancellationToken ct)
    {
        var now = Now;
        var cutoff = now.AddSeconds(-AttemptRules.GraceSeconds);
        var candidates = await db.Attempts.AsNoTracking()
            .Where(a => a.Status == AttemptStatus.IN_PROGRESS && a.StartedAt != null && a.StartedAt < cutoff)
            .Select(a => new
            {
                a.Id,
                a.StartedAt,
                a.Quiz!.TimeLimitMinutes,
                a.Quiz.CloseAt,
                Grade = a.Quiz.Lesson!.Class!.Grade,
            })
            .ToListAsync(ct);

        var overdue = candidates
            .Select(c => (c.Id, Level: SchoolClass.SchoolLevelOf(c.Grade), c.StartedAt, c.TimeLimitMinutes, c.CloseAt))
            .Where(c => AttemptRules.IsOverdue(AttemptRules.DeadlineOf(c.StartedAt!.Value, AttemptRules.EffectiveTimeLimit(c.Level, c.TimeLimitMinutes), c.CloseAt), now))
            .Take(BatchSize)
            .ToList();

        var expired = 0;
        foreach (var c in overdue)
        {
            if (await ExpireIfOverdueAsync(c.Id, c.Level, ct)) expired++;
        }
        return expired;
    }
}
