using DSVHVN.Application.Billing;

namespace DSVHVN.Application.Overview;

// Hợp đồng dữ liệu của màn Tổng quan: Tổng quan trường (quản trị trường) và Tổng quan hệ thống (quản trị hệ thống).
// Mọi số liệu tính khi hiển thị từ dữ liệu có sẵn, không có bảng mới. Chỉ có số tổng, không có thông tin từng học sinh.

/// <summary>Loại lời nhắc về gói ở đầu Tổng quan trường.</summary>
public enum PlanNoticeKind
{
    /// <summary>Gói còn hiệu lực và chưa gần hết hạn: không có lời nhắc.</summary>
    NONE,

    /// <summary>Gói hiệu lực còn không quá 14 ngày.</summary>
    EXPIRING_SOON,

    /// <summary>Trường không có gói hiệu lực (hết hạn, bị hủy, chỉ có đăng ký chờ thanh toán hoặc chưa có gói).</summary>
    NO_EFFECTIVE_PLAN,
}

/// <summary>Lời nhắc về gói: <see cref="DaysLeft"/> chỉ có khi gói còn hiệu lực. Chỉ báo cho quản trị trường; học sinh không bị ảnh hưởng.</summary>
public sealed record PlanNoticeDto(PlanNoticeKind Kind, int? DaysLeft, string? Message);

/// <summary>Số đếm theo một cấp học: số lớp, học sinh đang hoạt động, số lượt đã nộp và điểm trung bình (phần trăm tổng điểm; null khi chưa có lượt).</summary>
public sealed record OverviewLevelDto(
    int SchoolLevel,
    string SchoolLevelName,
    int ClassCount,
    int ActiveStudents,
    int FinishedAttempts,
    decimal? AveragePercent);

/// <summary>Một lớp của trường trong tổng quan: chỉ số tổng hợp, không có tên giáo viên hay danh sách học sinh.</summary>
public sealed record OverviewClassDto(
    long ClassId,
    string? ClassName,
    int Grade,
    int SchoolLevel,
    string SchoolLevelName,
    string? SchoolYear,
    int ActiveStudents,
    int FinishedAttempts,
    decimal? AveragePercent);

/// <summary>Học sinh đang hoạt động: tổng, số đã đăng nhập ít nhất một lần và số chưa đăng nhập lần nào (chưa có <c>last_login_at</c>).</summary>
public sealed record OverviewStudentsDto(int Active, int LoggedIn, int NeverLoggedIn);

/// <summary>Bài học chưa xóa của các lớp trong trường: tổng và số đã xuất bản.</summary>
public sealed record OverviewLessonsDto(int Total, int Published);

/// <summary>Bài kiểm tra chưa xóa: số đã phát hành (đang mở hoặc đã đóng) và trong đó số còn đang nhận bài.</summary>
public sealed record OverviewQuizzesDto(int Published, int Open);

/// <summary>
/// Lượt làm bài đã nộp (đã nộp hoặc hết giờ tự nộp) của học sinh đang hoạt động và điểm trung bình theo phần trăm tổng điểm
/// (trung bình điểm từng lượt, bài tổng điểm khác nhau vẫn so được).
/// </summary>
public sealed record OverviewAttemptsDto(int Finished, decimal? AveragePercent);

/// <summary>
/// Tổng quan trường (màn hình Tổng quan của quản trị trường): giáo viên so với hạn mức, lớp theo cấp, học sinh, bài học, bài kiểm tra đã phát hành, lượt nộp, điểm trung bình theo lớp,
/// lượt AI đã dùng so với hạn mức trong kỳ, gói hiện tại và lời nhắc hết hạn.
/// </summary>
public sealed record SchoolOverviewDto(
    long OrganizationId,
    string OrganizationName,
    DateTime GeneratedAt,
    SubscriptionDto? Plan,
    PlanNoticeDto PlanNotice,
    TeacherQuotaDto Teachers,
    int ClassCount,
    IReadOnlyList<OverviewLevelDto> Levels,
    OverviewStudentsDto Students,
    OverviewLessonsDto Lessons,
    OverviewQuizzesDto Quizzes,
    OverviewAttemptsDto Attempts,
    IReadOnlyList<OverviewClassDto> Classes,
    AiQuotaDto Ai);

/// <summary>Các số của trường trong tổng quan hệ thống: tổng, trường đang có gói hiệu lực và trường không có.</summary>
public sealed record SystemSchoolsDto(int Total, int WithEffectivePlan, int WithoutEffectivePlan);

public sealed record SystemTeachersDto(int Total, int Active);

/// <summary>Ngân hàng câu hỏi dùng chung: đã duyệt (đang trong ngân hàng), chờ duyệt (chỉ người tạo thấy) và đã bị quản trị hệ thống ẩn.</summary>
public sealed record SystemQuestionsDto(int Approved, int Pending, int Hidden);

/// <summary>Số lượng nội dung trên toàn hệ thống (của các trường chưa ngừng).</summary>
public sealed record SystemContentDto(
    int PublishedHeritages,
    int Lessons,
    int PublishedLessons,
    int PublishedQuizzes,
    int FinishedAttempts,
    SystemQuestionsDto Questions);

/// <summary>
/// Lượt gọi AI của toàn hệ thống (lượt đang xử lý quá lâu và lượt lỗi không tính): của giáo viên (soạn bài, sinh câu hỏi) và của quản trị hệ thống
/// (dịch, tóm tắt di sản, không tính vào hạn mức của trường nào); kèm số lượt trong tháng hiện tại theo giờ Việt Nam.
/// </summary>
public sealed record SystemAiDto(int Total, int ByTeachers, int ByAdmin, int ThisMonth);

/// <summary>Số trường đang dùng một gói (gói ACTIVE và hôm nay nằm trong ngày bắt đầu và kết thúc), kể cả gói đã ngừng bán.</summary>
public sealed record SystemPlanUsageDto(int PlanId, string? PlanName, decimal? Price, bool IsActive, int ActiveSchools, int ExpiringSoonSchools);

/// <summary>
/// Phần doanh thu của tổng quan hệ thống, tính từ giao dịch VNPay thành công (tổng <c>amount</c> của giao dịch SUCCEEDED theo ngày <c>paid_at</c> giờ Việt Nam;
/// gói Miễn phí không tạo doanh thu): tổng từ trước tới nay, tháng này, hôm nay và số giao dịch thành công. <see cref="Message"/> nhắc doanh thu quảng cáo
/// không nằm ở đây (xem ở bảng điều khiển của Google).
/// </summary>
public sealed record SystemRevenueDto(bool Available, string Message, decimal Total, decimal ThisMonth, decimal Today, int SucceededPayments);

/// <summary>Tổng quan hệ thống (tab Tổng quan của quản trị hệ thống): trường, giáo viên, học sinh, nội dung, lượt AI, số trường theo từng gói, doanh thu từ giao dịch VNPay thành công.</summary>
public sealed record SystemOverviewDto(
    DateTime GeneratedAt,
    SystemSchoolsDto Schools,
    SystemTeachersDto Teachers,
    int ActiveStudents,
    SystemContentDto Content,
    SystemAiDto Ai,
    IReadOnlyList<SystemPlanUsageDto> Plans,
    SystemRevenueDto Revenue);

/// <summary>Một kỳ của biểu đồ theo thời gian (ngày <c>yyyy-MM-dd</c> hoặc tháng <c>yyyy-MM</c>, giờ Việt Nam); <c>Revenue</c> là doanh thu từ giao dịch thành công trả trong kỳ.</summary>
public sealed record SystemTrendPointDto(
    string Period,
    int NewSchools,
    int NewTeachers,
    int NewStudents,
    int LessonsCreated,
    int QuizzesPublished,
    int AttemptsFinished,
    int AiRequests,
    decimal Revenue);

/// <summary>
/// Một giao dịch thanh toán trong danh sách giao dịch của tab Doanh thu (quản trị hệ thống): trường, hóa đơn, gói, cổng, số tiền, trạng thái,
/// thời điểm tạo và thời điểm trả (chỉ giao dịch thành công).
/// </summary>
public sealed record SystemPaymentDto(
    long Id,
    long InvoiceId,
    string? InvoiceNumber,
    long OrganizationId,
    string? OrganizationName,
    string? PlanName,
    string? ProviderCode,
    string? ProviderTransactionId,
    decimal? Amount,
    DSVHVN.Domain.Enums.PaymentStatus? Status,
    DateTime? CreatedAt,
    DateTime? PaidAt);

/// <summary>Lọc danh sách giao dịch: trạng thái, trường, khoảng ngày tạo giao dịch (ngày Việt Nam, gồm cả hai đầu), trang.</summary>
public sealed record SystemPaymentListQuery(
    DSVHVN.Domain.Enums.PaymentStatus? Status, long? OrganizationId, DateOnly? From, DateOnly? To, int? Page, int? PageSize);

/// <summary>Biểu đồ theo thời gian: mỗi kỳ trong khoảng đều có một điểm (kể cả kỳ không có gì), kèm tổng cả khoảng.</summary>
public sealed record SystemTrendDto(
    DateOnly From,
    DateOnly To,
    DSVHVN.Domain.Enums.TrendGranularity Granularity,
    IReadOnlyList<SystemTrendPointDto> Points,
    SystemTrendPointDto Totals);
