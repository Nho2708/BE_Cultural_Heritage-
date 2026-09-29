using DSVHVN.Application.Accounts;
using DSVHVN.Application.Audit;
using DSVHVN.Application.Auth;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Application.Organizations;
using DSVHVN.Application.Profile;
using DSVHVN.Application.Teachers;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace DSVHVN.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<LoginRequestValidator>(ServiceLifetime.Singleton);
        services.AddScoped<RequestValidator>();
        services.AddScoped<IAuditLogger, AuditLogger>();
        services.AddScoped<AuditLogService>();
        services.AddScoped<PasswordTokenService>();
        services.AddScoped<PlanGuard>();
        services.AddScoped<AccountProvisioner>();
        services.AddScoped<AccountActions>();
        services.AddScoped<AuthService>();
        services.AddScoped<ProfileService>();
        services.AddScoped<OrganizationService>();
        services.AddScoped<MyOrganizationService>();
        services.AddScoped<TeacherService>();
        return services;
    }
}
