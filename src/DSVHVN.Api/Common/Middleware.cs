using System.Text.RegularExpressions;
using DSVHVN.Application.Common;

namespace DSVHVN.Api.Common;

/// <summary>
/// Mỗi yêu cầu có X-Correlation-Id (nhận từ client nếu hợp lệ, không thì sinh mới),
/// trả lại trong header, ghi vào scope log và làm <c>traceId</c> của khuôn phản hồi.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var id = SafeId().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");
        context.TraceIdentifier = id;
        context.Response.Headers[HeaderName] = id;

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
            await next(context);
    }

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex SafeId();
}

/// <summary>Một cửa đổi ngoại lệ thành khuôn phản hồi chung; lỗi ngoài dự kiến trả thông điệp lỗi hệ thống, không lộ stack trace.</summary>
public sealed class ExceptionEnvelopeMiddleware(RequestDelegate next, ILogger<ExceptionEnvelopeMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (AppException ex) when (!context.Response.HasStarted)
        {
            logger.LogInformation("{Status} {Code} {Path}", ex.StatusCode, ex.AppMessage.Code, LoggablePath(context.Request.Path));
            context.Response.Clear();
            await ApiJson.WriteFailAsync(context, ex.StatusCode, ex.AppMessage, ex.Errors);
        }
        catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            // Tệp tải lên vượt giới hạn của yêu cầu (413) hoặc yêu cầu multipart hỏng: báo như dữ liệu không hợp lệ, không phải lỗi hệ thống.
            logger.LogInformation("{Status} {Path}", ex.StatusCode, LoggablePath(context.Request.Path));
            context.Response.Clear();
            var tooLarge = ex.StatusCode == StatusCodes.Status413PayloadTooLarge;
            var message = Messages.Invalid("Tệp tải lên", tooLarge ? "vượt quá dung lượng cho phép" : "yêu cầu không đọc được");
            await ApiJson.WriteFailAsync(context, tooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest, message,
                new Dictionary<string, string> { ["file"] = message.Text });
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client đã ngắt kết nối: không còn ai nhận phản hồi.
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            logger.LogError(ex, "Lỗi ngoài dự kiến ở {Path}", context.Request.Path);
            context.Response.Clear();
            await ApiJson.WriteFailAsync(context, StatusCodes.Status500InternalServerError, Messages.SystemError);
        }
    }

    /// <summary>
    /// Đường dẫn ghi log. Mã bài học nằm trong đường dẫn mở bài của học sinh; mã đúng của lớp khác mà học sinh gõ nhầm không được lộ qua log nên
    /// phần mã được che.
    /// </summary>
    public static string LoggablePath(PathString path) =>
        path.StartsWithSegments("/api/v1/student/lessons") ? "/api/v1/student/lessons/{code}" : path.Value ?? string.Empty;
}

/// <summary>Header bảo mật cơ bản cho API JSON.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        return next(context);
    }
}
