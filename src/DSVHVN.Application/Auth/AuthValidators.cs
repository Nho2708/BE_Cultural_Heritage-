using DSVHVN.Application.Common;
using DSVHVN.Domain.Rules;
using FluentValidation;

namespace DSVHVN.Application.Auth;

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.EmailOrUsername).RequiredField("email hoặc tên đăng nhập");
        RuleFor(x => x.Password).RequiredField("mật khẩu");
    }
}

public sealed class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator() => RuleFor(x => x.Email).RequiredEmail();
}

public sealed class PasswordTokenRequestValidator : AbstractValidator<PasswordTokenRequest>
{
    public PasswordTokenRequestValidator() => RuleFor(x => x.Token).RequiredField("mã trong liên kết");
}

public sealed class SetPasswordRequestValidator : AbstractValidator<SetPasswordRequest>
{
    public SetPasswordRequestValidator()
    {
        RuleFor(x => x.Token).RequiredField("mã trong liên kết");
        RuleFor(x => x.NewPassword).Cascade(CascadeMode.Stop)
            .RequiredField("mật khẩu mới")
            .Must(PasswordPolicy.IsSatisfiedBy).WithAppMessage(Messages.PasswordPolicy);
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).RequiredField("mật khẩu hiện tại");
        RuleFor(x => x.NewPassword).Cascade(CascadeMode.Stop)
            .RequiredField("mật khẩu mới")
            .Must(PasswordPolicy.IsSatisfiedBy).WithAppMessage(Messages.PasswordPolicy);
    }
}
