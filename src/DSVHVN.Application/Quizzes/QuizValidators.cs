using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using FluentValidation;

namespace DSVHVN.Application.Quizzes;

/// <summary>Các trường thiết lập chung của yêu cầu tạo và lưu bài kiểm tra, để dùng chung một bộ luật.</summary>
public interface IQuizSettingsRequest
{
    string? Name { get; }
    string? Description { get; }
    DifficultyLevel? DifficultyLevel { get; }
    int? TimeLimitMinutes { get; }
    DateTime? OpenAt { get; }
    DateTime? CloseAt { get; }
}

/// <summary>Luật chung của thiết lập bài kiểm tra; việc đối chiếu với cấp của lớp và trạng thái nằm ở dịch vụ.</summary>
internal static class QuizSettingsRules
{
    public static void Apply<T>(AbstractValidator<T> v) where T : IQuizSettingsRequest
    {
        v.RuleFor(x => x.Name).RequiredText("tên bài kiểm tra", "Tên bài kiểm tra", QuizRules.NameMaxLength);
        v.RuleFor(x => x.Description).OptionalText("Mô tả", QuizRules.DescriptionMaxLength);
        v.RuleFor(x => x.DifficultyLevel).Must(d => d is null || Enum.IsDefined(d.Value))
            .WithErrorCode(MessageCodes.Invalid).WithMessage(Messages.Invalid("Độ khó", "phải là Dễ, Trung bình hoặc Khó").Text);

        var minutes = Messages.Invalid("Thời gian làm bài", $"từ {QuizRules.TimeLimitMin} đến {QuizRules.TimeLimitMax} phút");
        v.RuleFor(x => x.TimeLimitMinutes).Must(t => t is null || t is >= QuizRules.TimeLimitMin and <= QuizRules.TimeLimitMax)
            .WithErrorCode(minutes.Code).WithMessage(minutes.Text);

        var order = Messages.Invalid("Giờ đóng", "phải sau giờ mở");
        v.RuleFor(x => x.CloseAt).Must((x, close) => x.OpenAt is not { } open || close is not { } c || QuizRules.ToUtc(open) < QuizRules.ToUtc(c))
            .WithErrorCode(order.Code).WithMessage(order.Text);
    }
}

public sealed class CreateQuizRequestValidator : AbstractValidator<CreateQuizRequest>
{
    public CreateQuizRequestValidator()
    {
        var lesson = Messages.Required("bài học");
        RuleFor(x => x.LessonId).NotNull().WithErrorCode(lesson.Code).WithMessage(lesson.Text);
        QuizSettingsRules.Apply(this);
    }
}

public sealed class UpdateQuizRequestValidator : AbstractValidator<UpdateQuizRequest>
{
    public UpdateQuizRequestValidator() =>
        QuizSettingsRules.Apply(this);
}

public sealed class AddQuizQuestionsRequestValidator : AbstractValidator<AddQuizQuestionsRequest>
{
    public AddQuizQuestionsRequestValidator()
    {
        var required = Messages.Required("câu hỏi cần thêm");
        RuleFor(x => x.QuestionIds).Must(ids => ids is { Count: > 0 }).WithErrorCode(required.Code).WithMessage(required.Text);
        var many = Messages.Invalid("Danh sách câu hỏi", $"tối đa {QuizRules.MaxQuestions} câu mỗi lần");
        RuleFor(x => x.QuestionIds).Must(ids => ids is null || ids.Count <= QuizRules.MaxQuestions)
            .WithErrorCode(many.Code).WithMessage(many.Text);
        RuleFor(x => x.Point).Custom(PointRule.Check);
    }
}

public sealed class ReplaceQuizQuestionsRequestValidator : AbstractValidator<ReplaceQuizQuestionsRequest>
{
    public ReplaceQuizQuestionsRequestValidator()
    {
        var required = Messages.Required("danh sách câu hỏi");
        RuleFor(x => x.Items).NotNull().WithErrorCode(required.Code).WithMessage(required.Text);
        RuleFor(x => x.Items).Custom((items, context) =>
        {
            if (items is null) return;
            var seen = new HashSet<long>();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item?.QuestionId is not { } id)
                {
                    QuizValidation.Fail(context, $"items[{i}].questionId", Messages.Required("câu hỏi"));
                    continue;
                }
                if (!seen.Add(id))
                    QuizValidation.Fail(context, $"items[{i}].questionId", Messages.Invalid("Câu hỏi", "mỗi câu chỉ xuất hiện một lần trong bài kiểm tra"));
                if (item.Point is not { } point || !QuizRules.IsValidPoint(point))
                    QuizValidation.Fail(context, $"items[{i}].point", PointRule.Message);
            }
        });
    }
}

public sealed class DuplicateQuizRequestValidator : AbstractValidator<DuplicateQuizRequest>
{
    public DuplicateQuizRequestValidator() =>
        RuleFor(x => x.Name).OptionalText("Tên bài kiểm tra", QuizRules.NameMaxLength);
}

internal static class PointRule
{
    public static AppMessage Message => Messages.Invalid("Điểm", $"lớn hơn 0, không quá {QuizRules.MaxPoint:0}, theo bước {QuizRules.PointStep.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

    public static void Check(decimal? point, ValidationContext<AddQuizQuestionsRequest> context)
    {
        if (point is { } p && !QuizRules.IsValidPoint(p)) QuizValidation.Fail(context, "point", Message);
    }
}

internal static class QuizValidation
{
    public static void Fail<T>(ValidationContext<T> context, string property, AppMessage message) =>
        context.AddFailure(new FluentValidation.Results.ValidationFailure(property, message.Text) { ErrorCode = message.Code });
}
