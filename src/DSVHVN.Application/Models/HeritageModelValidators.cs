using DSVHVN.Application.Common;
using DSVHVN.Application.Heritages;
using DSVHVN.Domain.Rules;
using System.Linq.Expressions;
using FluentValidation;

namespace DSVHVN.Application.Models;

/// <summary>Luật dùng chung của hai biểu mẫu mô hình 3D: tên, ảnh xem trước, ghi công đủ ba phần, giấy phép mở.</summary>
internal static class HeritageModelValidation
{
    public const string FileUrlLabel = "Đường dẫn tệp mô hình";

    public static IRuleBuilderOptions<T, string?> StoredModelUrl<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(v => v!.Trim().Length <= HeritageModelRules.UrlMaxLength && HeritageModelRules.StoredModelKey(v) is not null)
            .WithAppMessage(Messages.Invalid(FileUrlLabel,
                "phải là đường dẫn tệp .glb đã tải lên kho tệp của nền tảng, không dùng đường dẫn tới trang ngoài"));

    public static void ApplyCommonRules<T>(AbstractValidator<T> validator,
        Expression<Func<T, string?>> titleOf, Expression<Func<T, string?>> posterOf, Expression<Func<T, string?>> authorOf,
        Expression<Func<T, string?>> licenseOf, Expression<Func<T, string?>> licenseUrlOf, Expression<Func<T, string?>> sourceUrlOf,
        Expression<Func<T, int?>> sortOrderOf)
    {
        var author = authorOf.Compile();
        var license = licenseOf.Compile();
        var sourceUrl = sourceUrlOf.Compile();

        validator.RuleFor(titleOf).OptionalText("Tên mô hình", HeritageModelRules.TitleMaxLength);

        validator.RuleFor(posterOf)
            .Must(v => string.IsNullOrWhiteSpace(v)
                       || (v.Trim().Length <= HeritageModelRules.UrlMaxLength && (ValidationRules.IsHttpUrl(v) || StoredFileRules.IsStoredPath(v))))
            .WithAppMessage(Messages.Invalid("Ảnh xem trước",
                $"phải là đường dẫn http hoặc https (hoặc đường dẫn ảnh đã tải lên), tối đa {HeritageModelRules.UrlMaxLength} ký tự"));

        validator.RuleFor(authorOf).Cascade(CascadeMode.Stop)
            .RequiredField("tác giả")
            .Must(v => v!.Trim().Length <= HeritageModelRules.AuthorMaxLength)
            .WithAppMessage(Messages.Invalid("Tác giả", $"tối đa {HeritageModelRules.AuthorMaxLength} ký tự"))
            .Must(NoSeparator).WithAppMessage(Messages.Invalid("Tác giả", $"không chứa dấu \"{HeritageRules.CreditSeparator.Trim()}\""));

        validator.RuleFor(licenseOf).Cascade(CascadeMode.Stop)
            .RequiredField("giấy phép")
            .Must(v => v!.Trim().Length <= HeritageModelRules.LicenseMaxLength)
            .WithAppMessage(Messages.Invalid("Giấy phép", $"tối đa {HeritageModelRules.LicenseMaxLength} ký tự"))
            .Must(HeritageModelRules.IsAllowedLicense).WithAppMessage(Messages.Invalid("Giấy phép",
                "chỉ dùng CC0, CC BY, CC BY-SA, phạm vi công cộng (public domain) hoặc mô hình nhóm tự dựng; không nhận CC BY-NC, CC BY-ND"))
            .Must(NoSeparator).WithAppMessage(Messages.Invalid("Giấy phép", $"không chứa dấu \"{HeritageRules.CreditSeparator.Trim()}\""));

        validator.RuleFor(licenseUrlOf).OptionalHttpUrl("Đường dẫn giấy phép", HeritageModelRules.UrlMaxLength);

        // Đường dẫn nguồn là phần thứ ba của ghi công: bắt buộc, trừ mô hình nhóm tự dựng (không có trang nguồn).
        validator.RuleFor(sourceUrlOf)
            .Must(v => string.IsNullOrWhiteSpace(v)
                       || (ValidationRules.IsHttpUrl(v) && v.Trim().Length <= HeritageModelRules.UrlMaxLength))
            .WithAppMessage(Messages.Invalid("Đường dẫn nguồn", $"phải là đường dẫn http hoặc https, tối đa {HeritageModelRules.UrlMaxLength} ký tự"));
        validator.RuleFor(sourceUrlOf)
            .Must((x, v) => !string.IsNullOrWhiteSpace(v) || HeritageModelRules.IsTeamBuilt(license(x)) || !HeritageModelRules.IsAllowedLicense(license(x)))
            .WithAppMessage(Messages.Required("đường dẫn nguồn (trang nơi tải mô hình; chỉ mô hình nhóm tự dựng được bỏ trống)"));

        validator.RuleFor(authorOf)
            .Must((x, _) => CreditFits(author(x), license(x), sourceUrl(x)))
            .WithAppMessage(Messages.Invalid("Ghi công", $"tác giả, giấy phép và nguồn ghép lại tối đa {HeritageModelRules.CreditMaxLength} ký tự"));

        validator.RuleFor(sortOrderOf)
            .Must(v => v is null || v is >= 0 and <= 1_000_000)
            .Apply(Messages.Invalid("Thứ tự", "phải từ 0 đến 1.000.000"));
    }

    private static bool NoSeparator(string? value) => value is null || !value.Contains(HeritageRules.CreditSeparator.Trim());

    private static bool CreditFits(string? author, string? license, string? sourceUrl) =>
        string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(license)
        || HeritageModelRules.ComposeCredit(author, license, sourceUrl).Length <= HeritageModelRules.CreditMaxLength;
}

public sealed class HeritageModelRequestValidator : AbstractValidator<HeritageModelRequest>
{
    public HeritageModelRequestValidator()
    {
        RuleFor(x => x.FileUrl).Cascade(CascadeMode.Stop)
            .RequiredField("đường dẫn tệp mô hình (.glb đã tải lên kho tệp)")
            .StoredModelUrl();
        HeritageModelValidation.ApplyCommonRules(this, x => x.Title, x => x.PosterUrl, x => x.Author, x => x.License, x => x.LicenseUrl,
            x => x.SourceUrl, x => x.SortOrder);
    }
}

public sealed class UpdateHeritageModelRequestValidator : AbstractValidator<UpdateHeritageModelRequest>
{
    public UpdateHeritageModelRequestValidator()
    {
        RuleFor(x => x.FileUrl).StoredModelUrl().When(x => !string.IsNullOrWhiteSpace(x.FileUrl));
        HeritageModelValidation.ApplyCommonRules(this, x => x.Title, x => x.PosterUrl, x => x.Author, x => x.License, x => x.LicenseUrl,
            x => x.SourceUrl, x => x.SortOrder);
    }
}
