using DSVHVN.Application.Common;
using DSVHVN.Infrastructure.Email;
using DSVHVN.Infrastructure.Persistence;
using DSVHVN.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        services.AddOptions<AppLinkOptions>().Bind(configuration.GetSection(AppLinkOptions.Section));

        services.AddSingleton<JwtSigningKeyProvider>();
        services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
        services.AddSingleton<IAuthThrottle, InMemoryAuthThrottle>();
        services.AddSingleton<IEmailSender, ConsoleEmailSender>();
        services.AddScoped<IAccountMailer, AccountMailer>();
        return services;
    }
}
