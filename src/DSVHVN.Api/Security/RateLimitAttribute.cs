using System.Globalization;
using DSVHVN.Api.Common;
using DSVHVN.Application.Common;
using DSVHVN.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace DSVHVN.Api.Security;

/// <summary>Các giới hạn tần suất đếm theo người dùng hoặc mã thiết bị (ngưỡng trong <see cref="RateLimitOptions"/>, mục <c>RateLimiting</c>).</summary>
public enum RateLimitKind
{
    /// <summary>AI soạn bài học và AI sinh câu hỏi của giáo viên: 10 yêu cầu mỗi phút mỗi giáo viên.</summary>
    TeacherAi,

    /// <summary>AI dịch và tóm tắt di sản của quản trị hệ thống: 5 yêu cầu mỗi phút mỗi người.</summary>
    AdminTranslationAi,

    /// <summary>Đọc tóm tắt di sản của khách: 60 yêu cầu mỗi phút mỗi mã thiết bị.</summary>
    GuestSummary,
}

/// <summary>
/// Giới hạn tần suất theo người dùng hoặc mã thiết bị cho một action, vượt thì 429 theo khuôn phản hồi chung kèm <c>Retry-After</c> (giây). Chạy sau xác thực và phân quyền nên
/// người dùng lấy từ claim đã được đối chiếu CSDL; yêu cầu bị từ chối phân quyền (401, 403) không bị đếm. Đếm trong bộ nhớ máy chủ bằng <see cref="IRateCounter"/>, cửa sổ một phút.
/// Giới hạn theo IP của <c>PublicRateLimit</c> ở cấp controller vẫn áp song song: thuộc tính này chỉ thêm giới hạn theo người dùng hoặc thiết bị.
/// Khóa đếm là mã người dùng hoặc mã thiết bị đã chuẩn hóa; yêu cầu không có mã thiết bị hợp lệ thì đếm theo tài khoản đã đăng nhập (học sinh), không có nữa thì theo IP.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RateLimitAttribute(RateLimitKind kind) : Attribute, IAsyncActionFilter
{
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var counter = http.RequestServices.GetRequiredService<IRateCounter>();
        var limits = http.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;

        switch (kind)
        {
            case RateLimitKind.TeacherAi:
                counter.EnforcePerMinute("teacher-ai:" + UserKey(http), limits.TeacherAiRequestsPerMinute);
                break;
            case RateLimitKind.AdminTranslationAi:
                counter.EnforcePerMinute("admin-translation-ai:" + UserKey(http), limits.AdminTranslationAiRequestsPerMinute);
                break;
            case RateLimitKind.GuestSummary:
                counter.EnforcePerMinute("guest-summary:" + DeviceOrIp(http), limits.GuestSummaryRequestsPerMinute);
                break;
        }
        return next();
    }

    private static string UserKey(HttpContext http) =>
        long.TryParse(http.User.FindFirst(JwtClaimNames.Subject)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id.ToString(CultureInfo.InvariantCulture)
            : "ip:" + Ip(http);

    private static string DeviceOrIp(HttpContext http)
    {
        var key = DSVHVN.Domain.Rules.GuestRules.NormalizeDeviceKey(http.Request.Headers[GuestHeaders.DeviceKey].ToString());
        if (key is not null) return "device:" + key;
        // Học sinh đã đăng nhập không gửi mã thiết bị: đếm theo tài khoản để cả lớp dùng chung một IP không dùng chung một bộ đếm.
        return http.User.FindFirst(JwtClaimNames.Subject) is not null ? "user:" + UserKey(http) : "ip:" + Ip(http);
    }

    private static string Ip(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        return ip is null ? "unknown" : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }
}
