using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Application.Questions;

// Hợp đồng gọi Dịch vụ AI sinh câu hỏi (POST /v1/generate-questions trong kiến trúc). Cùng cách làm với soạn nội dung bài học: backend chỉ biết giao diện
// IQuestionAiClient; bản giả lập tất định (không gọi nhà cung cấp nào) và bản gọi dịch vụ AI nội bộ qua HTTP cùng thực hiện giao diện này,
// chọn bằng cấu hình Ai:Mode (dùng chung mục Ai với soạn nội dung bài học). Dữ liệu gửi đi chỉ gồm nội dung di sản, cấp học của lớp, dạng câu,
// độ khó, số câu và danh sách ảnh có sẵn của di sản; không có họ tên, mã học sinh, tên lớp, email.

/// <summary>
/// Yêu cầu sinh câu. <see cref="GradeLevel"/> là cấp học suy từ khối lớp của bài (1, 2, 3); <see cref="Types"/> là các dạng được chọn (đều dùng
/// được ở cấp đó); <see cref="Heritages"/> là các di sản AI được dùng (câu sinh ra phải gắn một trong các di sản này); mọi ảnh trong câu phải chọn
/// từ <c>Images</c> của di sản đó, AI không tạo ảnh mới.
/// </summary>
public sealed record AiQuestionRequest(
    long GenerationId,
    long LessonId,
    int GradeLevel,
    int Count,
    DifficultyLevel Difficulty,
    IReadOnlyList<QuestionType> Types,
    IReadOnlyList<AiHeritageInput> Heritages);

/// <summary>
/// Một câu AI sinh, cùng khuôn đáp án với <see cref="SaveQuestionRequest"/> (theo dạng) nhưng không có cấp, độ khó: hai giá trị đó do backend đặt
/// theo cấp của lớp và độ khó đã yêu cầu. <see cref="HeritageId"/> là di sản gắn với câu.
/// </summary>
public sealed record AiGeneratedQuestion(
    long HeritageId,
    QuestionType QuestionType,
    string? Content,
    string? Explanation,
    string? ImageUrl = null,
    string? Passage = null,
    string? PassageSource = null,
    bool? AnswerIsTrue = null,
    List<QuestionOption>? Options = null,
    List<QuestionPair>? Pairs = null,
    List<string>? Steps = null,
    List<QuestionBlank>? Blanks = null,
    List<string>? Distractors = null,
    List<QuestionStatement>? Statements = null)
{
    public SaveQuestionRequest ToSaveRequest(int level, DifficultyLevel difficulty) => new(
        QuestionType, HeritageId, Content, Explanation, difficulty, level, level, ImageUrl, Passage, PassageSource, AnswerIsTrue,
        Options, Pairs, Steps, Blanks, Distractors, Statements);
}

/// <summary>Các câu AI trả về, tên mô hình và số token đã dùng (ghi để theo dõi chi phí).</summary>
public sealed record AiQuestionResult(IReadOnlyList<AiGeneratedQuestion> Questions, string Model, AiUsage Usage);

/// <summary>Cổng gọi Dịch vụ AI sinh câu hỏi. Người gọi đặt thời gian chờ mỗi lần qua <c>CancellationToken</c>; lỗi ném <see cref="AiServiceException"/>.</summary>
public interface IQuestionAiClient
{
    /// <summary>Tên mô hình hoặc bản giả lập, ghi vào <c>ai_generations.model</c> của lần gọi thành công.</summary>
    string ModelName { get; }

    Task<AiQuestionResult> GenerateQuestionsAsync(AiQuestionRequest request, string correlationId, CancellationToken ct);
}
