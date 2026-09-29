namespace DSVHVN.Application.Common;

/// <summary>Một thông điệp: mã theo nghĩa (trả về ở <c>errorCode</c>) và nội dung tiếng Việt đã điền tham số.</summary>
public sealed record AppMessage(string Code, string Text);

/// <summary>
/// Mã thông điệp trả về trong <c>errorCode</c> của khuôn phản hồi. Giao diện dựa vào mã này để xử lý riêng
/// (vd tài khoản bị chặn thì đăng xuất và hiện lý do), không dựa vào câu chữ.
/// </summary>
public static class MessageCodes
{
    public const string Required = "REQUIRED";
    public const string Invalid = "INVALID";
    public const string PasswordPolicy = "PASSWORD_POLICY";
    public const string Duplicate = "DUPLICATE";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string AccountBlocked = "ACCOUNT_BLOCKED";
    public const string ForgotPasswordAccepted = "FORGOT_PASSWORD_ACCEPTED";
    public const string PasswordLinkInvalid = "PASSWORD_LINK_INVALID";
    public const string PasswordLinkSent = "PASSWORD_LINK_SENT";
    public const string Success = "SUCCESS";
    public const string Forbidden = "FORBIDDEN";
    public const string Blocked = "BLOCKED";
    public const string TeacherLimitReached = "TEACHER_LIMIT_REACHED";
    public const string SystemError = "SYSTEM_ERROR";
}

/// <summary>
/// Thông điệp của nền tảng: đăng nhập, đặt mật khẩu, hồ sơ, quản lý trường và giáo viên.
/// Nội dung chép nguyên danh mục thông điệp đã chốt; phần <c>{…}</c> là tham số. Không tự đặt câu mới ngoài danh mục.
/// Lời với giáo viên và quản trị viên xưng "bạn".
/// </summary>
public static class Messages
{
    /// <summary>Trường bắt buộc bị bỏ trống.</summary>
    public static AppMessage Required(string field) => new(MessageCodes.Required, $"Vui lòng nhập {field}.");

    /// <summary>Giá trị sai định dạng, vượt độ dài.</summary>
    public static AppMessage Invalid(string field, string rule) => new(MessageCodes.Invalid, $"{field} không hợp lệ: {rule}.");

    /// <summary>Mật khẩu không đạt quy tắc 8-64 ký tự, có chữ hoa, chữ thường và chữ số.</summary>
    public static readonly AppMessage PasswordPolicy =
        new(MessageCodes.PasswordPolicy, "Mật khẩu phải dài 8-64 ký tự, có chữ hoa, chữ thường và chữ số.");

    /// <summary>Email hoặc tên đăng nhập đã có tài khoản khác dùng.</summary>
    public static AppMessage Taken(string field, string value) =>
        new(MessageCodes.Duplicate, $"{field} \"{value}\" đã được sử dụng cho một tài khoản khác.");

    /// <summary>Sai thông tin đăng nhập, không cho biết sai ô nào.</summary>
    public static readonly AppMessage InvalidCredentials =
        new(MessageCodes.InvalidCredentials, "Email, tên đăng nhập hoặc mật khẩu không đúng.");

    /// <summary>Tài khoản tạm khóa do sai nhiều lần, bị khóa, ngừng sử dụng, hoặc trường đã ngừng.</summary>
    public static AppMessage CannotSignIn(string reason) =>
        new(MessageCodes.AccountBlocked, $"Tài khoản không đăng nhập được. {reason}");

    /// <summary>Các giá trị <c>{ly_do}</c> của thông điệp không đăng nhập được.</summary>
    public static class SignInBlockedReasons
    {
        public static string TemporaryLock(TimeSpan remaining)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
            return $"Bạn đã nhập sai mật khẩu 5 lần liên tiếp. Vui lòng thử lại sau {minutes} phút.";
        }

        public const string Locked = "Tài khoản đã bị khóa. Vui lòng liên hệ quản trị viên.";
        public const string Inactive = "Tài khoản đã ngừng sử dụng.";
        public const string OrganizationDeactivated = "Trường của bạn đã ngừng sử dụng nền tảng.";
    }

    /// <summary>Luôn trả cùng một câu dù email có thuộc tài khoản nào hay không.</summary>
    public static readonly AppMessage ForgotPasswordAccepted = new(MessageCodes.ForgotPasswordAccepted,
        "Nếu email này thuộc một tài khoản, liên kết đặt lại mật khẩu sẽ được gửi tới hộp thư. Liên kết có hiệu lực 30 phút.");

    /// <summary>Liên kết đặt mật khẩu hết hạn, đã dùng hoặc đã bị thay bằng liên kết mới.</summary>
    public static readonly AppMessage PasswordLinkInvalid =
        new(MessageCodes.PasswordLinkInvalid, "Liên kết đã hết hạn hoặc không còn dùng được. Vui lòng yêu cầu gửi liên kết mới.");

    /// <summary>Đã tạo tài khoản quản trị trường hoặc giáo viên và gửi liên kết đặt mật khẩu lần đầu.</summary>
    public static AppMessage PasswordLinkSent(string email) =>
        new(MessageCodes.PasswordLinkSent, $"Đã gửi email đặt mật khẩu tới {email}. Liên kết có hiệu lực 48 giờ.");

    /// <summary>Thao tác thành công.</summary>
    public static AppMessage Success(string action) => new(MessageCodes.Success, $"{action} thành công.");

    /// <summary>Ngoài phạm vi vai trò; cũng dùng khi chưa đăng nhập hoặc phiên hết hạn.</summary>
    public static readonly AppMessage Forbidden = new(MessageCodes.Forbidden, "Bạn không có quyền thực hiện thao tác này.");

    /// <summary>Thao tác bị quy tắc chặn.</summary>
    public static AppMessage Blocked(string action, string reason) => new(MessageCodes.Blocked, $"Không thể {action}. {reason}");

    /// <summary>
    /// 404: không có đối tượng, kể cả khi đối tượng thuộc trường khác. Dùng khuôn "thao tác bị chặn"
    /// (danh mục không có câu riêng cho 404), không cho biết đối tượng có tồn tại ở trường khác hay không.
    /// </summary>
    public static AppMessage NotFound(string action, string target) => Blocked(action, $"Không tìm thấy {target}.");

    /// <summary>Tạo giáo viên khi trường đã đủ <c>max_teachers</c> của gói.</summary>
    public static AppMessage TeacherLimitReached(int used, int max, string planName) => new(MessageCodes.TeacherLimitReached,
        $"Trường đã dùng {used}/{max} tài khoản giáo viên của gói {planName}. " +
        "Hãy nâng cấp gói hoặc xóa tài khoản không còn dùng để thêm giáo viên.");

    /// <summary>Lỗi hệ thống ngoài dự kiến.</summary>
    public static readonly AppMessage SystemError =
        new(MessageCodes.SystemError, "Đã có lỗi xảy ra. Vui lòng thử lại hoặc liên hệ quản trị viên.");
}
