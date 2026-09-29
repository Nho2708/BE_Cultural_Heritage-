using System.Net;
using DSVHVN.Application.Auth;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Lessons;
using DSVHVN.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Kiểm trên SQL Server thật: database tạm tạo bằng migration khởi tạo (khi Api khởi động) và tự xóa khi xong.
/// Lược đồ phải đúng CSDL v3 (27 bảng, 40 khóa ngoại, 19 CHECK, 4 UNIQUE có lọc, collation Vietnamese_CI_AI);
/// dữ liệu tỉnh/thành và di sản nạp được; luồng web quản trị chạy được; ràng buộc tài khoản học sinh đúng thiết kế.
/// </summary>
public sealed class SqlServerDatabaseTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>
{
    private readonly PlatformClient _api = new(factory);

    private static readonly string[] NullableForeignKeys =
    [
        "attempt_answers.answer_id", "audit_logs.user_id", "heritages.province_id", "media.event_id", "media.heritage_id",
        "questions.ai_generation_id", "questions.reviewed_by", "users.organization_id",
    ];

    private Task<List<T>> SqlAsync<T>(FormattableString sql) =>
        factory.WithDbAsync(db => db.Database.SqlQuery<T>(sql).ToListAsync());

    [LocalDbFact]
    public async Task Schema_created_by_the_migration_matches_the_database_design()
    {
        _ = factory.Server; // khởi động Api: áp migration vào database tạm

        Assert.True(LocalDb.Exists(factory.DatabaseName));
        Assert.Equal("Vietnamese_CI_AI",
            (await SqlAsync<string>($"SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128)) AS [Value]")).Single());
        Assert.Equal(27, (await SqlAsync<int>($"SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name <> '__EFMigrationsHistory'")).Single());
        Assert.Equal(233, (await SqlAsync<int>(
            $"SELECT COUNT(*) AS [Value] FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id WHERE t.name <> '__EFMigrationsHistory'")).Single());
        Assert.Equal(40, (await SqlAsync<int>($"SELECT COUNT(*) AS [Value] FROM sys.foreign_keys")).Single());
        Assert.Equal(["fk_password_reset_tokens_user_id"],
            await SqlAsync<string>($"SELECT name AS [Value] FROM sys.foreign_keys WHERE delete_referential_action_desc = 'CASCADE'"));
        Assert.Equal(19, (await SqlAsync<int>($"SELECT COUNT(*) AS [Value] FROM sys.check_constraints")).Single());

        var filtered = await SqlAsync<string>(
            $"SELECT i.name + ' ' + i.filter_definition AS [Value] FROM sys.indexes i WHERE i.is_unique = 1 AND i.has_filter = 1 ORDER BY i.name");
        Assert.Equal(
        [
            "UX_lessons_access_code ([access_code] IS NOT NULL)",
            "UX_payments_provider_txn ([provider_transaction_id] IS NOT NULL)",
            "UX_users_email ([email] IS NOT NULL AND [deleted_at] IS NULL)",
            "UX_users_username ([deleted_at] IS NULL)",
        ], filtered);
        Assert.Equal(10, (await SqlAsync<int>(
            $"SELECT COUNT(*) AS [Value] FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id WHERE i.is_unique = 1 AND i.has_filter = 0 AND i.is_primary_key = 0 AND t.name <> '__EFMigrationsHistory'")).Single());

        // 8 khóa ngoại được NULL; 32 khóa ngoại bắt buộc là NOT NULL.
        var nullableFks = await SqlAsync<string>($"""
            SELECT OBJECT_NAME(fkc.parent_object_id) + '.' + c.name AS [Value]
            FROM sys.foreign_key_columns fkc
            JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
            WHERE c.is_nullable = 1 ORDER BY 1
            """);
        Assert.Equal(NullableForeignKeys, nullableFks);

        // Cột enum là nvarchar(20); DEFAULT của cột bit đúng thiết kế.
        Assert.Equal(17, (await SqlAsync<int>($"""
            SELECT COUNT(*) AS [Value] FROM sys.columns c
            WHERE TYPE_NAME(c.user_type_id) = 'nvarchar' AND c.max_length = 40
              AND EXISTS (SELECT 1 FROM sys.check_constraints k
                          WHERE k.parent_object_id = c.object_id AND k.definition LIKE '%[[]' + c.name + '] IN %'
                             OR k.parent_object_id = c.object_id AND k.definition LIKE '%[[]' + c.name + ']=''%')
            """)).Single());
        Assert.Equal(["((0))", "((0))", "((0))", "((0))", "((0))", "((1))", "((1))", "((1))"],
            await SqlAsync<string>($"SELECT definition AS [Value] FROM sys.default_constraints ORDER BY definition"));

        var roles = await factory.WithDbAsync(db => db.Roles.OrderBy(r => r.Id).Select(r => r.Name).ToListAsync());
        Assert.Equal(["Quản trị hệ thống", "Quản trị trường", "Giáo viên", "Học sinh"], roles);
    }

    [LocalDbHeritageDataFact]
    public async Task Heritage_data_is_loaded_once_with_vietnamese_text_and_found_without_accents()
    {
        var dataDir = SqlServerApiFactory.HeritageDataDirectory!;
        _ = factory.Server;

        var counts = await factory.WithDbAsync(async db => new[]
        {
            await db.Provinces.CountAsync(), await db.Heritages.CountAsync(), await db.Timelines.CountAsync(),
            await db.Events.CountAsync(), await db.Media.CountAsync(), await db.HeritageReferences.CountAsync(),
        });
        Assert.Equal([34, 20, 37, 158, 64, 110], counts);

        var (hanoi, withoutTones, withoutLetterMarks, published) = await factory.WithDbAsync(async db => (
            (await db.Provinces.SingleAsync(p => p.Code == "01")).Name,
            await db.Heritages.CountAsync(h => h.Name!.Contains("hoang thanh")),
            await db.Heritages.CountAsync(h => h.Name!.Contains("thang long")),
            await db.Heritages.CountAsync(h => h.Status == ContentStatus.PUBLISHED)));
        Assert.Equal("Thành phố Hà Nội", hanoi);
        // Vietnamese_CI_AI bỏ qua dấu thanh ("hoang thanh" tìm ra "Hoàng thành") nhưng coi ă, â, ê, ô, ơ, ư, đ là chữ riêng:
        // "thang long" không tìm ra "Thăng Long". Chức năng tìm kiếm cần tự chuẩn hóa nếu muốn tìm được cả trường hợp này.
        Assert.Equal(1, withoutTones);
        Assert.Equal(0, withoutLetterMarks);
        Assert.Equal(20, published);

        // Chạy lại không nhân đôi dữ liệu; trường không có cột được liệt kê, không lưu.
        var again = await factory.WithDbAsync(db => HeritageDataSeeder.SeedAsync(db,
            Path.Combine(dataDir, "provinces.json"), Path.Combine(dataDir, "seed-dot-1.json"), default));
        Assert.Equal((0, 34, 0, 20), (again.ProvincesAdded, again.ProvincesExisting, again.HeritagesAdded, again.HeritagesExisting));
        Assert.Empty(again.Rejected);
        Assert.Contains("map_point_note", again.IgnoredFields);
    }

    [LocalDbFact]
    public async Task Admin_web_flow_runs_on_sql_server()
    {
        var (org, orgAdminToken) = await _api.OrgWithAdminAsync();
        var (teacher, teacherToken) = await _api.TeacherAsync(orgAdminToken);

        var (me, profile) = await _api.Http.GetAsync<ProfileDto>("/api/v1/me", teacherToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal((RoleCode.TEACHER, org.Id, false), (profile.Data!.Role, profile.Data.Organization!.Id, profile.Data.MustChangePassword));

        var actions = await factory.WithDbAsync(db => db.AuditLogs.Where(l => l.UserId != null)
            .Select(l => l.Action).Distinct().ToListAsync());
        Assert.Contains(AuditAction.ORG_CREATED, actions);
        Assert.Contains(AuditAction.ACCOUNT_CREATED, actions);
        Assert.Contains(AuditAction.LOGIN, actions);
        Assert.NotNull(teacher.Email);
    }

    [LocalDbFact]
    public async Task Student_accounts_follow_the_design_and_cannot_use_the_admin_web()
    {
        var (org, orgAdminToken) = await _api.OrgWithAdminAsync();
        var (teacher, _) = await _api.TeacherAsync(orgAdminToken);
        var suffix = PlatformClient.Unique().ToUpperInvariant()[..6];
        var hasher = factory.Services.GetRequiredService<IPasswordHasher>();

        // Hai học sinh trùng họ tên, không email, phân biệt bằng mã học sinh.
        var students = await factory.WithDbAsync(async db =>
        {
            var schoolClass = new SchoolClass { OrganizationId = org.Id, TeacherId = teacher.Id, Name = "8A1", Grade = 8, SchoolYear = "2026-2027" };
            db.Classes.Add(schoolClass);
            var accounts = new[] { "HS" + suffix, "HT" + suffix }.Select(code => new User
            {
                OrganizationId = org.Id, RoleId = Roles.StudentId, Username = code, Email = null, MustChangePassword = true,
                PasswordHash = hasher.Hash(PlatformClient.Password), FullName = "Nguyễn Văn An", Status = UserStatus.ACTIVE,
            }).ToList();
            db.Users.AddRange(accounts);
            await db.SaveChangesAsync();
            db.Students.AddRange(accounts.Select(a => new Student
            {
                ClassId = schoolClass.Id, UserId = a.Id, StudentCode = a.Username, FullName = a.FullName,
            }));
            await db.SaveChangesAsync();
            return accounts;
        });

        // Web quản trị: tài khoản học sinh (gõ thường, collation không phân biệt hoa thường vẫn tìm ra) nhận thông điệp sai thông tin.
        var (login, loginBody) = await _api.Http.PostAsync<LoginResultDto>("/api/v1/auth/login",
            new { emailOrUsername = students[0].Username!.ToLowerInvariant(), password = PlatformClient.Password });
        Assert.Equal((HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS"), (login.StatusCode, loginBody.ErrorCode));

        // Token vai trò học sinh không dùng được màn hình Hồ sơ của web quản trị.
        var studentToken = factory.Services.GetRequiredService<ITokenIssuer>().CreateAccessToken(new AccessTokenSubject(
            students[0].Id, students[0].Username!, students[0].FullName!, RoleCode.STUDENT, org.Id,
            SecurityStamps.From(students[0].PasswordHash))).Token;
        var (me, meBody) = await _api.Http.GetAsync<object>("/api/v1/me", studentToken);
        Assert.Equal((HttpStatusCode.Forbidden, "FORBIDDEN"), (me.StatusCode, meBody.ErrorCode));

        // Ràng buộc của CSDL: mã học sinh và tên đăng nhập không trùng; khối lớp 1-12; mã bài học không trùng khi có giá trị.
        await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            db.Users.Add(new User { OrganizationId = org.Id, RoleId = Roles.StudentId, Username = students[0].Username, FullName = "Trùng mã" });
            return await db.SaveChangesAsync();
        }));
        await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            db.Classes.Add(new SchoolClass { OrganizationId = org.Id, TeacherId = teacher.Id, Name = "13A", Grade = 13 });
            return await db.SaveChangesAsync();
        }));

        var code = "BH" + suffix;
        await factory.WithDbAsync(async db =>
        {
            var classId = await db.Classes.Where(c => c.TeacherId == teacher.Id).Select(c => c.Id).FirstAsync();
            db.Lessons.AddRange(
                new Lesson { TeacherId = teacher.Id, ClassId = classId, Title = "Hoàng thành Thăng Long", Status = ContentStatus.PUBLISHED, AccessCode = code },
                new Lesson { TeacherId = teacher.Id, ClassId = classId, Title = "Văn Miếu", Status = ContentStatus.DRAFT },
                new Lesson { TeacherId = teacher.Id, ClassId = classId, Title = "Cố đô Huế", Status = ContentStatus.DRAFT });
            return await db.SaveChangesAsync();
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            var classId = await db.Classes.Where(c => c.TeacherId == teacher.Id).Select(c => c.Id).FirstAsync();
            db.Lessons.Add(new Lesson { TeacherId = teacher.Id, ClassId = classId, Title = "Trùng mã", AccessCode = code });
            return await db.SaveChangesAsync();
        }));
    }

    [LocalDbFact]
    public async Task Pending_payments_without_gateway_id_are_allowed_but_a_gateway_id_is_recorded_once()
    {
        var (org, _) = await _api.OrgWithAdminAsync();
        var txn = "VNP" + PlatformClient.Unique();

        await factory.WithDbAsync(async db =>
        {
            var provider = await db.PaymentProviders.FirstOrDefaultAsync(p => p.Code == "VNPAY")
                           ?? db.PaymentProviders.Add(new PaymentProvider { Code = "VNPAY", Name = "VNPay" }).Entity;
            var subscription = await db.Subscriptions.FirstAsync(s => s.OrganizationId == org.Id);
            var invoice = new Invoice
            {
                OrganizationId = org.Id, Subscription = subscription, InvoiceNumber = "HD" + PlatformClient.Unique(),
                Amount = 990000m, Status = InvoiceStatus.UNPAID,
            };
            db.Invoices.Add(invoice);
            db.Payments.AddRange(
                new Payment { Invoice = invoice, Provider = provider, Status = PaymentStatus.PENDING },
                new Payment { Invoice = invoice, Provider = provider, Status = PaymentStatus.PENDING },
                new Payment { Invoice = invoice, Provider = provider, Status = PaymentStatus.SUCCEEDED, ProviderTransactionId = txn });
            return await db.SaveChangesAsync();
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            var payment = await db.Payments.FirstAsync(p => p.ProviderTransactionId == txn);
            db.Payments.Add(new Payment { InvoiceId = payment.InvoiceId, ProviderId = payment.ProviderId, ProviderTransactionId = txn });
            return await db.SaveChangesAsync();
        }));
    }
}
