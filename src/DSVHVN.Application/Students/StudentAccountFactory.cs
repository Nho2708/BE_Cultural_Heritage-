using System.Text;
using System.Text.RegularExpressions;
using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DSVHVN.Application.Students;

/// <summary>Họ tên học sinh: cắt khoảng trắng hai đầu, gộp khoảng trắng liên tiếp, chuẩn hóa Unicode về dạng dựng sẵn.</summary>
public static partial class StudentNames
{
    public static string Clean(string? name) =>
        Spaces().Replace((name ?? string.Empty).Normalize(NormalizationForm.FormC), " ").Trim();

    /// <summary>Khóa so sánh trùng họ tên: không phân biệt hoa thường và dấu.</summary>
    public static string Key(string? name) => SearchText.Fold(name);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>
/// Tạo tài khoản học sinh: với mỗi em sinh mã học sinh (không trùng toàn hệ thống), mật khẩu tạm, tài khoản vai trò học sinh
/// (tên đăng nhập là mã học sinh, không email, hoạt động, buộc đổi mật khẩu) và bản ghi học sinh nối với tài khoản và lớp.
/// Không tự mở giao dịch: người gọi bọc việc khóa lớp, kiểm sĩ số và bước này trong cùng một giao dịch.
/// Ghi một dòng nhật ký cho cả lần thêm, kèm số học sinh (không ghi mật khẩu tạm).
/// </summary>
public sealed class StudentAccountFactory(
    IAppDbContext db,
    IPasswordHasher hasher,
    IStudentCredentialGenerator generator,
    IAuditLogger audit,
    ILogger<StudentAccountFactory> logger)
{
    /// <summary>Mã bị trùng thì sinh mã khác và thử lại tối đa 5 lần; vẫn trùng thì báo lỗi hệ thống và ghi log.</summary>
    public const int MaxCodeAttempts = 5;

    public async Task<IReadOnlyList<NewStudentAccount>> CreateAsync(SchoolClass schoolClass, IReadOnlyList<string> fullNames,
        Actor actor, CancellationToken ct)
    {
        if (fullNames.Count == 0) return [];

        var passwords = fullNames.Select(_ => generator.NewTemporaryPassword()).ToArray();
        var hashes = new string[passwords.Length];
        // PBKDF2 tốn thời gian (khoảng vài chục mili giây mỗi mật khẩu): băm song song để 60 học sinh vẫn xong trong vài giây.
        Parallel.For(0, passwords.Length, i => hashes[i] = hasher.Hash(passwords[i]));

        for (var attempt = 1; attempt <= MaxCodeAttempts; attempt++)
        {
            var codes = await NewFreeCodesAsync(fullNames.Count, ct);
            var created = new List<NewStudentAccount>(fullNames.Count);
            for (var i = 0; i < fullNames.Count; i++)
            {
                var user = new User
                {
                    OrganizationId = schoolClass.OrganizationId,
                    RoleId = Roles.StudentId,
                    Username = codes[i],
                    Email = null,
                    PasswordHash = hashes[i],
                    MustChangePassword = true,
                    FullName = fullNames[i],
                    Status = UserStatus.ACTIVE,
                };
                var student = new Student
                {
                    ClassId = schoolClass.Id, User = user, StudentCode = codes[i], FullName = fullNames[i], IsActive = true,
                };
                db.Students.Add(student);
                created.Add(new NewStudentAccount(student, passwords[i]));
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                // Hai yêu cầu cùng sinh một mã ở cùng lúc: ràng buộc không trùng chặn yêu cầu thứ hai, bỏ lô này và sinh mã khác.
                logger.LogWarning(ex, "Mã học sinh bị trùng ở lần thử {Attempt}/{Max}, sinh mã khác", attempt, MaxCodeAttempts);
                foreach (var item in created)
                {
                    db.Students.Entry(item.Student).State = EntityState.Detached;
                    if (item.Student.User is { } u) db.Users.Entry(u).State = EntityState.Detached;
                }
                continue;
            }

            audit.Record(AuditAction.STUDENT_ACCOUNTS_CREATED, AuditTargets.Classes, schoolClass.Id, actor,
                newValue: new { classId = schoolClass.Id, className = schoolClass.Name, count = created.Count });
            await db.SaveChangesAsync(ct);
            return created;
        }

        logger.LogError("Không sinh được mã học sinh không trùng sau {Max} lần thử cho lớp {ClassId}", MaxCodeAttempts, schoolClass.Id);
        throw new InvalidOperationException("Không sinh được mã học sinh không trùng.");
    }

    // Mã không trùng trong chính lô và không có trong bảng học sinh (kể cả học sinh đã xóa: mã không bao giờ cấp lại)
    // hay tên đăng nhập của tài khoản nào còn dùng. Mỗi mã trùng được sinh lại, tối đa MaxCodeAttempts vòng.
    private async Task<string[]> NewFreeCodesAsync(int count, CancellationToken ct)
    {
        var codes = new string[count];
        var pending = Enumerable.Range(0, count).ToList();
        for (var round = 0; round < MaxCodeAttempts && pending.Count > 0; round++)
        {
            var seen = codes.Where(c => c is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var i in pending)
            {
                var code = generator.NewStudentCode();
                if (seen.Add(code)) codes[i] = code;
                else codes[i] = null!;
            }
            pending = pending.Where(i => codes[i] is null).ToList();

            var candidates = codes.Where(c => c is not null).ToList();
            var taken = (await db.Students.Where(s => candidates.Contains(s.StudentCode!)).Select(s => s.StudentCode!).ToListAsync(ct))
                .Concat(await db.Users.Where(u => u.DeletedAt == null && candidates.Contains(u.Username!)).Select(u => u.Username!).ToListAsync(ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < count; i++)
            {
                if (codes[i] is not null && taken.Contains(codes[i]))
                {
                    codes[i] = null!;
                    pending.Add(i);
                }
            }
        }

        if (pending.Count > 0)
        {
            logger.LogError("Không sinh được mã học sinh không trùng sau {Max} vòng", MaxCodeAttempts);
            throw new InvalidOperationException("Không sinh được mã học sinh không trùng.");
        }
        return codes;
    }
}

/// <summary>Dựng dữ liệu phiếu đăng nhập từ cấu hình và danh sách học sinh vừa tạo hoặc vừa đặt lại mật khẩu.</summary>
public sealed class LoginSheetBuilder(IOptions<StudentSheetOptions> options)
{
    public const string FirstLoginReminder = "Em sẽ phải đổi mật khẩu ở lần đăng nhập đầu tiên.";

    public const string Notice =
        "Mật khẩu tạm chỉ hiện một lần. Hãy in hoặc tải phiếu đăng nhập trước khi rời trang; nếu mất phiếu, dùng \"Đặt lại mật khẩu\".";

    public LoginSheetDto Build(string? className, IEnumerable<LoginSheetEntryDto> entries)
    {
        var o = options.Value;
        var loginUrl = o.LearningWebBaseUrl.TrimEnd('/') + "/" + o.LearningLoginPath.TrimStart('/');
        var appUrl = string.IsNullOrWhiteSpace(o.MobileAppDownloadUrl) ? null : o.MobileAppDownloadUrl.Trim();

        var instructions = new List<string>
        {
            $"Mở trang học tập: {loginUrl}",
            appUrl is null
                ? "Hoặc cài ứng dụng DiViet trên điện thoại, mở ứng dụng và chọn chế độ \"Học sinh\" (không chọn \"Khách tham quan\")."
                : $"Hoặc cài ứng dụng DiViet trên điện thoại (tải tại {appUrl}), mở ứng dụng và chọn chế độ \"Học sinh\" (không chọn \"Khách tham quan\").",
            "Đăng nhập bằng mã học sinh và mật khẩu tạm ghi trên phiếu.",
        };
        return new LoginSheetDto(className, loginUrl, appUrl, instructions, FirstLoginReminder, Notice, entries.ToList());
    }
}
