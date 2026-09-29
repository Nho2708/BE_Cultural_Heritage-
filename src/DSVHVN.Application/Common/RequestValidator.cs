using System.Text.RegularExpressions;
using DSVHVN.Domain.Rules;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;

namespace DSVHVN.Application.Common;

/// <summary>
/// Chạy validator FluentValidation của một request và ném <see cref="AppException"/> 400 kèm lỗi theo trường.
/// Mỗi trường chỉ giữ lỗi đầu tiên (hiển thị một dòng chữ đỏ dưới ô nhập).
/// </summary>
public sealed class RequestValidator(IServiceProvider services)
{
    public async Task EnsureValidAsync<T>(T request, CancellationToken cancellationToken)
    {
        var validator = services.GetRequiredService<IValidator<T>>();
        var result = await validator.ValidateAsync(request, cancellationToken);
        if (!result.IsValid) throw ToException(result);
    }

    private static AppException ToException(ValidationResult result)
    {
        var errors = new Dictionary<string, string>();
        foreach (var failure in result.Errors)
            errors.TryAdd(ToCamelPath(failure.PropertyName), failure.ErrorMessage);

        var first = result.Errors[0];
        return new AppException(400, new AppMessage(first.ErrorCode, first.ErrorMessage), errors);
    }

    // "OrgAdmin.Email" → "orgAdmin.email" cho khớp tên trường JSON.
    private static string ToCamelPath(string name) =>
        string.Join('.', name.Split('.').Select(p => p.Length == 0 ? p : char.ToLowerInvariant(p[0]) + p[1..]));
}

/// <summary>Luật dùng lại cho mọi validator, gắn sẵn mã thông điệp.</summary>
public static partial class ValidationRules
{
    public static bool IsEmail(string? value) => value is not null && EmailPattern().IsMatch(value.Trim());

    /// <summary>Số điện thoại: có thể bắt đầu bằng +, gồm chữ số, dấu cách, dấu chấm, gạch ngang; 8-20 ký tự.</summary>
    public static bool IsPhone(string? value) => value is not null && PhonePattern().IsMatch(value.Trim());

    public static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public static IRuleBuilderOptions<T, string?> RequiredField<T>(this IRuleBuilder<T, string?> rule, string field)
    {
        var message = Messages.Required(field);
        return rule.Must(v => !string.IsNullOrWhiteSpace(v)).WithErrorCode(message.Code).WithMessage(message.Text);
    }

    public static IRuleBuilderOptions<T, string?> WithAppMessage<T>(this IRuleBuilderOptions<T, string?> rule, AppMessage message) =>
        rule.WithErrorCode(message.Code).WithMessage(message.Text);

    /// <summary>Chuỗi bắt buộc, sau khi cắt khoảng trắng không dài quá <paramref name="max"/>.</summary>
    /// <remarks>Luật sau bỏ qua giá trị trống để mỗi trường chỉ báo một lỗi (lỗi đầu tiên được giữ).</remarks>
    public static IRuleBuilderOptions<T, string?> RequiredText<T>(this IRuleBuilder<T, string?> rule, string field, string label, int max) =>
        rule.RequiredField(field)
            .Must(v => string.IsNullOrWhiteSpace(v) || v.Trim().Length <= max)
            .WithAppMessage(Messages.Invalid(label, $"tối đa {max} ký tự"));

    /// <summary>Chuỗi không bắt buộc (null hoặc rỗng được), có thì không dài quá <paramref name="max"/>.</summary>
    public static IRuleBuilderOptions<T, string?> OptionalText<T>(this IRuleBuilder<T, string?> rule, string label, int max) =>
        rule.Must(v => string.IsNullOrWhiteSpace(v) || v.Trim().Length <= max)
            .WithAppMessage(Messages.Invalid(label, $"tối đa {max} ký tự"));

    public static IRuleBuilderOptions<T, string?> RequiredEmail<T>(this IRuleBuilder<T, string?> rule) =>
        rule.RequiredField("email")
            .Must(e => string.IsNullOrWhiteSpace(e) || (IsEmail(e) && e.Trim().Length <= AccountRules.EmailMaxLength))
            .WithAppMessage(Messages.Invalid("Email", $"sai định dạng hoặc dài quá {AccountRules.EmailMaxLength} ký tự"));

    public static IRuleBuilderOptions<T, string?> OptionalEmail<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(e => string.IsNullOrWhiteSpace(e) || (IsEmail(e) && e.Trim().Length <= AccountRules.EmailMaxLength))
            .WithAppMessage(Messages.Invalid("Email", $"sai định dạng hoặc dài quá {AccountRules.EmailMaxLength} ký tự"));

    public static IRuleBuilderOptions<T, string?> OptionalPhone<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(p => string.IsNullOrWhiteSpace(p) || IsPhone(p))
            .WithAppMessage(Messages.Invalid("Số điện thoại",
                "chỉ gồm chữ số, dấu cách, dấu chấm, gạch ngang, có thể bắt đầu bằng dấu +, dài 8-20 ký tự"));

    /// <summary>Chuỗi rỗng hoặc toàn khoảng trắng coi như không nhập (lưu NULL).</summary>
    public static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Đủ chặt cho giao diện: một @, tên miền có dấu chấm, không khoảng trắng.
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^(?=.{8,20}$)\+?[0-9][0-9 .\-]*[0-9]$")]
    private static partial Regex PhonePattern();
}
