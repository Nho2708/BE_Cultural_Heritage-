using DSVHVN.Domain.Common;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Audit;

/// <summary>
/// Bảng <c>audit_logs</c>: nhật ký thao tác quản trị, CHỈ THÊM — DbContext chặn mọi lệnh sửa, xóa.
/// Không có <c>organization_id</c>: suy ra qua <c>user_id</c> → <c>users.organization_id</c>.
/// </summary>
public class AuditLog : IHasCreatedAt
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

    /// <summary>Chuỗi JSON rút gọn, không chứa mật khẩu, token, chữ ký.</summary>
    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public string? IpAddress { get; set; }
}

/// <summary><c>audit_logs.target_type</c>: tên bảng. Luồng nền tảng dùng 3 giá trị đầu.</summary>
public static class AuditTargets
{
    public const string Users = "users";
    public const string Organizations = "organizations";
    public const string Subscriptions = "subscriptions";
    public const string Heritages = "heritages";
    public const string Students = "students";
    public const string Quizzes = "quizzes";
    public const string Questions = "questions";
    public const string Plans = "plans";
    public const string Invoices = "invoices";
    public const string Payments = "payments";
}
