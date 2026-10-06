using DSVHVN.Application.Accounts;
using DSVHVN.Application.Audit;
using DSVHVN.Application.Auth;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Classes;
using DSVHVN.Application.Common;
using DSVHVN.Application.Discovery;
using DSVHVN.Application.Files;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Organizations;
using DSVHVN.Application.Profile;
using DSVHVN.Application.Questions;
using DSVHVN.Application.StudentAuth;
using DSVHVN.Application.Students;
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
        services.AddScoped<HeritageService>();
        services.AddScoped<TimelineService>();
        services.AddScoped<HeritageMediaService>();
        services.AddScoped<ModelFileService>();
        services.AddScoped<HeritageReferenceService>();
        services.AddScoped<DiscoveryService>();
        services.AddScoped<HeritagePreviewService>();
        services.AddSingleton<IStudentCredentialGenerator, RandomStudentCredentialGenerator>();
        services.AddScoped<ClassService>();
        services.AddScoped<SchoolSummaryService>();
        services.AddScoped<StudentAccountFactory>();
        services.AddScoped<LoginSheetBuilder>();
        services.AddScoped<StudentService>();
        services.AddScoped<StudentImportService>();
        services.AddScoped<StudentAuthService>();
        services.AddSingleton<ILessonCodeGenerator, RandomLessonCodeGenerator>();
        services.AddScoped<LessonMaterialService>();
        services.AddScoped<LessonDetailBuilder>();
        services.AddScoped<LessonService>();
        services.AddScoped<AiHeritageInputBuilder>();
        services.AddScoped<LessonAiService>();
        services.AddScoped<QuestionPresenter>();
        services.AddScoped<QuestionService>();
        services.AddScoped<QuestionAiService>();
        services.AddScoped<AdminQuestionService>();
        services.AddScoped<StudentLessonService>();
        return services;
    }
}
