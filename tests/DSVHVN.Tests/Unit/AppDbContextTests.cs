using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy ước CSDL của luồng nền tảng: tên theo DBML, 3 vai trò seed, audit_logs chỉ thêm, mốc UTC, unique có lọc deleted_at.</summary>
public sealed class AppDbContextTests : IDisposable
{
    private readonly ServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Tables_and_columns_use_the_DBML_names()
    {
        var (tables, userColumns) = await _h.QueryAsync(db => Task.FromResult((
            db.Model.GetEntityTypes().Select(e => e.GetTableName()).OrderBy(n => n).ToList(),
            db.Model.FindEntityType(typeof(User))!.GetProperties().Select(p => p.GetColumnName()).ToList())));

        Assert.Equal(["audit_logs", "organizations", "password_reset_tokens", "plans", "roles", "subscriptions", "users"], tables);
        Assert.Contains("organization_id", userColumns);
        Assert.Contains("password_hash", userColumns);
        Assert.Contains("last_login_at", userColumns);
    }

    [Fact]
    public async Task Three_roles_are_seeded_with_fixed_ids()
    {
        var roles = await _h.QueryAsync(db => db.Roles.OrderBy(r => r.Id).Select(r => new { r.Id, r.Code, r.Name }).ToListAsync());
        Assert.Equal(3, roles.Count);
        Assert.Equal((Roles.AdminId, RoleCode.ADMIN, "Quản trị hệ thống"), (roles[0].Id, roles[0].Code, roles[0].Name));
        Assert.Equal((Roles.OrgAdminId, RoleCode.ORG_ADMIN, "Quản trị trường"), (roles[1].Id, roles[1].Code, roles[1].Name));
        Assert.Equal((Roles.TeacherId, RoleCode.TEACHER, "Giáo viên"), (roles[2].Id, roles[2].Code, roles[2].Name));
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
        Assert.Equal(DateTimeKind.Utc, reread.CreatedAt.Kind);
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
    public async Task Status_check_constraint_rejects_values_outside_the_enum()
    {
        var org = await _h.AddOrganizationAsync();
        var user = await _h.AddUserAsync("kiemtra.check", RoleCode.TEACHER, org.Id);
        await Assert.ThrowsAnyAsync<Exception>(() => _h.QueryAsync(db =>
            db.Database.ExecuteSqlRawAsync("UPDATE users SET status = 'BANNED' WHERE id = {0}", user.Id)));
    }
}
