using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Audit;

/// <summary>
/// Bảng <c>audit_logs</c>: nhật ký thao tác quan trọng, CHỈ THÊM — DbContext chặn mọi lệnh sửa, xóa.
/// Không có <c>organization_id</c>: suy ra qua <c>user_id</c> → <c>users.organization_id</c>.
/// Bốn cột <c>created_at</c>, <c>actor_role</c>, <c>action</c>, <c>target_type</c> là NOT NULL; DbContext điền <c>created_at</c>.
/// </summary>
public class AuditLog
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Người thực hiện; NULL khi hệ thống tự làm (IPN, tác vụ nền).</summary>
    public long? UserId { get; set; }

    /// <summary>Bản sao vai trò lúc ghi; SYSTEM khi <see cref="UserId"/> NULL.</summary>
    public ActorRole ActorRole { get; set; }

    public AuditAction Action { get; set; }

    /// <summary>Tên bảng của đối tượng bị tác động (<see cref="AuditTargets"/>).</summary>
    public string TargetType { get; set; } = string.Empty;

    /// <summary>Id trong bảng <see cref="TargetType"/>; không khai báo FK vì trỏ tới nhiều bảng.</summary>
    public long? TargetId { get; set; }

    /// <summary>Chuỗi JSON rút gọn, không chứa mật khẩu, mật khẩu tạm, token, chữ ký.</summary>
    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public string? IpAddress { get; set; }
}

/// <summary>
/// <c>audit_logs.target_type</c>: tên bảng. Thêm học sinh, nhập danh sách và đặt lại mật khẩu nhiều em một lần ghi đối tượng
/// là lớp (<see cref="Classes"/>); thao tác trên một học sinh ghi <see cref="Students"/>.
/// </summary>
public static class AuditTargets
{
    public const string Users = "users";
    public const string Organizations = "organizations";
    public const string Heritages = "heritages";
    public const string Classes = "classes";
    public const string Students = "students";
    public const string Quizzes = "quizzes";
    public const string Questions = "questions";
    public const string Plans = "plans";
    public const string Subscriptions = "subscriptions";
    public const string Invoices = "invoices";
    public const string Payments = "payments";
}
