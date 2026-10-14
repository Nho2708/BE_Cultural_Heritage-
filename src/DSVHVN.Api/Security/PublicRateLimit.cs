using System.Threading.RateLimiting;
using DSVHVN.Api.Common;
using DSVHVN.Application.Common;
using Microsoft.AspNetCore.RateLimiting;

namespace DSVHVN.Api.Security;

/// <summary>
/// Giới hạn tần suất của API công khai (khám phá di sản, không cần đăng nhập): mặc định 300 yêu cầu mỗi phút mỗi địa chỉ IP,
/// cửa sổ trượt, đếm trong bộ nhớ máy chủ (khởi động lại thì về 0, không có bảng lưu). Vượt thì 429 kèm <c>Retry-After</c> (giây)
/// và khuôn phản hồi chung. Đổi ngưỡng bằng cấu hình <c>RateLimiting:PublicRequestsPerMinute</c>. Một lớp học dùng chung IP vẫn
/// dư sức vì một trang chỉ vài yêu cầu. Chạy sau proxy ngược thì phải cấu hình chuyển tiếp địa chỉ client, không thì mọi người chung một IP.
/// </summary>
public static class PublicRateLimit
{
    public const string PolicyName = "public";
    public const string ConfigKey = "RateLimiting:PublicRequestsPerMinute";
    public const int DefaultRequestsPerMinute = 300;

    /// <summary>Chính sách riêng của biểu mẫu liên hệ của trường: mặc định 5 lần mỗi 10 phút mỗi IP (cửa sổ cố định).</summary>
    public const string SchoolInquiryPolicyName = "school-inquiry";
    public const string SchoolInquiryPermitsKey = "RateLimiting:SchoolInquiryPermits";
    public const string SchoolInquiryWindowMinutesKey = "RateLimiting:SchoolInquiryWindowMinutes";
    public const int DefaultSchoolInquiryPermits = 5;
    public const int DefaultSchoolInquiryWindowMinutes = 10;

    public static IServiceCollection AddPublicRateLimit(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(PolicyName, context =>
            {
                // Đọc cấu hình lúc có yêu cầu (không đọc lúc đăng ký) để ghi đè cấu hình về sau vẫn có hiệu lực.
                var limit = context.RequestServices.GetRequiredService<IConfiguration>()
                    .GetValue(ConfigKey, DefaultRequestsPerMinute);
                return RateLimitPartition.GetSlidingWindowLimiter(ClientKey(context), _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, limit),
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
            options.AddPolicy(SchoolInquiryPolicyName, context =>
            {
                var config = context.RequestServices.GetRequiredService<IConfiguration>();
                var permits = config.GetValue(SchoolInquiryPermitsKey, DefaultSchoolInquiryPermits);
                var minutes = config.GetValue(SchoolInquiryWindowMinutesKey, DefaultSchoolInquiryWindowMinutes);
                return RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, permits),
                    Window = TimeSpan.FromMinutes(Math.Max(1, minutes)),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
            options.OnRejected = async (context, ct) =>
            {
                var wait = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : TimeSpan.FromSeconds(60);
                var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await ApiJson.WriteFailAsync(context.HttpContext, StatusCodes.Status429TooManyRequests, Messages.TooManyRequests(seconds));
            };
        });
        return services;
    }

    private static string ClientKey(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        return ip is null ? "unknown" : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }
}
