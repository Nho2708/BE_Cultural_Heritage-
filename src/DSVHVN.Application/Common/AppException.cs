namespace DSVHVN.Application.Common;

/// <summary>
/// Lỗi nghiệp vụ có chủ đích. Middleware ở tầng Api đổi thành khuôn phản hồi
/// <c>{ success, data, message, errorCode, traceId, errors }</c> với mã HTTP theo quy ước chung của API.
/// </summary>
public class AppException : Exception
{
    public AppException(int statusCode, AppMessage appMessage, IReadOnlyDictionary<string, string>? errors = null)
        : base(appMessage.Text)
    {
        StatusCode = statusCode;
        AppMessage = appMessage;
        Errors = errors;
    }

    public int StatusCode { get; }
    public AppMessage AppMessage { get; }

    /// <summary>Lỗi theo trường (tên trường camelCase → câu hiển thị dưới ô nhập).</summary>
    public IReadOnlyDictionary<string, string>? Errors { get; }

    /// <summary>400: dữ liệu vào không hợp lệ.</summary>
    public static AppException Validation(string field, AppMessage message) =>
        new(400, message, new Dictionary<string, string> { [field] = message.Text });

    /// <summary>401: chưa xác thực hoặc sai thông tin đăng nhập.</summary>
    public static AppException Unauthorized(AppMessage message) => new(401, message);

    /// <summary>403: vượt quyền hoặc tài khoản không đăng nhập được.</summary>
    public static AppException Forbidden(AppMessage message) => new(403, message);

    /// <summary>404: không có đối tượng, kể cả khi đối tượng thuộc trường khác.</summary>
    public static AppException NotFound(AppMessage message) => new(404, message);

    /// <summary>409: trùng dữ liệu (email, tên đăng nhập).</summary>
    public static AppException Conflict(string field, AppMessage message) =>
        new(409, message, new Dictionary<string, string> { [field] = message.Text });

    /// <summary>422: vi phạm business rule.</summary>
    public static AppException BusinessRule(AppMessage message) => new(422, message);
}
