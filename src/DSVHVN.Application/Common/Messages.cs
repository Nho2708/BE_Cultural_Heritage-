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
    public const string SamePassword = "SAME_PASSWORD";
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
    public const string NotReady = "NOT_READY";
    public const string SystemError = "SYSTEM_ERROR";
    public const string TooManyRequests = "TOO_MANY_REQUESTS";
    public const string StudentInvalidCredentials = "STUDENT_INVALID_CREDENTIALS";
    public const string StudentAccountBlocked = "STUDENT_ACCOUNT_BLOCKED";
    public const string PasswordChangeRequired = "PASSWORD_CHANGE_REQUIRED";
    public const string PasswordMismatch = "PASSWORD_MISMATCH";
    public const string StudentsAdded = "STUDENTS_ADDED";
    public const string ClassLevelChangeNeedsConfirmation = "CLASS_LEVEL_CHANGE_NEEDS_CONFIRMATION";
    public const string LessonPublished = "LESSON_PUBLISHED";
    public const string LessonDuplicated = "LESSON_DUPLICATED";
    public const string LessonCodeInvalid = "LESSON_CODE_INVALID";
    public const string LessonCodeBlocked = "LESSON_CODE_BLOCKED";
    public const string PlanRequired = "PLAN_REQUIRED";
    public const string AiQuotaReached = "AI_QUOTA_REACHED";
    public const string AiUnavailable = "AI_UNAVAILABLE";
    public const string AiDraftReady = "AI_DRAFT_READY";
    public const string StorageUnavailable = "STORAGE_UNAVAILABLE";
    public const string PreviewInvalid = "PREVIEW_INVALID";
    public const string SummaryUnavailable = "SUMMARY_UNAVAILABLE";
    public const string TranslationGenerated = "TRANSLATION_GENERATED";
    public const string TranslationApproved = "TRANSLATION_APPROVED";
    public const string TranslationRejected = "TRANSLATION_REJECTED";
    public const string TranslationBusy = "TRANSLATION_BUSY";
    public const string ModelUnavailable = "MODEL_UNAVAILABLE";
    public const string QuizPublished = "QUIZ_PUBLISHED";
    public const string QuizClosed = "QUIZ_CLOSED";
    public const string QuizDuplicated = "QUIZ_DUPLICATED";
    public const string QuestionLevelMismatch = "QUESTION_LEVEL_MISMATCH";
    public const string QuizNotOpen = "QUIZ_NOT_OPEN";
    public const string QuizEnded = "QUIZ_ENDED";
    public const string AttemptFinished = "ATTEMPT_FINISHED";
    public const string AttemptInProgress = "ATTEMPT_IN_PROGRESS";
    public const string GuestOnly = "GUEST_ONLY";
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

    /// <summary>Tự đổi mật khẩu: mật khẩu mới trùng mật khẩu hiện tại.</summary>
    public static readonly AppMessage SamePassword = new(MessageCodes.SamePassword, "Mật khẩu mới phải khác mật khẩu cũ.");

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

    /// <summary>Giá trị trùng với dữ liệu đã có (vd đường dẫn di sản), báo dưới ô nhập kèm mã trùng.</summary>
    public static AppMessage Duplicate(string field, string rule) => new(MessageCodes.Duplicate, $"{field} không hợp lệ: {rule}.");

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

    /// <summary>Xuất bản di sản (hoặc lưu di sản đang xuất bản) khi chưa đủ điều kiện: liệt kê phần còn thiếu.</summary>
    public static AppMessage NotReady(string action, IEnumerable<string> missing) =>
        new(MessageCodes.NotReady, $"Chưa đủ điều kiện {action}. Còn thiếu: {string.Join(", ", missing)}.");

    /// <summary>Tạo giáo viên khi trường đã đủ <c>max_teachers</c> của gói.</summary>
    public static AppMessage TeacherLimitReached(int used, int max, string planName) => new(MessageCodes.TeacherLimitReached,
        $"Trường đã dùng {used}/{max} tài khoản giáo viên của gói {planName}. " +
        "Hãy nâng cấp gói hoặc xóa tài khoản không còn dùng để thêm giáo viên.");

    /// <summary>
    /// 429: vượt giới hạn yêu cầu của API công khai (300 yêu cầu mỗi phút mỗi địa chỉ IP). Danh mục thông điệp chưa có câu riêng;
    /// câu này không xưng hô vì người gọi có thể là khách hoặc học sinh.
    /// </summary>
    public static AppMessage TooManyRequests(int seconds) =>
        new(MessageCodes.TooManyRequests, $"Có quá nhiều yêu cầu gửi tới trong thời gian ngắn. Vui lòng thử lại sau {Math.Max(1, seconds)} giây.");

    // Lời với học sinh xưng "em", cùng một bộ câu chữ cho cả ba cấp học.

    /// <summary>Sai mã học sinh hoặc mật khẩu, không cho biết sai ô nào (kể cả khi người lớn đăng nhập ở khu học).</summary>
    public static readonly AppMessage StudentInvalidCredentials = new(MessageCodes.StudentInvalidCredentials,
        "Mã học sinh hoặc mật khẩu chưa đúng. Em hãy kiểm tra lại phiếu đăng nhập.");

    /// <summary>Học sinh không đăng nhập được: tạm khóa do sai nhiều lần, bị vô hiệu, lớp đã xóa hoặc trường đã ngừng.</summary>
    public static AppMessage StudentCannotSignIn(string reason) =>
        new(MessageCodes.StudentAccountBlocked, $"Tài khoản của em tạm thời chưa đăng nhập được. {reason}");

    /// <summary>Các giá trị <c>{ly_do}</c> của thông điệp học sinh không đăng nhập được.</summary>
    public static class StudentBlockedReasons
    {
        public static string TemporaryLock(TimeSpan remaining)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
            return $"Em đã nhập sai nhiều lần. Em hãy thử lại sau {minutes} phút hoặc nhờ thầy cô đặt lại mật khẩu.";
        }

        /// <summary>Vô hiệu, lớp đã xóa, trường đã ngừng, tài khoản không hoạt động.</summary>
        public const string AskTeacher = "Em hãy báo thầy cô phụ trách lớp.";
    }

    /// <summary>Dòng nhắc cố định trên màn hình đăng nhập của học sinh; không có liên kết quên mật khẩu qua email.</summary>
    public const string StudentForgotPasswordHint = "Quên mật khẩu? Em hãy nhờ thầy cô đặt lại.";

    /// <summary>Tài khoản đang bị buộc đổi mật khẩu chỉ được đổi mật khẩu cho tới khi đổi xong.</summary>
    public static readonly AppMessage PasswordChangeRequired = new(MessageCodes.PasswordChangeRequired,
        "Em hãy đặt mật khẩu mới của riêng em để bắt đầu học. Mật khẩu cần 8-64 ký tự, có chữ hoa, chữ thường và chữ số.");

    /// <summary>Hai lần nhập mật khẩu mới không giống nhau.</summary>
    public static readonly AppMessage PasswordMismatch =
        new(MessageCodes.PasswordMismatch, "Hai lần nhập mật khẩu chưa giống nhau.");

    /// <summary>Kết quả nhập danh sách học sinh (bước xem trước và sau khi lưu).</summary>
    public static AppMessage StudentsAdded(int added, string className, IReadOnlyCollection<string> skipped) =>
        new(MessageCodes.StudentsAdded, skipped.Count == 0
            ? $"Đã thêm {added} học sinh vào lớp {className}."
            : $"Đã thêm {added} học sinh vào lớp {className}. {skipped.Count} dòng bị bỏ qua: {string.Join("; ", skipped)}.");

    /// <summary>Sửa khối lớp sang cấp khác khi lớp đã có bài học: cần giáo viên xác nhận.</summary>
    public static AppMessage ClassLevelChangeNeedsConfirmation(string className, int oldLevel, int newLevel) =>
        new(MessageCodes.ClassLevelChangeNeedsConfirmation,
            $"Lớp {className} sẽ chuyển từ cấp {oldLevel} sang cấp {newLevel}. Giao diện học của học sinh đổi theo cấp mới; " +
            "bài học và bài kiểm tra đã có giữ nguyên nội dung. Bạn có chắc muốn đổi?");

    // ---- Bài học cho lớp theo cấp ----

    /// <summary>Trường chưa có gói hiệu lực: chặn tạo, nhân bản bài học và gọi AI (xem, sửa, xuất bản, lưu trữ bài đã có vẫn được).</summary>
    public static AppMessage PlanRequired(string action) => new(MessageCodes.PlanRequired,
        $"Không thể {action}. Gói dịch vụ của trường đã hết hiệu lực hoặc chưa có gói. Học sinh vẫn học bài và làm bài kiểm tra đã phát hành bình thường.");

    /// <summary>Xuất bản bài học lần đầu: hiện mã bài học để đưa cho lớp.</summary>
    public static AppMessage LessonPublished(string code, string className) => new(MessageCodes.LessonPublished,
        $"Đã xuất bản bài học. Mã bài học: {code}. Hãy đưa mã hoặc đường dẫn cho học sinh lớp {className}.");

    /// <summary>
    /// Nhân bản bài học sang lớp khác xong. Khi chép kèm bài kiểm tra thì nhắc đã chép mấy bài (thành bản nháp, giờ mở và giờ đóng để trống)
    /// và bỏ qua mấy câu hỏi đã ẩn hoặc không dùng được ở cấp của lớp đích.
    /// </summary>
    public static AppMessage LessonDuplicated(string className, int skippedHeritages, int copiedQuizzes = 0, int skippedQuestions = 0)
    {
        var text = $"Đã tạo bài học nháp ở lớp {className}.";
        if (skippedHeritages > 0) text += $" Bỏ qua {skippedHeritages} di sản đã ẩn.";
        if (copiedQuizzes > 0) text += $" Đã chép {copiedQuizzes} bài kiểm tra thành bản nháp; hãy đặt lại giờ mở và giờ đóng.";
        if (skippedQuestions > 0) text += $" Bỏ qua {skippedQuestions} câu hỏi đã ẩn hoặc không dùng được ở cấp của lớp.";
        return new AppMessage(MessageCodes.LessonDuplicated, text);
    }

    /// <summary>Lớp đích khác cấp với lớp nguồn khi nhân bản bài học: nội dung vẫn được chép nguyên, cảnh báo giáo viên xem lại.</summary>
    public static string LessonLevelDiffers(string className, int targetLevel, int sourceLevel) =>
        $"Lớp {className} thuộc cấp {targetLevel}, khác cấp {sourceLevel} của bài gốc. Nội dung đang theo khung của cấp {sourceLevel}; " +
        "bạn nên xem lại hoặc nhờ AI soạn lại.";

    /// <summary>AI trả về bản nháp nội dung bài học; kèm số lượt AI còn lại trong kỳ.</summary>
    public static AppMessage AiDraftReady(string product, int remaining) => new(MessageCodes.AiDraftReady,
        $"Đã tạo bản nháp {product}. Hãy kiểm tra độ chính xác trước khi dùng. Còn {remaining} lượt AI trong kỳ.");

    /// <summary>Dịch vụ AI quá thời gian chờ hoặc lỗi sau các lần thử lại; lượt không bị tính.</summary>
    public static readonly AppMessage AiUnavailable = new(MessageCodes.AiUnavailable,
        "Dịch vụ AI đang bận. Lượt này không bị tính. Bạn có thể thử lại sau hoặc tự nhập nội dung; nội dung bài học được giữ nguyên.");

    /// <summary>Trường đã dùng hết lượt AI của kỳ gói hiện tại.</summary>
    public static AppMessage AiQuotaReached(int max, DateOnly endDate) => new(MessageCodes.AiQuotaReached,
        $"Trường đã dùng hết {max} lượt AI của kỳ gói hiện tại (đến hết ngày {endDate.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)}). Vui lòng liên hệ quản trị trường để nâng cấp gói.");

    /// <summary>Mã bài học sai, bài chưa xuất bản hoặc đã lưu trữ, bài không thuộc lớp của em: luôn cùng một câu.</summary>
    public static readonly AppMessage LessonCodeInvalid = new(MessageCodes.LessonCodeInvalid,
        "Mã bài học chưa đúng hoặc không dành cho lớp của em. Em hãy kiểm tra lại mã thầy cô đã đưa.");

    /// <summary>Nhập sai mã quá nhiều lần (theo tài khoản hoặc theo địa chỉ IP): chặn nhập mã một lúc.</summary>
    public static AppMessage LessonCodeBlocked(TimeSpan remaining)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return new AppMessage(MessageCodes.LessonCodeBlocked, $"Em đã nhập sai mã quá nhiều lần. Em hãy thử lại sau {minutes} phút.");
    }

    /// <summary>Kho lưu trữ tệp (Cloudinary) không ghi hoặc không xóa được: di sản giữ nguyên, tải lại được.</summary>
    public static readonly AppMessage StorageUnavailable = new(MessageCodes.StorageUnavailable,
        "Không tải được tệp lên kho lưu trữ. Di sản được giữ nguyên, bạn hãy thử tải lại sau ít phút.");

    /// <summary>Mã xem trước sai, hết hạn, di sản không còn hoặc người mở không phải quản trị hệ thống: luôn cùng một câu, không lộ di sản có tồn tại hay không.</summary>
    public static readonly AppMessage PreviewInvalid = new(MessageCodes.PreviewInvalid,
        "Liên kết xem trước đã hết hạn hoặc không hợp lệ. Hãy quay lại màn hình Biên tập di sản và bấm \"Xem trước\" để lấy liên kết mới.");

    // ---- Bài kiểm tra của bài học ----

    /// <summary>
    /// Xác nhận trước khi phát hành và lời báo sau khi phát hành: danh sách câu và điểm bị khóa, học sinh lớp làm bài qua mã bài học trong khoảng giờ mở và đóng.
    /// </summary>
    public static string QuizPublishNotice(string className, string quizName, string? lessonCode, DateTime? openAt, DateTime? closeAt) =>
        $"Sau khi phát hành, danh sách câu hỏi và điểm sẽ bị khóa. Học sinh lớp {className} làm được bài kiểm tra \"{quizName}\" " +
        $"qua mã bài học {lessonCode ?? "của bài"} từ {Domain.Rules.QuizRules.FormatVietnamTime(openAt)} đến {Domain.Rules.QuizRules.FormatVietnamTime(closeAt)}.";

    public static AppMessage QuizPublished(string className, string quizName, string? lessonCode, DateTime? openAt, DateTime? closeAt) =>
        new(MessageCodes.QuizPublished, $"Đã phát hành bài kiểm tra \"{quizName}\". Học sinh lớp {className} làm bài qua mã bài học {lessonCode ?? "của bài"} " +
            $"từ {Domain.Rules.QuizRules.FormatVietnamTime(openAt)} đến {Domain.Rules.QuizRules.FormatVietnamTime(closeAt)}.");

    public static AppMessage QuizClosed(string quizName, int expiredAttempts) => new(MessageCodes.QuizClosed, expiredAttempts == 0
        ? $"Đã đóng bài kiểm tra \"{quizName}\"."
        : $"Đã đóng bài kiểm tra \"{quizName}\". {expiredAttempts} lượt làm bài đang dở đã được tự nộp.");

    public static AppMessage QuizDuplicated(string quizName, int skippedQuestions) => new(MessageCodes.QuizDuplicated, skippedQuestions == 0
        ? $"Đã tạo bài kiểm tra nháp \"{quizName}\"."
        : $"Đã tạo bài kiểm tra nháp \"{quizName}\". Bỏ qua {skippedQuestions} câu hỏi đã bị ẩn.");

    /// <summary>Thêm câu vào bài kiểm tra, hoặc phát hành, khi câu có dạng hoặc cấp không dùng được ở cấp của lớp.</summary>
    public static AppMessage QuestionLevelMismatch(string typeName, int level) =>
        new(MessageCodes.QuestionLevelMismatch, $"Câu hỏi dạng \"{typeName}\" không dùng được ở lớp cấp {level}.");

    // ---- Làm bài của học sinh (xưng "em") ----

    /// <summary>Em chưa tới giờ làm bài kiểm tra.</summary>
    public static AppMessage QuizNotOpen(DateTime? openAt) => new(MessageCodes.QuizNotOpen,
        $"Bài kiểm tra chưa mở. Em quay lại làm bài từ {Domain.Rules.QuizRules.FormatVietnamTime(openAt)} nhé.");

    /// <summary>Bài kiểm tra đã đóng hoặc quá giờ đóng, em chưa có lượt làm bài.</summary>
    public static readonly AppMessage QuizEnded = new(MessageCodes.QuizEnded,
        "Bài kiểm tra đã đóng nên em không làm bài được nữa. Em hãy hỏi thầy cô nếu cần.");

    /// <summary>Lượt đã nộp (em nộp rồi hoặc hết giờ nên hệ thống tự nộp): chỉ còn xem kết quả.</summary>
    public static AppMessage AttemptFinished(bool autoSubmitted) => new(MessageCodes.AttemptFinished, autoSubmitted
        ? "Đã hết giờ làm bài nên bài của em được nộp tự động. Em hãy xem kết quả."
        : "Em đã nộp bài này rồi. Em hãy xem kết quả.");

    /// <summary>Xem kết quả khi lượt còn đang làm dở.</summary>
    public static readonly AppMessage AttemptInProgress = new(MessageCodes.AttemptInProgress,
        "Em chưa nộp bài này nên chưa xem được kết quả. Em hãy làm tiếp và nộp bài nhé.");

    /// <summary>Lời nhắc trước khi bắt đầu làm bài kiểm tra.</summary>
    public const string QuizOnlyOnceNotice = "Em chỉ được làm bài kiểm tra này một lần. Em bấm \"Bắt đầu\" khi đã sẵn sàng nhé.";

    // ---- Thống kê và gợi ý ----

    /// <summary>Trang học, tiến độ, gợi ý của em khi em chưa có lượt nào (hoặc lớp chưa có bài nào xuất bản).</summary>
    public const string StudentNoAttemptsYet = "Em chưa có bài làm nào. Em hãy nhập mã bài học thầy cô đưa để bắt đầu học.";

    /// <summary>Thống kê, tiến độ, gợi ý của lớp khi chưa có lượt nộp.</summary>
    public const string ClassNoResultsYet = "Chưa có học sinh nào nộp bài. Thống kê và gợi ý sẽ hiện khi có kết quả.";

    /// <summary>Lỗi hệ thống ngoài dự kiến.</summary>
    public static readonly AppMessage SystemError =
        new(MessageCodes.SystemError, "Đã có lỗi xảy ra. Vui lòng thử lại hoặc liên hệ quản trị viên.");
}
