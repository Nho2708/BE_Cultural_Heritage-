using DSVHVN.Application.Ads;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Application.Discovery;
using DSVHVN.Application.Files;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Questions;
using DSVHVN.Application.Recommendations;
using DSVHVN.Application.SchoolInquiries;
using DSVHVN.Application.Speech;
using DSVHVN.Application.Students;
using DSVHVN.Application.Translations;
using DSVHVN.Infrastructure.Ads;
using DSVHVN.Infrastructure.Ai;
using DSVHVN.Infrastructure.Background;
using DSVHVN.Infrastructure.Files;
using DSVHVN.Infrastructure.Email;
using DSVHVN.Infrastructure.Payments;
using DSVHVN.Infrastructure.Persistence;
using DSVHVN.Infrastructure.Security;
using DSVHVN.Infrastructure.Speech;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
        services.AddBilling(configuration);
        services.AddAds(configuration);
        services.AddSpeech(configuration);

        // Liên kết xem trước bản nháp di sản: mã ký bằng khóa của máy chủ, hạn Preview:CodeMinutes (mặc định 30 phút), không có bảng.
        services.AddOptions<PreviewOptions>().Bind(configuration.GetSection(PreviewOptions.Section)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PreviewOptions>, PreviewOptionsValidator>());
        services.AddSingleton<IHeritagePreviewCodec, HeritagePreviewCodec>();

        // Tự đóng bài kiểm tra quá giờ đóng, chạy nền theo chu kỳ của mục Quizzes.
        services.AddOptions<QuizAutoCloseOptions>().Bind(configuration.GetSection(QuizAutoCloseOptions.Section));
        services.AddHostedService<QuizAutoCloseService>();

        // Dọn lịch sử khách tham quan quá 90 ngày, chạy nền hằng ngày theo mục Guests.
        services.AddOptions<GuestCleanupOptions>().Bind(configuration.GetSection(GuestCleanupOptions.Section));
        services.AddHostedService<GuestHistoryCleanupService>();

        // Phiếu đăng nhập của học sinh lấy địa chỉ web học tập và đường dẫn tải app từ mục App.
        services.AddOptions<StudentSheetOptions>().Bind(configuration.GetSection(StudentSheetOptions.Section));
        services.AddSingleton<IStudentListFile, StudentListFileReader>();
        return services;
    }

    /// <summary>
    /// Quảng cáo Google có thưởng: <c>Ads</c> (bật cổng, đơn vị quảng cáo AdMob của Android và iOS, đơn vị Ad Manager của web, địa chỉ danh sách khóa xác minh của Google). Khóa công khai của Google
    /// tải bằng <see cref="HttpClient"/> dùng chung cho cả tiến trình và đệm trong bộ nhớ; mã giao dịch của web ký bằng khóa suy ra từ khóa JWT; bộ đếm tần suất nằm trong bộ nhớ.
    /// Mặc định cổng tắt (không đơn vị quảng cáo nào) nên không cấu hình gì thì tóm tắt và nghe thuyết minh mở thẳng.
    /// </summary>
    private static void AddAds(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AdsOptions>().Bind(configuration.GetSection(AdsOptions.Section));
        services.AddSingleton<IAdMobKeyStore>(sp =>
        {
            var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
            var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            return new GoogleAdMobKeyStore(http, sp.GetRequiredService<IOptions<AdsOptions>>(), sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<GoogleAdMobKeyStore>>());
        });
        services.AddSingleton<IAdRewardTokenCodec, HmacAdRewardTokenCodec>();
        services.AddSingleton<IAdRewardThrottle, InMemoryAdRewardThrottle>();
    }

    /// <summary>
    /// Thanh toán VNPay sandbox: <c>VnPay</c> (mã cửa hàng, khóa bí mật, địa chỉ trang thanh toán, địa chỉ trang trả về) và tác vụ nền gói
    /// (<c>Billing:Maintenance</c>). Thiếu khóa VNPay không làm dừng khởi động: chỉ không tạo được thanh toán và từ chối IPN.
    /// </summary>
    private static void AddBilling(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<VnPayOptions>().Bind(configuration.GetSection(VnPayOptions.Section));
        services.AddSingleton<IVnPayGateway, VnPayGateway>();
        services.AddOptions<BillingMaintenanceOptions>().Bind(configuration.GetSection(BillingMaintenanceOptions.Section));
        services.AddHostedService<BillingMaintenanceWorker>();
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

        // Xếp hạng di sản liên quan cho gợi ý (cùng mục cấu hình Ai); thời gian chờ 2 giây do RelatedRanker đặt, lỗi thì máy chủ tự tính dự phòng.
        services.AddSingleton<IRecommendationAiClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AiOptions>>();
            if (options.Value.Mode != AiMode.Remote) return new SimulatedRecommendationAiClient();

            var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
            var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            return new HttpRecommendationAiClient(http, options);
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

        // Liên hệ của trường (trang Dành cho nhà trường): gửi tới các địa chỉ khai ở SchoolInquiry:Recipients, chưa khai thì tới quản trị hệ thống.
        services.AddOptions<SchoolInquiryOptions>().Bind(configuration.GetSection(SchoolInquiryOptions.Section));
        services.AddScoped<ISchoolInquiryMailer, SchoolInquiryMailer>();
    }

    /// <summary>
    /// Chuyển văn bản thành giọng nói (TTS) qua FPT.AI (giọng Ban Mai): <c>FptAi</c> (ApiKey, Voice, Speed).
    /// </summary>
    private static void AddSpeech(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FptAiOptions>()
            .Bind(configuration.GetSection(FptAiOptions.Section))
            .PostConfigure(opt =>
            {
                var envKey = Environment.GetEnvironmentVariable("FPT_AI_API_KEY")
                             ?? Environment.GetEnvironmentVariable("FptAi__ApiKey");
                if (!string.IsNullOrWhiteSpace(envKey))
                {
                    opt.ApiKey = envKey.Trim();
                }
            });

        services.AddSingleton<ITtsService>(sp =>
        {
            var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            return new FptAiTtsService(
                http,
                sp.GetRequiredService<IOptions<FptAiOptions>>(),
                sp.GetRequiredService<IMemoryCache>(),
                sp.GetRequiredService<ILogger<FptAiTtsService>>());
        });
    }
}

