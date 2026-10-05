using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using FluentValidation;

namespace DSVHVN.Application.Lessons;

public sealed class CreateLessonRequestValidator : AbstractValidator<CreateLessonRequest>
{
    public CreateLessonRequestValidator()
    {
        var classRequired = Messages.Required("lớp");
        RuleFor(x => x.ClassId).NotNull().WithErrorCode(classRequired.Code).WithMessage(classRequired.Text);
        RuleFor(x => x.Title).RequiredText("tiêu đề bài học", "Tiêu đề bài học", LessonRules.TitleMaxLength);
        RuleFor(x => x.HeritageIds).Custom(LessonValidation.Heritages);
    }
}

public sealed class UpdateLessonRequestValidator : AbstractValidator<UpdateLessonRequest>
{
    public UpdateLessonRequestValidator()
    {
        RuleFor(x => x.Title).RequiredText("tiêu đề bài học", "Tiêu đề bài học", LessonRules.TitleMaxLength).When(x => x.Title is not null);
        RuleFor(x => x.Summary).Must(s => s is null || s.Length <= LessonRules.SummaryMaxLength)
            .WithAppMessage(Messages.Invalid("Tóm tắt", $"tối đa {LessonRules.SummaryMaxLength} ký tự"));
        RuleFor(x => x.KeyPoints).Custom(LessonValidation.KeyPoints);
        RuleFor(x => x.Sections).Custom(LessonValidation.Sections);
        RuleFor(x => x.HeritageIds).Custom(LessonValidation.Heritages).When(x => x.HeritageIds is not null);
    }
}

public sealed class DuplicateLessonRequestValidator : AbstractValidator<DuplicateLessonRequest>
{
    public DuplicateLessonRequestValidator()
    {
        var required = Messages.Required("lớp đích");
        RuleFor(x => x.TargetClassId).NotNull().WithErrorCode(required.Code).WithMessage(required.Text);
    }
}

public sealed class ComposeLessonRequestValidator : AbstractValidator<ComposeLessonRequest>
{
    public ComposeLessonRequestValidator()
    {
        RuleFor(x => x.Length).Must(l => l is null || Enum.IsDefined(l.Value))
            .WithErrorCode(MessageCodes.Invalid).WithMessage(Messages.Invalid("Độ dài", "phải là Ngắn, Vừa hoặc Chi tiết").Text);
    }
}

/// <summary>Kiểm tra cấu trúc của nội dung bài gửi lên: độ dài, trường bắt buộc theo loại phần. Việc đối chiếu với cấp của lớp và thư viện ảnh nằm ở dịch vụ.</summary>
internal static class LessonValidation
{
    public static void Heritages(IReadOnlyList<long>? ids, FluentValidation.ValidationContext<CreateLessonRequest> context) => Check(ids, context);

    public static void Heritages(IReadOnlyList<long>? ids, FluentValidation.ValidationContext<UpdateLessonRequest> context) => Check(ids, context);

    private static void Check<T>(IReadOnlyList<long>? ids, ValidationContext<T> context)
    {
        if (ids is null || ids.Count < LessonRules.MinHeritages)
        {
            Fail(context, nameof(CreateLessonRequest.HeritageIds), Messages.Required("ít nhất một di sản"));
            return;
        }
        if (ids.Count > LessonRules.MaxHeritages || ids.Distinct().Count() != ids.Count)
            Fail(context, nameof(CreateLessonRequest.HeritageIds),
                Messages.Invalid("Di sản của bài", $"chọn từ {LessonRules.MinHeritages} đến {LessonRules.MaxHeritages} di sản, không lặp"));
    }

    public static void KeyPoints(IReadOnlyList<string>? points, ValidationContext<UpdateLessonRequest> context)
    {
        if (points is null) return;
        if (points.Count > LessonRules.MaxKeyPoints)
        {
            Fail(context, nameof(UpdateLessonRequest.KeyPoints), Messages.Invalid("Ý chính", $"tối đa {LessonRules.MaxKeyPoints} ý"));
            return;
        }
        for (var i = 0; i < points.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(points[i]))
                Fail(context, $"{nameof(UpdateLessonRequest.KeyPoints)}[{i}]", Messages.Required("nội dung ý chính"));
            else if (points[i].Trim().Length > LessonRules.KeyPointMaxLength)
                Fail(context, $"{nameof(UpdateLessonRequest.KeyPoints)}[{i}]",
                    Messages.Invalid("Ý chính", $"tối đa {LessonRules.KeyPointMaxLength} ký tự"));
        }
    }

    public static void Sections(IReadOnlyList<LessonSectionRequest>? sections, ValidationContext<UpdateLessonRequest> context)
    {
        if (sections is null) return;
        if (sections.Count > LessonRules.MaxSections)
        {
            Fail(context, nameof(UpdateLessonRequest.Sections), Messages.Invalid("Số phần nội dung", $"tối đa {LessonRules.MaxSections} phần"));
            return;
        }

        for (var i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            var at = $"{nameof(UpdateLessonRequest.Sections)}[{i}]";
            if (s is null || s.Type is not { } type || !Enum.IsDefined(type))
            {
                Fail(context, $"{at}.{nameof(LessonSectionRequest.Type)}", Messages.Required("loại phần nội dung"));
                continue;
            }

            if (s.Title is { } title && title.Trim().Length > LessonRules.SectionTitleMaxLength)
                Fail(context, $"{at}.{nameof(LessonSectionRequest.Title)}", Messages.Invalid("Tiêu đề phần", $"tối đa {LessonRules.SectionTitleMaxLength} ký tự"));
            if (s.Body is { } body && body.Length > LessonRules.SectionBodyMaxLength)
                Fail(context, $"{at}.{nameof(LessonSectionRequest.Body)}", Messages.Invalid("Nội dung phần", $"tối đa {LessonRules.SectionBodyMaxLength} ký tự"));
            if (s.ImageUrl is { } url && url.Trim().Length > LessonRules.ImageUrlMaxLength)
                Fail(context, $"{at}.{nameof(LessonSectionRequest.ImageUrl)}", Messages.Invalid("Đường dẫn hình", $"tối đa {LessonRules.ImageUrlMaxLength} ký tự"));
            if (s.SourceCitation is { } cite && cite.Trim().Length > LessonRules.SourceCitationMaxLength)
                Fail(context, $"{at}.{nameof(LessonSectionRequest.SourceCitation)}", Messages.Invalid("Nguồn", $"tối đa {LessonRules.SourceCitationMaxLength} ký tự"));

            var hasBody = !string.IsNullOrWhiteSpace(s.Body);
            switch (type)
            {
                case LessonSectionType.IMAGE:
                    if (string.IsNullOrWhiteSpace(s.ImageUrl))
                        Fail(context, $"{at}.{nameof(LessonSectionRequest.ImageUrl)}", Messages.Required("hình"));
                    break;
                case LessonSectionType.MILESTONE:
                    if (s.MilestoneYear is not { } year)
                        Fail(context, $"{at}.{nameof(LessonSectionRequest.MilestoneYear)}", Messages.Required("năm của mốc thời gian"));
                    else if (year < LessonRules.MinMilestoneYear || year > DateTime.UtcNow.Year + 1)
                        Fail(context, $"{at}.{nameof(LessonSectionRequest.MilestoneYear)}",
                            Messages.Invalid("Năm của mốc", $"phải từ {LessonRules.MinMilestoneYear} (năm âm là trước Công nguyên) đến năm hiện tại"));
                    if (!hasBody) Fail(context, $"{at}.{nameof(LessonSectionRequest.Body)}", Messages.Required("nội dung mốc thời gian"));
                    break;
                case LessonSectionType.SOURCE_EXCERPT:
                    if (!hasBody) Fail(context, $"{at}.{nameof(LessonSectionRequest.Body)}", Messages.Required("đoạn trích"));
                    if (string.IsNullOrWhiteSpace(s.SourceCitation))
                        Fail(context, $"{at}.{nameof(LessonSectionRequest.SourceCitation)}", Messages.Required("nguồn của đoạn trích"));
                    break;
                default:
                    if (!hasBody) Fail(context, $"{at}.{nameof(LessonSectionRequest.Body)}", Messages.Required("nội dung phần"));
                    break;
            }
        }
    }

    private static void Fail<T>(ValidationContext<T> context, string property, AppMessage message) =>
        context.AddFailure(new FluentValidation.Results.ValidationFailure(property, message.Text) { ErrorCode = message.Code });
}
