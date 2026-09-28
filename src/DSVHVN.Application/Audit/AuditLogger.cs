using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Application.Audit;

/// <summary>
/// Dịch vụ ghi nhật ký thao tác dùng chung cho mọi chức năng. Bản ghi được thêm vào cùng DbContext với
/// thay đổi nghiệp vụ nên lưu cùng lần SaveChanges (cùng giao dịch). Bảng chỉ thêm: DbContext chặn sửa, xóa.
/// </summary>
public interface IAuditLogger
{
    /// <param name="actor">Người thực hiện; null nghĩa là hệ thống tự làm (<c>actor_role</c> = SYSTEM, không có IP).</param>
    /// <param name="oldValue">Giá trị trước, đổi sang JSON rút gọn. Không truyền mật khẩu, token, chữ ký.</param>
    /// <param name="newValue">Giá trị sau, đổi sang JSON rút gọn.</param>
    void Record(AuditAction action, string targetType, long? targetId, Actor? actor,
        object? oldValue = null, object? newValue = null);
}

public sealed class AuditLogger(IAppDbContext db) : IAuditLogger
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Giữ chữ tiếng Việt đọc được trong CSDL.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Record(AuditAction action, string targetType, long? targetId, Actor? actor,
        object? oldValue = null, object? newValue = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            UserId = actor?.UserId,
            ActorRole = actor?.AuditRole ?? ActorRole.SYSTEM,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            OldValue = ToJson(oldValue),
            NewValue = ToJson(newValue),
            IpAddress = actor?.IpAddress,
        });
    }

    private static string? ToJson(object? value) => value is null ? null : JsonSerializer.Serialize(value, Json);
}

/// <summary>Nhãn tiếng Việt của mã thao tác và vai trò cho màn hình Nhật ký thao tác; giao diện chỉ tiếng Việt.</summary>
public static class AuditLabels
{
    public static string Of(AuditAction action) => action switch
    {
        AuditAction.LOGIN => "Đăng nhập web quản trị",
        AuditAction.LOGIN_LOCKED_OUT => "Tạm khóa đăng nhập do sai 5 lần",
        AuditAction.ORG_CREATED => "Tạo trường",
        AuditAction.ORG_DEACTIVATED => "Ngừng trường",
        AuditAction.ACCOUNT_CREATED => "Tạo tài khoản",
        AuditAction.ACCOUNT_LOCKED => "Khóa tài khoản",
        AuditAction.ACCOUNT_UNLOCKED => "Mở khóa tài khoản",
        AuditAction.ACCOUNT_DELETED => "Xóa tài khoản giáo viên",
        AuditAction.HERITAGE_PUBLISHED => "Xuất bản di sản",
        AuditAction.HERITAGE_ARCHIVED => "Ẩn di sản",
        AuditAction.STUDENT_ACCOUNTS_CREATED => "Thêm học sinh và tạo tài khoản",
        AuditAction.STUDENT_PASSWORD_RESET => "Đặt lại mật khẩu học sinh",
        AuditAction.STUDENT_DEACTIVATED => "Vô hiệu hoặc xóa học sinh",
        AuditAction.STUDENT_REACTIVATED => "Mở lại học sinh",
        AuditAction.QUIZ_PUBLISHED => "Phát hành bài kiểm tra",
        AuditAction.QUIZ_CLOSED => "Đóng bài kiểm tra",
        AuditAction.QUESTION_HIDDEN => "Ẩn câu hỏi",
        AuditAction.PLAN_UPDATED => "Cập nhật gói dịch vụ",
        AuditAction.SUBSCRIPTION_CHANGED => "Đổi gói của trường",
        AuditAction.BILLING_STATUS_CHANGED => "Hệ thống đổi trạng thái gói, hóa đơn, giao dịch",
        AuditAction.IPN_REJECTED => "Từ chối IPN",
        _ => action.ToString(),
    };

    public static string Of(ActorRole role) => role switch
    {
        ActorRole.ADMIN => "Quản trị hệ thống",
        ActorRole.ORG_ADMIN => "Quản trị trường",
        ActorRole.TEACHER => "Giáo viên",
        ActorRole.STUDENT => "Học sinh",
        ActorRole.SYSTEM => "Hệ thống",
        _ => role.ToString(),
    };
}
