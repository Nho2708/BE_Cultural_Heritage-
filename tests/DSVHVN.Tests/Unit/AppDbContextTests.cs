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
/// Quy ước CSDL v3 trên model EF Core (SQLite trong bộ nhớ): đủ 27 bảng theo tên DBML, 4 vai trò seed, audit_logs chỉ thêm,
/// mốc UTC, unique có lọc, CHECK. Lược đồ SQL Server thật được kiểm ở lớp kiểm thử CSDL tạm trên LocalDB.
/// </summary>
public sealed class AppDbContextTests : IDisposable
{
    private readonly ServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    public static readonly string[] Tables =
    [
        "ai_generations", "answers", "attempt_answers", "attempts", "audit_logs", "classes", "events", "heritage_references",
        "heritages", "invoices", "lesson_heritages", "lessons", "media", "organizations", "password_reset_tokens",
        "payment_providers", "payments", "plans", "provinces", "questions", "quiz_questions", "quizzes", "roles", "students",
        "subscriptions", "timelines", "users",
    ];

    [Fact]
    public async Task Model_has_the_27_tables_and_the_column_names_of_the_database_design()
    {
        var (tables, userColumns, fkCount, checkCount) = await _h.QueryAsync(db => Task.FromResult((
            db.Model.GetEntityTypes().Select(e => e.GetTableName()).OrderBy(n => n, StringComparer.Ordinal).ToList(),
            db.Model.FindEntityType(typeof(User))!.GetProperties().Select(p => p.GetColumnName()).ToList(),
            db.Model.GetEntityTypes().Sum(e => e.GetForeignKeys().Count()),
            db.GetService<IDesignTimeModel>().Model.GetEntityTypes().Sum(e => e.GetCheckConstraints().Count()))));

        Assert.Equal(Tables, tables);
        Assert.Equal(40, fkCount);
        Assert.Equal(19, checkCount);
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
