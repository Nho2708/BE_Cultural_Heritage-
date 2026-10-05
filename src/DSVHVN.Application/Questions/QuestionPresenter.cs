using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Questions;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Questions;

/// <summary>
/// Quyền của người xem trên một câu theo quy tắc Ngân hàng câu hỏi dùng chung và Duyệt câu hỏi và ẩn câu kém chất lượng:
/// chỉ người tạo sửa, xóa, duyệt câu của mình, và chỉ khi câu chưa nằm trong quiz nào; câu bị quản trị hệ thống ẩn chỉ đọc và không nhân bản được.
/// </summary>
public static class QuestionAccessRules
{
    /// <summary>Câu bị quản trị hệ thống ẩn: bị loại (REJECTED) bởi tài khoản quản trị hệ thống. Cần nạp <see cref="Question.Reviewer"/>.</summary>
    public static bool IsHiddenByAdmin(Question q) =>
        q.ReviewStatus == ReviewStatus.REJECTED && q.Reviewer is { RoleId: Roles.AdminId };

    public static QuestionAccessDto ForTeacher(Question q, long teacherId, bool inQuiz)
    {
        var owner = q.CreatedBy == teacherId;
        var hidden = IsHiddenByAdmin(q);
        QuestionReadOnlyReason? reason = hidden ? QuestionReadOnlyReason.HIDDEN_BY_ADMIN
            : !owner ? QuestionReadOnlyReason.NOT_OWNER
            : inQuiz ? QuestionReadOnlyReason.USED_IN_QUIZ
            : null;
        var canChange = owner && !hidden && !inQuiz;
        return new QuestionAccessDto(
            reason is not null, reason, reason is { } r ? ReadOnlyText(r) : null,
            CanEdit: canChange,
            CanDelete: canChange,
            CanApprove: canChange && q.ReviewStatus is ReviewStatus.PENDING or ReviewStatus.REJECTED,
            CanReject: canChange && q.ReviewStatus == ReviewStatus.PENDING,
            CanDuplicate: !hidden && q.ReviewStatus == ReviewStatus.APPROVED);
    }

    /// <summary>Quản trị hệ thống xem chỉ đọc mọi câu đã duyệt hoặc đã bị ẩn; ẩn được câu đã duyệt.</summary>
    public static QuestionAccessDto ForAdmin(Question q) => new(
        true, null, null, false, false, false, false, false,
        CanHide: q.ReviewStatus == ReviewStatus.APPROVED);

    /// <summary>Câu thông báo câu hỏi chỉ đọc kèm hướng dẫn (hai lý do đầu cho nhân bản, câu bị ẩn thì tạo câu mới).</summary>
    public static string ReadOnlyText(QuestionReadOnlyReason reason) => reason switch
    {
        QuestionReadOnlyReason.USED_IN_QUIZ => "Câu hỏi này chỉ xem được vì đã được dùng trong bài kiểm tra. Bấm \"Nhân bản\" để tạo bản của bạn rồi sửa.",
        QuestionReadOnlyReason.NOT_OWNER => "Câu hỏi này chỉ xem được vì do người khác tạo. Bấm \"Nhân bản\" để tạo bản của bạn rồi sửa.",
        _ => "Câu hỏi này chỉ xem được vì đã bị quản trị hệ thống ẩn. Câu bị ẩn không nhân bản được; bạn có thể tạo câu mới.",
    };
}

/// <summary>Dựng chi tiết và dòng danh sách của câu hỏi cho giao diện (giáo viên và quản trị hệ thống dùng chung).</summary>
public sealed class QuestionPresenter(IAppDbContext db, LessonMaterialService materialService)
{
    /// <summary>Câu cần nạp sẵn <c>Answers</c>, <c>Heritage</c>, <c>Creator</c> (kèm trường) và <c>Reviewer</c>.</summary>
    public async Task<QuestionDetailDto> DetailAsync(Question q, QuestionAccessDto access, int quizCount, CancellationToken ct)
    {
        var shape = QuestionShape.Of(q);
        var type = q.QuestionType ?? QuestionType.SINGLE_CHOICE;
        var materials = await materialService.LoadAsync([q.HeritageId], ct);
        var images = QuestionShape.ImageUrlsOf(q)
            .Select(url => new QuestionImageDto(url, materials.CreditOf(url)?.Caption, materials.CreditOf(url)?.Credit)).ToList();
        var levels = Enumerable.Range(q.LevelMin, q.LevelMax - q.LevelMin + 1).ToList();

        return new QuestionDetailDto(
            q.Id, type, QuestionTypes.NameOf(type), q.HeritageId, q.Heritage?.Name, q.Content, q.Explanation, q.DifficultyLevel,
            q.LevelMin, q.LevelMax, levels, q.ImageUrl, q.Passage, q.PassageSource, shape.AnswerIsTrue, shape.Options, shape.Pairs, shape.Steps,
            shape.Blanks, shape.Distractors, shape.WordBank, shape.Statements, QuestionGrader.PartsTotal(type, q.Answers), images,
            q.Source, q.ReviewStatus, q.CreatedBy, q.Creator?.FullName, q.Creator?.Organization?.Name, q.AiGenerationId, q.ReviewedBy,
            q.ReviewedAt, quizCount, access, q.CreatedAt, q.UpdatedAt);
    }

    public Task<int> QuizCountAsync(long questionId, CancellationToken ct) =>
        db.QuizQuestions.CountAsync(x => x.QuestionId == questionId, ct);

    /// <summary>Thư viện ảnh (đường dẫn) của một di sản đã xuất bản: ảnh của di sản và ảnh của các sự kiện trên dòng thời gian.</summary>
    public async Task<HashSet<string>> LibraryUrlsAsync(long heritageId, CancellationToken ct)
    {
        var materials = await materialService.LoadAsync([heritageId], ct);
        return materials.Images.Where(i => !string.IsNullOrEmpty(i.Url)).Select(i => i.Url!).ToHashSet(StringComparer.Ordinal);
    }

    public static QuestionRowDto Row(Question q, string? creatorName, string? organizationName, string? heritageName, bool isMine, bool hidden, int quizCount)
    {
        var type = q.QuestionType ?? QuestionType.SINGLE_CHOICE;
        return new QuestionRowDto(
            q.Id, type, QuestionTypes.NameOf(type), q.Content ?? string.Empty, q.ImageUrl, q.HeritageId, heritageName, q.DifficultyLevel,
            q.LevelMin, q.LevelMax, q.Source, q.ReviewStatus, q.CreatedBy, creatorName, organizationName, isMine, hidden, quizCount,
            q.CreatedAt, q.UpdatedAt);
    }
}
