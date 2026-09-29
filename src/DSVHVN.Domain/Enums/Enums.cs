namespace DSVHVN.Domain.Enums;

// Tên thành viên viết HOA đúng như DBML v2: tên thành viên chính là giá trị lưu trong CSDL (nvarchar + CHECK
// hoặc chuỗi không CHECK) và là giá trị trong JSON của API. Giao diện luôn hiện nhãn tiếng Việt tương ứng.
// Đổi tên một thành viên là đổi dữ liệu: phải kèm migration mới.

/// <summary>enum <c>user_status</c> (<c>users.status</c>): hoạt động, bị khóa, ngừng sử dụng.</summary>
public enum UserStatus
{
    ACTIVE,
    LOCKED,
    INACTIVE,
}

/// <summary>enum <c>subscription_status</c> (<c>subscriptions.status</c>), có PENDING cho đăng ký chờ thanh toán.</summary>
public enum SubscriptionStatus
{
    PENDING,
    ACTIVE,
    EXPIRED,
    CANCELLED,
}

/// <summary><c>roles.code</c>: mã lưu dạng chuỗi, không có CHECK. Mỗi tài khoản đúng một vai trò.</summary>
public enum RoleCode
{
    ADMIN,
    ORG_ADMIN,
    TEACHER,
}

/// <summary><c>audit_logs.actor_role</c>: bản sao <c>roles.code</c> lúc ghi, thêm SYSTEM khi hệ thống tự làm.</summary>
public enum ActorRole
{
    ADMIN,
    ORG_ADMIN,
    TEACHER,
    SYSTEM,
}

/// <summary>
/// <c>audit_logs.action</c>: đúng 18 mã hành động được ghi nhật ký. Cột không có CHECK, danh sách hợp lệ kiểm ở tầng code.
/// Luồng nền tảng ghi 8 mã đầu; các mã còn lại do luồng sau ghi qua cùng <c>IAuditLogger</c>.
/// </summary>
public enum AuditAction
{
    LOGIN,
    LOGIN_LOCKED_OUT,
    ORG_CREATED,
    ORG_DEACTIVATED,
    ACCOUNT_CREATED,
    ACCOUNT_LOCKED,
    ACCOUNT_UNLOCKED,
    ACCOUNT_DELETED,
    HERITAGE_PUBLISHED,
    HERITAGE_ARCHIVED,
    STUDENT_DEACTIVATED,
    QUIZ_PUBLISHED,
    QUIZ_CLOSED,
    QUESTION_HIDDEN,
    PLAN_UPDATED,
    SUBSCRIPTION_CHANGED,
    BILLING_STATUS_CHANGED,
    IPN_REJECTED,
}
