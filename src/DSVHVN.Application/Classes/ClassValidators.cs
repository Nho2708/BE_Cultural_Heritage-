using DSVHVN.Application.Common;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Rules;
using FluentValidation;

namespace DSVHVN.Application.Classes;

public sealed class ClassRequestValidator : AbstractValidator<ClassRequest>
{
    public ClassRequestValidator()
    {
        RuleFor(x => x.Name).RequiredText("tên lớp", "Tên lớp", ClassRules.NameMaxLength);

        var required = Messages.Required("khối lớp");
        var invalid = Messages.Invalid("Khối lớp", $"phải là số nguyên từ {SchoolClass.MinGrade} đến {SchoolClass.MaxGrade}");
        RuleFor(x => x.Grade).Cascade(CascadeMode.Stop)
            .NotNull().WithErrorCode(required.Code).WithMessage(required.Text)
            .InclusiveBetween((int)SchoolClass.MinGrade, (int)SchoolClass.MaxGrade)
            .WithErrorCode(invalid.Code).WithMessage(invalid.Text);

        RuleFor(x => x.SchoolYear).Cascade(CascadeMode.Stop)
            .RequiredField("năm học")
            .Must(SchoolYearPolicy.IsSatisfiedBy)
            .WithAppMessage(Messages.Invalid("Năm học", "phải đúng dạng YYYY-YYYY gồm hai năm liên tiếp, ví dụ 2026-2027"));
    }
}
