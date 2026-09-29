using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using DSVHVN.Application.Common;

namespace DSVHVN.Api.Common;

/// <summary>
/// Khuôn phản hồi chung: <c>{ success, data, message, errorCode, traceId }</c>.
/// <c>errors</c> chỉ có khi lỗi theo trường (hiển thị dưới ô nhập); <c>traceId</c> là X-Correlation-Id.
/// </summary>
public sealed record ApiResponse<T>(
    bool Success,
    T? Data,
    string? Message,
    string? ErrorCode,
    string? TraceId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string>? Errors = null);

public static class ApiResponse
{
    public static ApiResponse<T> Ok<T>(T? data, string? message, string traceId) =>
        new(true, data, message, null, traceId);

    public static ApiResponse<object> Fail(AppMessage message, string traceId, IReadOnlyDictionary<string, string>? errors = null) =>
        new(false, null, message.Text, message.Code, traceId, errors);
}

/// <summary>Tùy chọn JSON dùng chung cho controller và cho phản hồi viết tay (middleware, sự kiện JWT).</summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        // Giữ nguyên chữ tiếng Việt có dấu trong JSON (vẫn escape ký tự nhạy cảm HTML như < > &).
        options.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
        options.Converters.Add(new JsonStringEnumConverter());
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        return options;
    }

    public static Task WriteFailAsync(HttpContext context, int statusCode, AppMessage message,
        IReadOnlyDictionary<string, string>? errors = null)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(ApiResponse.Fail(message, context.TraceIdentifier, errors), Options,
            contentType: "application/json; charset=utf-8");
    }
}
