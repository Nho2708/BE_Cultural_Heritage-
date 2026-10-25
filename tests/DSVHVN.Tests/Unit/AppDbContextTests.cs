using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Identity;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Quy ước CSDL trên model EF Core (SQLite trong bộ nhớ): đủ 27 bảng của v3 và mười hai bảng của v4 (lesson_sections, năm bảng đa ngôn ngữ, mô hình 3D, hai bảng của khách tham quan, ad_unlocks, lesson_completions) theo tên DBML, 4 vai trò seed, audit_logs chỉ thêm,
/// mốc UTC, unique có lọc, CHECK. Lược đồ SQL Server thật được kiểm ở lớp kiểm thử CSDL tạm trên LocalDB.
/// </summary>
public sealed class AppDbContextTests : IDisposable
{
    private readonly ServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    public static readonly string[] Tables =
    [
        "ad_unlocks", "ai_generations", "answers", "attempt_answer_parts", "attempt_answers", "attempts", "audit_logs", "classes", "event_translations", "events",
        "guest_activities", "guest_devices", "heritage_models", "heritage_narrations", "heritage_references", "heritage_summaries", "heritage_translations", "heritages", "invoices", "lesson_completions", "lesson_heritages", "lesson_sections",
        "lessons", "media", "media_translations", "organizations", "password_reset_tokens", "payment_providers", "payments", "plans",
        "provinces", "questions", "quiz_questions", "quizzes", "roles", "students", "subscriptions", "timeline_translations", "timelines",
        "users",
    ];

    [Fact]
    public async Task Model_has_the_40_tables_and_the_column_names_of_the_database_design()
    {
        var (tables, userColumns, fkCount, checkCount) = await _h.QueryAsync(db => Task.FromResult((
            db.Model.GetEntityTypes().Select(e => e.GetTableName()).OrderBy(n => n, StringComparer.Ordinal).ToList(),
            db.Model.FindEntityType(typeof(User))!.GetProperties().Select(p => p.GetColumnName()).ToList(),
            db.Model.GetEntityTypes().Sum(e => e.GetForeignKeys().Count()),
            db.GetService<IDesignTimeModel>().Model.GetEntityTypes().Sum(e => e.GetCheckConstraints().Count()))));

        Assert.Equal(Tables, tables);
        Assert.Equal(63, fkCount);   // 43 + 9 khóa ngoại của năm bảng đa ngôn ngữ + 1 của mô hình 3D + 3 của attempt_answer_parts + 2 của guest_activities + 2 của ad_unlocks + 1 của thuyết minh tạo sẵn + 2 của lesson_completions
        Assert.Equal(51, checkCount);   // 25 + 10 CHECK của năm bảng đa ngôn ngữ (mã ngôn ngữ, trạng thái theo ngôn ngữ, không có dòng tiếng Việt ở bảng con) + 3 CHECK của mô hình 3D (định dạng, trạng thái, dung lượng) + CK_attempt_answers_parts, CK_attempt_answer_parts_target (luồng học tập) + 5 CHECK của khách tham quan (nền tảng và ngôn ngữ của thiết bị, loại lượt và ngôn ngữ của lượt, CK_guest_activities_target) + 4 CHECK của ad_unlocks (nền tảng, mạng quảng cáo, CK_ad_unlocks_network_platform, CK_ad_unlocks_expiry) + 2 CHECK của heritage_narrations (mã ngôn ngữ, thời lượng)
        Assert.Contains("organization_id", userColumns);
        Assert.Contains("must_change_password", userColumns);
        Assert.Contains("last_login_at", userColumns);
    }

    [Fact]
    public async Task Four_roles_are_seeded_with_fixed_ids()
    {
        var roles = await _h.QueryAsync(db => db.Roles.OrderBy(r => r.Id).Select(r => new { r.Id, r.Code, r.Name }).ToListAsync());
        Assert.Equal(4, roles.Count);
        Assert.Equal((Roles.AdminId, RoleCode.ADMIN, "Quản trị hệ thống"), (roles[0].Id, roles[0].Code!.Value, roles[0].Name));
        Assert.Equal((Roles.OrgAdminId, RoleCode.ORG_ADMIN, "Quản trị trường"), (roles[1].Id, roles[1].Code!.Value, roles[1].Name));
        Assert.Equal((Roles.TeacherId, RoleCode.TEACHER, "Giáo viên"), (roles[2].Id, roles[2].Code!.Value, roles[2].Name));
        Assert.Equal((Roles.StudentId, RoleCode.STUDENT, "Học sinh"), (roles[3].Id, roles[3].Code!.Value, roles[3].Name));
    }

    [Fact]
    public async Task Audit_log_rows_cannot_be_updated_or_deleted()
    {
        await _h.QueryAsync(async db =>
        {
            db.AuditLogs.Add(new AuditLog { Action = AuditAction.LOGIN, ActorRole = ActorRole.SYSTEM, TargetType = AuditTargets.Users });
            return await db.SaveChangesAsync();
        });

        await _h.QueryAsync(async db =>
        {
            var log = await db.AuditLogs.SingleAsync();
            Assert.Equal(_h.Clock.GetUtcNow().UtcDateTime, log.CreatedAt);
            log.NewValue = "sửa";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

            db.ChangeTracker.Clear();
            db.AuditLogs.Remove(await db.AuditLogs.SingleAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            return 0;
        });
    }

    [Fact]
    public async Task Created_and_updated_timestamps_are_stamped_in_utc()
    {
        var org = await _h.AddOrganizationAsync();
        Assert.Equal(_h.Clock.GetUtcNow().UtcDateTime, org.CreatedAt);
        Assert.Null(org.UpdatedAt);

        _h.Clock.Advance(TimeSpan.FromMinutes(5));
        await _h.QueryAsync(async db =>
        {
            var stored = await db.Organizations.SingleAsync(o => o.Id == org.Id);
            stored.Address = "Quận 1, TP. Hồ Chí Minh";
            return await db.SaveChangesAsync();
        });

        var reread = await _h.QueryAsync(db => db.Organizations.SingleAsync(o => o.Id == org.Id));
        Assert.Equal(DateTimeKind.Utc, reread.CreatedAt!.Value.Kind);
        Assert.Equal(_h.Clock.GetUtcNow().UtcDateTime, reread.UpdatedAt);
        Assert.Equal("Quận 1, TP. Hồ Chí Minh", reread.Address);
    }

    [Fact]
    public async Task Email_and_username_are_unique_only_among_accounts_not_soft_deleted()
    {
        var org = await _h.AddOrganizationAsync();
        var first = await _h.AddUserAsync("giaovien.a", RoleCode.TEACHER, org.Id);

        await Assert.ThrowsAsync<DbUpdateException>(() => _h.AddUserAsync("giaovien.a", RoleCode.TEACHER, org.Id));

        await _h.QueryAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == first.Id);
            user.DeletedAt = _h.Clock.GetUtcNow().UtcDateTime;
            return await db.SaveChangesAsync();
        });

        var again = await _h.AddUserAsync("giaovien.a", RoleCode.TEACHER, org.Id);
        Assert.NotEqual(first.Id, again.Id);
    }

    [Fact]
    public async Task Students_without_email_and_with_the_same_name_are_told_apart_by_student_code()
    {
        var org = await _h.AddOrganizationAsync();
        var teacher = await _h.AddUserAsync("giaovien.lan", RoleCode.TEACHER, org.Id);

        var (first, _) = await _h.AddStudentAsync(org.Id, teacher.Id, "HS8K2QX7", "Nguyễn Văn An");
        var (second, _) = await _h.AddStudentAsync(org.Id, teacher.Id, "HS8M4TP9", "Nguyễn Văn An");

        Assert.Null(first.Email);
        Assert.Null(second.Email);
        Assert.True(first.MustChangePassword);
        Assert.Equal("HS8M4TP9", second.Username);

        // Mã học sinh không trùng toàn hệ thống; hai học sinh không dùng chung một tài khoản.
        await Assert.ThrowsAsync<DbUpdateException>(() => _h.AddStudentAsync(org.Id, teacher.Id, "HS8K2QX7", "Trần Bình"));
        await Assert.ThrowsAsync<DbUpdateException>(() => _h.QueryAsync(async db =>
        {
            var schoolClass = await db.Classes.FirstAsync();
            db.Students.Add(new Student { ClassId = schoolClass.Id, UserId = first.Id, StudentCode = "HS2ZZZ22", FullName = "Trùng" });
            return await db.SaveChangesAsync();
        }));
    }

    [Fact]
    public async Task Check_constraints_reject_values_outside_the_design()
    {
        var org = await _h.AddOrganizationAsync();
        var user = await _h.AddUserAsync("kiemtra.check", RoleCode.TEACHER, org.Id);

        // Enum lưu chuỗi: giá trị ngoài danh sách bị CHECK chặn.
        await Assert.ThrowsAnyAsync<Exception>(() => _h.QueryAsync(db =>
            db.Database.ExecuteSqlRawAsync("UPDATE users SET status = 'BANNED' WHERE id = {0}", user.Id)));

        // Khối lớp chỉ 1-12.
        await Assert.ThrowsAsync<DbUpdateException>(() => _h.QueryAsync(async db =>
        {
            db.Classes.Add(new SchoolClass { OrganizationId = org.Id, TeacherId = user.Id, Name = "13A", Grade = 13 });
            return await db.SaveChangesAsync();
        }));

        // Ảnh thuộc đúng một trong hai: di sản hoặc sự kiện.
        await Assert.ThrowsAsync<DbUpdateException>(() => _h.QueryAsync(async db =>
        {
            db.Media.Add(new Media { Url = "https://example.vn/anh.jpg" });
            return await db.SaveChangesAsync();
        }));
    }

    [Fact]
    public async Task Boolean_columns_take_their_defaults_when_inserted_without_a_value()
    {
        var org = await _h.AddOrganizationAsync();
        var teacher = await _h.AddUserAsync("giaovien.macdinh", RoleCode.TEACHER, org.Id);
        await _h.QueryAsync(db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO plans (name) VALUES ('Gói thử'); INSERT INTO payment_providers (code) VALUES ('VNPAY');"));

        var (planActive, providerActive, mustChange) = await _h.QueryAsync(async db => (
            (await db.Plans.SingleAsync(p => p.Name == "Gói thử")).IsActive,
            (await db.PaymentProviders.SingleAsync()).IsActive,
            (await db.Users.SingleAsync(u => u.Id == teacher.Id)).MustChangePassword));

        Assert.True(planActive);
        Assert.True(providerActive);
        Assert.False(mustChange);
    }
}
