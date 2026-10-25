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
/// Lược đồ phải đúng CSDL v3 cộng phần v4 đã làm (40 bảng, 63 khóa ngoại, 51 CHECK, 10 UNIQUE có lọc, collation Vietnamese_CI_AI);
/// dữ liệu tỉnh/thành và di sản nạp được; luồng web quản trị chạy được; ràng buộc tài khoản học sinh đúng thiết kế.
/// </summary>
public sealed class SqlServerDatabaseTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>
{
    private readonly PlatformClient _api = new(factory);

    private static readonly string[] NullableForeignKeys =
    [
        "ai_generations.heritage_id", "ai_generations.lesson_id", "attempt_answer_parts.answer_id", "attempt_answer_parts.selected_answer_id",
        "attempt_answers.answer_id", "audit_logs.user_id", "guest_activities.heritage_id",
        "heritage_summaries.ai_generation_id", "heritage_summaries.reviewed_by", "heritage_translations.ai_generation_id",
        "heritage_translations.reviewed_by", "heritages.province_id", "lesson_sections.event_id", "media.event_id", "media.heritage_id",
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
        Assert.Equal(40, (await SqlAsync<int>($"SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name <> '__EFMigrationsHistory'")).Single());
        Assert.Equal(369, (await SqlAsync<int>(   // 233 cột của thiết kế v3 + search_name, search_text (luồng Khám phá di sản) + lesson_sections (12 cột) và ai_generations.heritage_id (luồng bài học theo cấp) + 5 cột của questions, 4 cột của answers (luồng ngân hàng câu hỏi) + 46 cột của năm bảng đa ngôn ngữ (luồng đa ngôn ngữ) + 17 cột của heritage_models (luồng mô hình 3D) + 9 cột của attempt_answer_parts và 2 cột parts_total, parts_correct của attempt_answers (luồng học tập) + 9 cột của ad_unlocks (luồng quảng cáo Google) + 10 cột của heritage_narrations (thuyết minh tạo sẵn) + 4 cột của lesson_completions và attempt_answers.try_count (bài chơi dạng luyện tập)
            $"SELECT COUNT(*) AS [Value] FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id WHERE t.name <> '__EFMigrationsHistory'")).Single());
        Assert.Equal(63, (await SqlAsync<int>($"SELECT COUNT(*) AS [Value] FROM sys.foreign_keys")).Single());
        Assert.Equal(["fk_event_translations_event_id", "fk_media_translations_media_id", "fk_password_reset_tokens_user_id", "fk_timeline_translations_timeline_id"],
            await SqlAsync<string>($"SELECT name AS [Value] FROM sys.foreign_keys WHERE delete_referential_action_desc = 'CASCADE' ORDER BY name"));
        Assert.Equal(51, (await SqlAsync<int>($"SELECT COUNT(*) AS [Value] FROM sys.check_constraints")).Single());

        var filtered = await SqlAsync<string>(
            $"SELECT i.name + ' ' + i.filter_definition AS [Value] FROM sys.indexes i WHERE i.is_unique = 1 AND i.has_filter = 1 ORDER BY i.name");
        Assert.Equal(
        [
            "UX_attempt_answer_parts_answer ([answer_id] IS NOT NULL)",
            "UX_attempt_answer_parts_blank ([blank_index] IS NOT NULL)",
            "UX_heritage_models_visible ([status]='PUBLISHED' AND [deleted_at] IS NULL)",
            "UX_lessons_access_code ([access_code] IS NOT NULL)",
            "UX_payments_one_pending_per_invoice ([status]='PENDING')",
            "UX_payments_provider_txn ([provider_transaction_id] IS NOT NULL)",
            "UX_plans_name ([name] IS NOT NULL)",
            "UX_subscriptions_one_pending ([status]='PENDING')",
            "UX_users_email ([email] IS NOT NULL AND [deleted_at] IS NULL)",
            "UX_users_username ([deleted_at] IS NULL)",
        ], filtered);
        Assert.Equal(19, (await SqlAsync<int>(   // 10 + năm khóa không trùng (bản gốc, ngôn ngữ) của các bảng đa ngôn ngữ + mã thiết bị của khách + (mạng quảng cáo, mã giao dịch) của lượt mở khóa + (di sản, ngôn ngữ) của thuyết minh + (học sinh, bài học) của bài đã học xong
            $"SELECT COUNT(*) AS [Value] FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id WHERE i.is_unique = 1 AND i.has_filter = 0 AND i.is_primary_key = 0 AND t.name <> '__EFMigrationsHistory'")).Single());

        // 18 khóa ngoại được NULL; 42 khóa ngoại bắt buộc là NOT NULL.
        var nullableFks = await SqlAsync<string>($"""
            SELECT OBJECT_NAME(fkc.parent_object_id) + '.' + c.name AS [Value]
            FROM sys.foreign_key_columns fkc
            JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
            WHERE c.is_nullable = 1 ORDER BY 1
            """);
        Assert.Equal(NullableForeignKeys, nullableFks);

        // Cột enum là nvarchar(20); DEFAULT của cột bit đúng thiết kế.
        Assert.Equal(34, (await SqlAsync<int>($"""
            SELECT COUNT(*) AS [Value] FROM sys.columns c
            WHERE TYPE_NAME(c.user_type_id) = 'nvarchar' AND c.max_length = 40
              AND EXISTS (SELECT 1 FROM sys.check_constraints k
                          WHERE k.parent_object_id = c.object_id AND k.definition LIKE '%[[]' + c.name + '] IN %'
                             OR k.parent_object_id = c.object_id AND k.definition LIKE '%[[]' + c.name + ']=''%')
            """)).Single());
        Assert.Equal(["((0))", "((0))", "((0))", "((0))", "((0))", "((0))", "((0))", "((0))", "((0))", "((1))", "((1))", "((1))", "(CONVERT([smallint],(0)))", "(CONVERT([smallint],(1)))", "(CONVERT([tinyint],(0)))", "(CONVERT([tinyint],(1)))", "(CONVERT([tinyint],(3)))", "(N'vi')"],   // smallint: attempt_answers.parts_correct = 0, parts_total = 1; tinyint: attempt_answers.try_count = 0, questions.level_min = 1, level_max = 3; guest_devices.location_allowed = 0, language_code = 'vi' 
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

        var (hanoi, withoutTones, withoutLetterMarks, drafts) = await factory.WithDbAsync(async db => (
            (await db.Provinces.SingleAsync(p => p.Code == "01")).Name,
            await db.Heritages.CountAsync(h => h.Name!.Contains("hoang thanh")),
            await db.Heritages.CountAsync(h => h.Name!.Contains("thang long")),
            await db.Heritages.CountAsync(h => h.Status == ContentStatus.DRAFT)));
        Assert.Equal("Thành phố Hà Nội", hanoi);
        // Vietnamese_CI_AI bỏ qua dấu thanh ("hoang thanh" tìm ra "Hoàng thành") nhưng coi ă, â, ê, ô, ơ, ư, đ là chữ riêng:
        // "thang long" không tìm ra "Thăng Long". Chức năng tìm kiếm cần tự chuẩn hóa nếu muốn tìm được cả trường hợp này.
        Assert.Equal(1, withoutTones);
        Assert.Equal(0, withoutLetterMarks);
        Assert.Equal(20, drafts);       // nạp luôn vào DRAFT; chỉ lên PUBLISHED qua màn hình quản trị

        // Chạy lại không nhân đôi dữ liệu; trường không có cột được liệt kê, không lưu.
        var again = await factory.WithDbAsync(db => HeritageDataSeeder.SeedAsync(db,
            Path.Combine(dataDir, "provinces.json"), Path.Combine(dataDir, "seed-dot-1.json"), default));
        Assert.Equal((0, 34, 0, 20), (again.ProvincesAdded, again.ProvincesExisting, again.HeritagesAdded, again.HeritagesExisting));
        Assert.Empty(again.Rejected);
        Assert.Contains("map_point_note", again.IgnoredFields);
    }

    /// <summary>
    /// Các đợt sau đợt 1 (đợt 2: di tích quốc gia đặc biệt, danh hiệu UNESCO; đợt 3: di sản văn hóa phi vật thể quốc gia, di tích
    /// quốc gia; đợt 4: vườn quốc gia, khu dự trữ sinh quyển, công viên địa chất, khu Ramsar, vườn di sản ASEAN, di sản tư liệu)
    /// nạp lần lượt vào cùng database: mỗi tệp thêm đúng số di sản ghi ở meta của nó, không di sản nào bị bỏ vì không khớp
    /// cột, slug không trùng các đợt trước, mọi di sản ở DRAFT; nạp lại từng tệp không nhân đôi.
    /// </summary>
    [LocalDbHeritageDataFact]
    public async Task Later_heritage_batches_load_in_order_without_rejects_or_duplicates()
    {
        var dataDir = SqlServerApiFactory.HeritageDataDirectory!;
        var laterFiles = Directory.GetFiles(dataDir, "seed-dot-*.json")
            .Where(f => !Path.GetFileName(f).Equals("seed-dot-1.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.Contains(laterFiles, f => Path.GetFileName(f) == "seed-dot-2.json");
        Assert.Contains(laterFiles, f => Path.GetFileName(f).StartsWith("seed-dot-3", StringComparison.Ordinal));
        // Database tạm riêng (đã nạp đợt 1 lúc Api khởi động) để không đổi số đếm của test dùng chung fixture.
        using var own = new SqlServerApiFactory();
        var factory = own;
        _ = factory.Server;

        var before = await factory.WithDbAsync(db => db.Heritages.CountAsync());
        var loaded = 0;
        foreach (var file in laterFiles)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(file));
            var counts = doc.RootElement.GetProperty("meta").GetProperty("counts");
            var expected = (Heritages: counts.GetProperty("heritages").GetInt32(), Media: counts.GetProperty("media").GetInt32(),
                References: counts.GetProperty("references").GetInt32());
            // Mỗi đợt là một kho thật, không phải tệp thử: đợt nhỏ nhất (đợt 4, di sản thiên nhiên) có 52 di sản.
            Assert.True(expected.Heritages >= 10, $"{Path.GetFileName(file)} phải có ít nhất 10 di sản");

            var first = await factory.WithDbAsync(db => HeritageDataSeeder.SeedAsync(db,
                Path.Combine(dataDir, "provinces.json"), file, default));
            Assert.Empty(first.Rejected);
            Assert.Equal((0, 34, expected.Heritages, 0, expected.Media, expected.References),
                (first.ProvincesAdded, first.ProvincesExisting, first.HeritagesAdded, first.HeritagesExisting, first.MediaAdded, first.ReferencesAdded));
            loaded += expected.Heritages;

            var again = await factory.WithDbAsync(db => HeritageDataSeeder.SeedAsync(db,
                Path.Combine(dataDir, "provinces.json"), file, default));
            Assert.Equal((0, expected.Heritages), (again.HeritagesAdded, again.HeritagesExisting));
            Assert.Empty(again.Rejected);
        }

        var (total, drafts, withProvince, distinctSlugs) = await factory.WithDbAsync(async db => (
            await db.Heritages.CountAsync(),
            await db.Heritages.CountAsync(h => h.Status == ContentStatus.DRAFT),
            await db.Heritages.CountAsync(h => h.ProvinceId != null),
            await db.Heritages.Select(h => h.Slug).Distinct().CountAsync()));
        Assert.Equal(before + loaded, total);
        Assert.Equal(total, drafts);
        Assert.Equal(total, withProvince);
        Assert.Equal(total, distinctSlugs);
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
    public async Task Payments_without_gateway_id_are_allowed_but_a_gateway_id_is_recorded_once()
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
                new Payment { Invoice = invoice, Provider = provider, Status = PaymentStatus.FAILED },
                new Payment { Invoice = invoice, Provider = provider, Status = PaymentStatus.FAILED },
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
