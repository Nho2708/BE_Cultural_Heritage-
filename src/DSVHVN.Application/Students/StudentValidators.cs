using DSVHVN.Application.Common;
using DSVHVN.Domain.Rules;
using FluentValidation;

namespace DSVHVN.Application.Students;

public sealed class AddStudentRequestValidator : AbstractValidator<AddStudentRequest>
{
    public AddStudentRequestValidator() =>
        RuleFor(x => x.FullName).RequiredText("họ tên", "Họ tên", ClassRules.StudentNameMaxLength);
}

public sealed class RenameStudentRequestValidator : AbstractValidator<RenameStudentRequest>
{
    public RenameStudentRequestValidator() =>
        RuleFor(x => x.FullName).RequiredText("họ tên", "Họ tên", ClassRules.StudentNameMaxLength);
}

public sealed class ResetPasswordsRequestValidator : AbstractValidator<ResetPasswordsRequest>
{
    public ResetPasswordsRequestValidator()
    {
        var required = Messages.Required("ít nhất một học sinh");
        var tooMany = Messages.Invalid("Danh sách học sinh", $"tối đa {ClassRules.MaxActiveStudents} học sinh mỗi lần");
        RuleFor(x => x.StudentIds).Cascade(CascadeMode.Stop)
            .Must(ids => ids is { Count: > 0 }).WithErrorCode(required.Code).WithMessage(required.Text)
            .Must(ids => ids!.Count <= ClassRules.MaxActiveStudents).WithErrorCode(tooMany.Code).WithMessage(tooMany.Text);
    }
}
