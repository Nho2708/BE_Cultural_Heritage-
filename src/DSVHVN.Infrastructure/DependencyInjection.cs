using DSVHVN.Application.Common;
using DSVHVN.Application.Discovery;
using DSVHVN.Application.Files;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Questions;
using DSVHVN.Application.Students;
using DSVHVN.Application.Translations;
using DSVHVN.Infrastructure.Ai;
using DSVHVN.Infrastructure.Files;
using DSVHVN.Infrastructure.Email;
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
        services.AddSingleton<ILessonCodeThrottle, InMemoryLessonCodeThrottle>();
        services.AddEmail(configuration);
        services.AddLessonAi(configuration);
        services.AddFileStorage(configuration);

        // Liên kết xem trước bản nháp di sản: mã ký bằng khóa của máy chủ, hạn Preview:CodeMinutes (mặc định 30 phút), không có bảng.
        services.AddOptions<PreviewOptions>().Bind(configuration.GetSection(PreviewOptions.Section)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PreviewOptions>, PreviewOptionsValidator>());
        services.AddSingleton<IHeritagePreviewCodec, HeritagePreviewCodec>();

        // Phiếu đăng nhập của học sinh lấy địa chỉ web học tập và đường dẫn tải app từ mục App.
        services.AddOptions<StudentSheetOptions>().Bind(configuration.GetSection(StudentSheetOptions.Section));
        services.AddSingleton<IStudentListFile, StudentListFileReader>();
        return services;
    }

    /// <summary>
    /// Kho tệp (ảnh di sản, mô hình 3D): <c>Storage:Provider</c> chọn nhà cung cấp lúc tạo dịch vụ. <c>Local</c> (mặc định) lưu trên đĩa máy chủ ở
    /// <c>Storage:Local:RootPath</c>; <c>Cloudinary</c> (triển khai) lưu trên Cloudinary và thiếu <c>Storage:Cloudinary:CloudName</c>, <c>ApiKey</c>, <c>ApiSecret</c> thì dừng khởi động
    /// (không đọc ngay lúc đăng ký, nên cấu hình ghi đè về sau vẫn có hiệu lực). Khóa chỉ ở User Secrets hoặc biến môi trường.
    /// </summary>
    private static void AddFileStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.Section)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>());
        services.AddSingleton<IFileStorage>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<StorageOptions>>();
            return options.Value.Provider == StorageProvider.Cloudinary
                ? new CloudinaryFileStorage(options)
                : ActivatorUtilities.CreateInstance<LocalFileStorage>(sp);
        });
        services.AddSingleton<IImageProcessor, SkiaImageProcessor>();
    }

    /// <summary>
    /// Dịch vụ AI soạn nội dung bài học, sinh câu hỏi, dịch và tóm tắt di sản: <c>Ai:Mode</c> chọn bản lúc tạo dịch vụ (không đọc ngay lúc đăng ký). <c>Simulated</c> (mặc định) là bản giả lập
    /// không gọi mạng; <c>Remote</c> gọi Dịch vụ AI nội bộ theo hợp đồng và thiếu <c>BaseUrl</c>, <c>InternalKey</c> thì dừng khởi động.
    /// </summary>
    private static void AddLessonAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AiOptions>().Bind(configuration.GetSection(AiOptions.Section)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AiOptions>, AiOptionsValidator>());

        services.AddSingleton<ILessonAiClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AiOptions>>();
            if (options.Value.Mode != AiMode.Remote) return new SimulatedLessonAiClient();

            // Một HttpClient dùng chung cho cả tiến trình; thời gian chờ từng lần gọi do LessonAiService đặt qua CancellationToken.
            var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
            var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            return new HttpLessonAiClient(http, options);
        });

        // Sinh câu hỏi dùng cùng mục cấu hình Ai (cùng chế độ, địa chỉ và khóa nội bộ) với soạn nội dung bài học.
        services.AddSingleton<IQuestionAiClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AiOptions>>();
            if (options.Value.Mode != AiMode.Remote) return new SimulatedQuestionAiClient();

            var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
            var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            return new HttpQuestionAiClient(http, options);
        });

        // Dịch và tóm tắt di sản (quản trị hệ thống) dùng cùng mục cấu hình Ai; thời gian chờ mỗi lần riêng (Ai:HeritageAttemptTimeoutSeconds, mặc định 90 giây).
        services.AddSingleton<IHeritageAiClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AiOptions>>();
            if (options.Value.Mode != AiMode.Remote) return new SimulatedHeritageAiClient();

            var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
            var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            return new HttpHeritageAiClient(http, options);
        });
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
