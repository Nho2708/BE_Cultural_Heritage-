using DSVHVN.Application.Accounts;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using FluentValidation;

namespace DSVHVN.Application.Organizations;

/// <summary>Thông tin trường ADMIN nhập và sửa (màn hình Chi tiết trường).</summary>
public sealed record OrganizationInfoRequest(string? Name, string? Address, string? Phone, string? Email);

/// <summary>Tạo trường mới: thông tin trường + tài khoản quản trị trường đầu tiên.</summary>
public sealed record CreateOrganizationRequest(
    string? Name,
    string? Address,
    string? Phone,
    string? Email,
    NewAccountRequest? OrgAdmin);

/// <summary>Tab Thông tin trường: ORG_ADMIN chỉ sửa địa chỉ, số điện thoại, email liên hệ; tên chỉ đọc.</summary>
public sealed record UpdateOrganizationContactRequest(string? Address, string? Phone, string? Email);

/// <summary>Trạng thái trường trên màn hình Quản lý trường: đang hoạt động hoặc đã ngừng (xóa mềm).</summary>
public enum OrganizationState
{
    ACTIVE,
    DEACTIVATED,
}

public sealed record OrganizationListQuery(string? Keyword, OrganizationState? Status, int? Page, int? PageSize);

/// <summary>Dòng của màn hình Quản lý trường: tên, email, gói hiện tại, trạng thái gói, số giáo viên, ngày tạo, trạng thái.</summary>
public sealed record OrganizationSummaryDto(
    long Id,
    string Name,
    string? Email,
    string? Phone,
    string? PlanName,
    SubscriptionStatus? SubscriptionStatus,
    DateOnly? SubscriptionEndDate,
    bool HasEffectivePlan,
    int TeacherCount,
    OrganizationState Status,
    DateTime? CreatedAt,
    DateTime? DeactivatedAt);

/// <summary>Chi tiết trường (xem/sửa): thông tin trường, quản trị trường, gói hiện tại (chỉ đọc), số giáo viên.</summary>
public sealed record OrganizationDetailDto(
    long Id,
    string Name,
    string? Address,
    string? Phone,
    string? Email,
    OrganizationState Status,
    DateTime? CreatedAt,
    DateTime? UpdatedAt,
    DateTime? DeactivatedAt,
    SubscriptionDto? Subscription,
    TeacherQuotaDto TeacherQuota,
    IReadOnlyList<AccountDto> OrgAdmins);

/// <summary>Thông tin trường của ORG_ADMIN: thông tin trường mình, gói và hạn mức giáo viên.</summary>
public sealed record MyOrganizationDto(
    long Id,
    string Name,
    string? Address,
    string? Phone,
    string? Email,
    SubscriptionDto? Subscription,
    TeacherQuotaDto TeacherQuota);

public sealed class OrganizationInfoRequestValidator : AbstractValidator<OrganizationInfoRequest>
{
    public OrganizationInfoRequestValidator()
    {
        RuleFor(x => x.Name).RequiredText("tên trường", "Tên trường", AccountRules.OrganizationNameMaxLength);
        RuleFor(x => x.Address).OptionalText("Địa chỉ", AccountRules.OrganizationAddressMaxLength);
        RuleFor(x => x.Phone).OptionalPhone();
        RuleFor(x => x.Email).OptionalEmail();
    }
}

public sealed class CreateOrganizationRequestValidator : AbstractValidator<CreateOrganizationRequest>
{
    public CreateOrganizationRequestValidator()
    {
        RuleFor(x => x.Name).RequiredText("tên trường", "Tên trường", AccountRules.OrganizationNameMaxLength);
        RuleFor(x => x.Address).OptionalText("Địa chỉ", AccountRules.OrganizationAddressMaxLength);
        RuleFor(x => x.Phone).OptionalPhone();
        RuleFor(x => x.Email).OptionalEmail();

        var required = Messages.Required("thông tin tài khoản quản trị trường");
        RuleFor(x => x.OrgAdmin).Cascade(CascadeMode.Stop)
            .NotNull().WithErrorCode(required.Code).WithMessage(required.Text)
            .SetValidator(new NewAccountRequestValidator()!);
    }
}

public sealed class UpdateOrganizationContactRequestValidator : AbstractValidator<UpdateOrganizationContactRequest>
{
    public UpdateOrganizationContactRequestValidator()
    {
        RuleFor(x => x.Address).OptionalText("Địa chỉ", AccountRules.OrganizationAddressMaxLength);
        RuleFor(x => x.Phone).OptionalPhone();
        RuleFor(x => x.Email).OptionalEmail();
    }
}
