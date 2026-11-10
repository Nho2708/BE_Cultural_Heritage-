using DSVHVN.Application.Common;
using DSVHVN.Application.Discovery;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using FluentValidation;

namespace DSVHVN.Application.Curriculum;

// Chủ đề chương trình môn học (bản gọn): giáo viên chọn di sản theo chủ đề của môn đang dạy; quản trị hệ thống quản lý chủ đề và danh sách di sản của từng chủ đề.

/// <summary>Dạng ngắn của chủ đề, gắn vào chi tiết bài học, dòng thư viện bài mẫu và chi tiết di sản của quản trị (chỉ đọc).</summary>
public sealed record CurriculumTopicBriefDto(int Id, string Subject, int Grade, string GroupTitle, string Title);

/// <summary>Chủ đề cho giáo viên chọn: chỉ chủ đề đang dùng; <c>HeritageCount</c> chỉ đếm di sản đang xuất bản.</summary>
public sealed record TeacherCurriculumTopicDto(
    int Id,
    string Subject,
    int Grade,
    string GroupTitle,
    string Title,
    string? Description,
    bool LocalProvince,
    int HeritageCount);

public sealed record TeacherCurriculumTopicListDto(IReadOnlyList<TeacherCurriculumTopicDto> Items);

/// <summary>Di sản của một chủ đề cho giáo viên: chủ đề và thẻ di sản như danh sách công khai (chỉ di sản đang xuất bản).</summary>
public sealed record TeacherCurriculumTopicHeritagesDto(TeacherCurriculumTopicDto Topic, IReadOnlyList<HeritageCardDto> Items);

/// <summary>Dòng của trang Chủ đề chương trình (quản trị hệ thống); <c>HeritageCount</c> đếm mọi di sản chưa xóa.</summary>
public sealed record AdminCurriculumTopicDto(
    int Id,
    string Subject,
    int Grade,
    string GroupTitle,
    string Title,
    string? Description,
    bool LocalProvince,
    int HeritageCount,
    int SortOrder,
    string? SourceUrl,
    bool IsActive);

public sealed record AdminCurriculumTopicListDto(IReadOnlyList<AdminCurriculumTopicDto> Items);

/// <summary>Di sản của chủ đề ở góc nhìn quản trị: kể cả bản nháp và đã ẩn.</summary>
public sealed record AdminCurriculumTopicHeritageDto(long Id, string? Slug, string? Name, string? ProvinceName, ContentStatus? Status);

public sealed record AdminCurriculumTopicHeritagesDto(AdminCurriculumTopicDto Topic, IReadOnlyList<AdminCurriculumTopicHeritageDto> Items);

/// <summary>
/// Thêm hoặc sửa chủ đề: môn, khối 1-12, chủ đề lớn, nội dung, mô tả (không bắt buộc), "Địa phương em", thứ tự trong chương trình, nguồn (không bắt buộc).
/// Độ dài theo cột: môn 100, chủ đề lớn 255, nội dung 255, mô tả 1000, nguồn 500.
/// </summary>
public sealed record CurriculumTopicRequest(
    string? Subject,
    int? Grade,
    string? GroupTitle,
    string? Title,
    string? Description,
    bool? LocalProvince,
    int? SortOrder,
    string? SourceUrl);

/// <summary>Thay toàn bộ danh sách di sản của chủ đề: 0-200 di sản, không trùng, chưa xóa.</summary>
public sealed record SetTopicHeritagesRequest(IReadOnlyList<long>? HeritageIds);

public sealed class CurriculumTopicRequestValidator : AbstractValidator<CurriculumTopicRequest>
{
    public CurriculumTopicRequestValidator()
    {
        RuleFor(x => x.Subject).RequiredText("môn học", "Môn học", CurriculumRules.SubjectMaxLength);

        var gradeRequired = Messages.Required("khối");
        var gradeInvalid = Messages.Invalid("Khối", "phải là số nguyên từ 1 đến 12");
        RuleFor(x => x.Grade).Cascade(CascadeMode.Stop)
            .NotNull().WithErrorCode(gradeRequired.Code).WithMessage(gradeRequired.Text)
            .Must(g => CurriculumRules.IsValidGrade(g!.Value)).WithErrorCode(gradeInvalid.Code).WithMessage(gradeInvalid.Text);

        RuleFor(x => x.GroupTitle).RequiredText("chủ đề lớn", "Chủ đề lớn", CurriculumRules.GroupTitleMaxLength);
        RuleFor(x => x.Title).RequiredText("nội dung chủ đề", "Nội dung chủ đề", CurriculumRules.TitleMaxLength);
        RuleFor(x => x.Description).OptionalText("Mô tả", CurriculumRules.DescriptionMaxLength);
        RuleFor(x => x.SourceUrl).OptionalText("Nguồn", CurriculumRules.SourceUrlMaxLength);

        var localRequired = Messages.Required("thông tin chủ đề Địa phương em");
        RuleFor(x => x.LocalProvince).NotNull().WithErrorCode(localRequired.Code).WithMessage(localRequired.Text);

        var orderRequired = Messages.Required("thứ tự trong chương trình");
        var orderInvalid = Messages.Invalid("Thứ tự", "phải là số nguyên từ 0 đến 1.000.000");
        RuleFor(x => x.SortOrder).Cascade(CascadeMode.Stop)
            .NotNull().WithErrorCode(orderRequired.Code).WithMessage(orderRequired.Text)
            .InclusiveBetween(0, CurriculumRules.MaxSortOrder).WithErrorCode(orderInvalid.Code).WithMessage(orderInvalid.Text);
    }
}

public sealed class SetTopicHeritagesRequestValidator : AbstractValidator<SetTopicHeritagesRequest>
{
    public SetTopicHeritagesRequestValidator()
    {
        var required = Messages.Required("danh sách di sản của chủ đề");
        var tooMany = Messages.Invalid("Danh sách di sản", $"tối đa {CurriculumRules.MaxHeritagesPerTopic} di sản cho một chủ đề");
        var duplicated = Messages.Invalid("Danh sách di sản", "mỗi di sản chỉ được chọn một lần");
        RuleFor(x => x.HeritageIds).Cascade(CascadeMode.Stop)
            .NotNull().WithErrorCode(required.Code).WithMessage(required.Text)
            .Must(ids => ids!.Count <= CurriculumRules.MaxHeritagesPerTopic).WithErrorCode(tooMany.Code).WithMessage(tooMany.Text)
            .Must(ids => ids!.Distinct().Count() == ids!.Count).WithErrorCode(duplicated.Code).WithMessage(duplicated.Text);
    }
}
