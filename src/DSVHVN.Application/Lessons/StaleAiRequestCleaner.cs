using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Lessons;

/// <summary>
/// Dọn các lượt gọi AI bị bỏ dở. Một lượt (<c>ai_generations</c>) còn "đang xử lý" sau khi máy chủ dừng hoặc khởi động lại giữa chừng sẽ không bao giờ tự ra khỏi trạng thái đó;
/// các chức năng chỉ bỏ qua nó khi đếm hạn mức hay kiểm tra "đang bận", còn dữ liệu và thống kê vẫn thấy nó. Bước này chuyển lượt quá ngưỡng sang thất bại kèm lý do hết thời gian.
/// Ngưỡng theo loại lượt, đều lớn hơn thời gian tối đa của các lần gọi và thử lại: lượt của bài học (soạn nội dung, tạo câu hỏi) quá
/// <see cref="LessonRules.AiStaleProcessing"/>; lượt của di sản (dịch, tóm tắt) quá <see cref="TranslationRules.StaleProcessing"/>.
/// Một câu lệnh có điều kiện nên chạy lặp lại hay chạy song song với lượt đang kết thúc đúng hạn đều không làm hỏng dữ liệu.
/// </summary>
public sealed class StaleAiRequestCleaner(IAppDbContext db, TimeProvider clock)
{
    public const string TimeoutMessage = "Yêu cầu AI bị bỏ dở do quá thời gian xử lý (máy chủ có thể đã dừng hoặc khởi động lại).";

    /// <summary>Chuyển các lượt đang xử lý quá ngưỡng sang thất bại. Trả số lượt đã đổi.</summary>
    public async Task<int> FailStaleAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var lessonBefore = now - LessonRules.AiStaleProcessing;
        var heritageBefore = now - TranslationRules.StaleProcessing;
        return await db.AiGenerations
            .Where(g => g.Status == AiTaskStatus.PROCESSING
                && ((g.LessonId != null && g.CreatedAt < lessonBefore) || (g.HeritageId != null && g.CreatedAt < heritageBefore)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(g => g.Status, AiTaskStatus.FAILED)
                .SetProperty(g => g.ErrorMessage, TimeoutMessage)
                .SetProperty(g => g.CompletedAt, now), ct);
    }
}
