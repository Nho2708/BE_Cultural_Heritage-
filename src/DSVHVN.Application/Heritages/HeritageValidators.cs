using DSVHVN.Application.Common;
using DSVHVN.Domain.Rules;
using FluentValidation;

namespace DSVHVN.Application.Heritages;

internal static class HeritageValidationExtensions
{
    /// <summary>Gắn mã và câu thông điệp cho luật của thuộc tính kiểu bất kỳ (bản có sẵn chỉ dành cho chuỗi).</summary>
    public static IRuleBuilderOptions<T, TProperty> Apply<T, TProperty>(this IRuleBuilderOptions<T, TProperty> rule, AppMessage message) =>
        rule.WithErrorCode(message.Code).WithMessage(message.Text);

    public static IRuleBuilderOptions<T, string?> OptionalHttpUrl<T>(this IRuleBuilder<T, string?> rule, string label, int max) =>
        rule.Must(v => string.IsNullOrWhiteSpace(v) || (ValidationRules.IsHttpUrl(v) && v.Trim().Length <= max))
            .WithAppMessage(Messages.Invalid(label, $"phải là đường dẫn http hoặc https, tối đa {max} ký tự"));

    public static IRuleBuilderOptions<T, string?> RequiredHttpUrl<T>(this IRuleBuilder<T, string?> rule, string field, string label, int max) =>
        rule.RequiredField(field).OptionalHttpUrl(label, max);

    public static IRuleBuilderOptions<T, int?> YearInRange<T>(this IRuleBuilder<T, int?> rule, string label, Func<int> currentYear) =>
        rule.Must(v => v is null || (v >= HeritageRules.MinYear && v <= currentYear()))
            .Apply(Messages.Invalid(label, $"phải từ {HeritageRules.MinYear} đến năm hiện tại (năm âm là trước Công nguyên)"));

    public static IRuleBuilderOptions<T, short?> MonthInRange<T>(this IRuleBuilder<T, short?> rule, string label) =>
        rule.Must(v => v is null || v is >= 1 and <= 12).Apply(Messages.Invalid(label, "phải từ 1 đến 12"));

    public static IRuleBuilderOptions<T, int?> SortOrderInRange<T>(this IRuleBuilder<T, int?> rule) =>
        rule.Must(v => v is null || v is >= 0 and <= 1_000_000).Apply(Messages.Invalid("Thứ tự", "phải từ 0 đến 1.000.000"));
}

public sealed class HeritageRequestValidator : AbstractValidator<HeritageRequest>
{
    public const int ContentMaxLength = 200_000;

    public HeritageRequestValidator(TimeProvider clock)
    {
        int CurrentYear() => VietnamTime.Today(clock.GetUtcNow()).Year;

        RuleFor(x => x.Name).Cascade(CascadeMode.Stop)
            .RequiredText("tên di sản", "Tên di sản", HeritageRules.NameMaxLength)
            .Must(name => HeritageRules.Slugify(name).Length > 0)
            .WithAppMessage(Messages.Invalid("Tên di sản", "phải có ít nhất một chữ cái hoặc chữ số"));
        RuleFor(x => x.Slug)
            .Must(slug => string.IsNullOrWhiteSpace(slug) || HeritageRules.IsValidSlug(slug.Trim()))
            .WithAppMessage(Messages.Invalid("Đường dẫn",
                $"chỉ gồm chữ thường không dấu, chữ số và dấu gạch ngang giữa các đoạn, tối đa {HeritageRules.SlugMaxLength} ký tự"));
        RuleFor(x => x.HeritageType)
            .Must(t => t is null || Enum.IsDefined(t.Value))
            .Apply(Messages.Invalid("Loại di sản", "chỉ nhận TANGIBLE, INTANGIBLE, NATURAL, MIXED hoặc DOCUMENTARY"));
        RuleFor(x => x.RecognitionLevel)
            .Must(l => l is null || Enum.IsDefined(l.Value))
            .Apply(Messages.Invalid("Cấp xếp hạng", "chỉ nhận UNESCO, NATIONAL_SPECIAL, NATIONAL, PROVINCIAL hoặc NONE"));
        RuleFor(x => x.ShortDescription).OptionalText("Mô tả ngắn", HeritageRules.ShortDescriptionMaxLength);
        RuleFor(x => x.Content).OptionalText("Nội dung", ContentMaxLength);
        RuleFor(x => x.RecognizedYear)
            .Must(year => year is null || (year >= 1 && year <= CurrentYear()))
            .Apply(Messages.Invalid("Năm công nhận", "phải là năm dương lịch không lớn hơn năm hiện tại"));
        RuleFor(x => x.Address).OptionalText("Địa chỉ", HeritageRules.AddressMaxLength);
        RuleFor(x => x.PlaceId).OptionalText("Mã tham chiếu ngoài", HeritageRules.PlaceIdMaxLength);

        RuleFor(x => x.Latitude)
            .Must(v => v is null || HeritageRules.IsLatitude(v.Value))
            .Apply(Messages.Invalid("Vĩ độ", "phải từ 8,0 đến 23,5"));   // HeritageRules.MinLatitude, MaxLatitude; dấu phẩy thập phân của tiếng Việt
        RuleFor(x => x.Longitude)
            .Must(v => v is null || HeritageRules.IsLongitude(v.Value))
            .Apply(Messages.Invalid("Kinh độ", "phải từ 102,0 đến 117,5"));   // HeritageRules.MinLongitude, MaxLongitude
        RuleFor(x => x.Latitude)
            .Must((x, _) => HeritageRules.HasBothOrNoneCoordinates(x.Latitude, x.Longitude))
            .Apply(Messages.Invalid("Tọa độ", "phải nhập đủ cả vĩ độ và kinh độ hoặc bỏ trống cả hai"))
            .When(x => x.Latitude is null || HeritageRules.IsLatitude(x.Latitude.Value));
    }
}

public sealed class TimelineRequestValidator : AbstractValidator<TimelineRequest>
{
    public TimelineRequestValidator(TimeProvider clock)
    {
        int CurrentYear() => VietnamTime.Today(clock.GetUtcNow()).Year;

        RuleFor(x => x.Name).RequiredText("tên giai đoạn", "Tên giai đoạn", HeritageRules.TimelineNameMaxLength);
        RuleFor(x => x.Description).OptionalText("Mô tả", HeritageValidatorsLimits.LongTextMaxLength);
        RuleFor(x => x.StartYear).YearInRange("Năm bắt đầu", CurrentYear);
        RuleFor(x => x.EndYear).YearInRange("Năm kết thúc", CurrentYear);
        RuleFor(x => x.EndYear)
            .Must((x, end) => x.StartYear is null || end is null || end >= x.StartYear)
            .Apply(Messages.Invalid("Năm kết thúc", "không được nhỏ hơn năm bắt đầu"));
        RuleFor(x => x.SortOrder).SortOrderInRange();
    }
}

public sealed class EventRequestValidator : AbstractValidator<EventRequest>
{
    public EventRequestValidator(TimeProvider clock)
    {
        int CurrentYear() => VietnamTime.Today(clock.GetUtcNow()).Year;

        RuleFor(x => x.Name).RequiredText("tên sự kiện", "Tên sự kiện", HeritageRules.EventNameMaxLength);
        RuleFor(x => x.Description).OptionalText("Mô tả", HeritageValidatorsLimits.LongTextMaxLength);

        RuleFor(x => x.StartYear).Cascade(CascadeMode.Stop)
            .Must(v => v is not null).Apply(Messages.Required("năm bắt đầu"))
            .YearInRange("Năm bắt đầu", CurrentYear);
        RuleFor(x => x.StartMonth).MonthInRange("Tháng bắt đầu");
        RuleFor(x => x.StartDay)
            .Must((x, day) => day is null || (x.StartMonth is >= 1 and <= 12 && x.StartYear is not null
                                              && HeritageRules.IsValidDay(x.StartYear.Value, x.StartMonth, day)))
            .Apply(Messages.Invalid("Ngày bắt đầu", "phải là ngày có thật của tháng và chỉ nhập khi đã có tháng"));

        RuleFor(x => x.EndYear).YearInRange("Năm kết thúc", CurrentYear);
        RuleFor(x => x.EndMonth).Cascade(CascadeMode.Stop)
            .MonthInRange("Tháng kết thúc")
            .Must((x, month) => month is null || x.EndYear is not null)
            .Apply(Messages.Invalid("Tháng kết thúc", "chỉ nhập khi đã có năm kết thúc"));
        RuleFor(x => x.EndDay)
            .Must((x, day) => day is null || (x.EndMonth is >= 1 and <= 12 && x.EndYear is not null
                                              && HeritageRules.IsValidDay(x.EndYear.Value, x.EndMonth, day)))
            .Apply(Messages.Invalid("Ngày kết thúc", "phải là ngày có thật của tháng và chỉ nhập khi đã có tháng"));
        RuleFor(x => x.EndYear)
            .Must((x, endYear) => endYear is null || x.StartYear is null
                                  || !HeritageRules.EndsBeforeStart(x.StartYear.Value, x.StartMonth, x.StartDay,
                                      endYear.Value, x.EndMonth, x.EndDay))
            .Apply(Messages.Invalid("Thời điểm kết thúc", "không được trước thời điểm bắt đầu"))
            .When(x => x.StartYear is not null);
        RuleFor(x => x.SortOrder).SortOrderInRange();
    }
}

public sealed class MediaRequestValidator : AbstractValidator<MediaRequest>
{
    public MediaRequestValidator()
    {
        RuleFor(x => x.MediaType).Cascade(CascadeMode.Stop)
            .RequiredField("loại media")
            .Must(t => HeritageRules.MediaTypes.Contains(t!.Trim()))
            .WithAppMessage(Messages.Invalid("Loại media", "chỉ nhận IMAGE, VIDEO hoặc AUDIO"));
        // Đường dẫn http/https (ảnh dán đường dẫn) hoặc đường dẫn tương đối tới tệp đã tải lên kho của nền tảng (/files/...).
        RuleFor(x => x.Url).Cascade(CascadeMode.Stop)
            .RequiredField("đường dẫn")
            .Must(v => v!.Trim().Length <= HeritageRules.UrlMaxLength && (ValidationRules.IsHttpUrl(v) || StoredFileRules.IsStoredPath(v)))
            .WithAppMessage(Messages.Invalid("Đường dẫn",
                $"phải là đường dẫn http hoặc https (hoặc đường dẫn tệp đã tải lên), tối đa {HeritageRules.UrlMaxLength} ký tự"));
        RuleFor(x => x.Caption).OptionalText("Chú thích", HeritageRules.CaptionMaxLength);

        RuleFor(x => x.Author).Cascade(CascadeMode.Stop)
            .RequiredField("tác giả")
            .Must(NoSeparator).WithAppMessage(Messages.Invalid("Tác giả", $"không chứa dấu \"{HeritageRules.CreditSeparator.Trim()}\""))
            .Must((x, _) => CreditFits(x)).WithAppMessage(Messages.Invalid("Ghi công",
                $"tác giả, giấy phép và nguồn ghép lại tối đa {HeritageRules.CreditMaxLength} ký tự"));
        RuleFor(x => x.License).Cascade(CascadeMode.Stop)
            .RequiredField("giấy phép")
            .Must(HeritageRules.IsAllowedLicense).WithAppMessage(Messages.Invalid("Giấy phép",
                "chỉ dùng CC0, CC BY, CC BY-SA, phạm vi công cộng (public domain) hoặc ảnh nhóm tự chụp"))
            .Must(NoSeparator).WithAppMessage(Messages.Invalid("Giấy phép", $"không chứa dấu \"{HeritageRules.CreditSeparator.Trim()}\""));
        RuleFor(x => x.Source).Cascade(CascadeMode.Stop)
            .RequiredField("nguồn ảnh")
            .Must(NoSeparator).WithAppMessage(Messages.Invalid("Nguồn ảnh", $"không chứa dấu \"{HeritageRules.CreditSeparator.Trim()}\""));
        RuleFor(x => x.SortOrder).SortOrderInRange();
    }

    private static bool NoSeparator(string? value) => value is null || !value.Contains(HeritageRules.CreditSeparator.Trim());

    private static bool CreditFits(MediaRequest x) =>
        string.IsNullOrWhiteSpace(x.License) || string.IsNullOrWhiteSpace(x.Source)
        || HeritageRules.ComposeCredit(x.Author!, x.License, x.Source).Length <= HeritageRules.CreditMaxLength;
}

public sealed class ReferenceRequestValidator : AbstractValidator<ReferenceRequest>
{
    public ReferenceRequestValidator()
    {
        RuleFor(x => x.Title).RequiredText("tiêu đề nguồn", "Tiêu đề nguồn", HeritageRules.ReferenceTitleMaxLength);
        RuleFor(x => x.SourceType).Cascade(CascadeMode.Stop)
            .RequiredField("loại nguồn")
            .Must(t => HeritageRules.SourceTypes.Contains(t!.Trim()))
            .WithAppMessage(Messages.Invalid("Loại nguồn", "chỉ nhận GOV_DOC (văn bản nhà nước), WEBSITE hoặc BOOK (sách)"));
        RuleFor(x => x.Url).Cascade(CascadeMode.Stop)
            .OptionalHttpUrl("Đường dẫn", HeritageRules.UrlMaxLength)
            .Must((x, url) => !string.Equals(x.SourceType?.Trim(), HeritageRules.SourceWebsite, StringComparison.Ordinal)
                              || !string.IsNullOrWhiteSpace(url))
            .WithAppMessage(Messages.Required("đường dẫn của nguồn website"));
    }
}

internal static class HeritageValidatorsLimits
{
    /// <summary>Giới hạn an toàn cho cột nvarchar(max) do người dùng nhập.</summary>
    public const int LongTextMaxLength = 100_000;
}
