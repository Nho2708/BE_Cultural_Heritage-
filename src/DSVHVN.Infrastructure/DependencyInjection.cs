using DSVHVN.Application.Common;
using DSVHVN.Infrastructure.Email;
using DSVHVN.Infrastructure.ExternalServices;
using DSVHVN.Infrastructure.Persistence;
using DSVHVN.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();

        // Chuỗi kết nối đọc lúc tạo DbContext (không đọc ngay lúc đăng ký) để cấu hình ghi đè về sau vẫn có hiệu lực.
        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            var connection = sp.GetRequiredService<IConfiguration>().GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(connection))
                throw new InvalidOperationException("Thiếu ConnectionStrings:Default.");
            options.UseSqlServer(connection);
        });
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.Section));

        services.AddSingleton<JwtSigningKeyProvider>();
        services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
        services.AddSingleton<IAuthThrottle, InMemoryAuthThrottle>();
        services.AddEmail(configuration);

        services.AddHttpClient<IGeocodingService, GeocodingService>();
        services.AddHttpClient<IAiServiceClient, AiServiceClient>();
        return services;
    }

    /// <summary>
    /// Thư đi: <c>Email:Mode</c> chọn bản gửi lúc tạo dịch vụ (không đọc ngay lúc đăng ký, như chuỗi kết nối).
    /// <c>Log</c> (mặc định) chỉ ghi ra log; <c>Smtp</c> gửi thật và thiếu cấu hình bắt buộc thì dừng khởi động.
    /// </summary>
    private static void AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AppLinkOptions>().Bind(configuration.GetSection(AppLinkOptions.Section));
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection(EmailOptions.Section)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>());

        services.AddSingleton<IEmailSender>(sp =>
            sp.GetRequiredService<IOptions<EmailOptions>>().Value.Mode == EmailMode.Smtp
                ? ActivatorUtilities.CreateInstance<SmtpEmailSender>(sp)
                : ActivatorUtilities.CreateInstance<LogEmailSender>(sp));
        services.AddScoped<IAccountMailer, AccountMailer>();
    }
}
