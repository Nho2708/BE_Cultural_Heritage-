using System.Text;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Common;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DSVHVN.Infrastructure.Persistence;

/// <summary>
/// DbContext của toàn hệ thống. Lược đồ chỉ đổi qua migration EF Core theo luồng:
/// Luồng nền tảng tạo 7 bảng organizations, roles, users, password_reset_tokens, audit_logs, plans, subscriptions.
/// Tên bảng, tên cột giữ đúng DBML v2 (snake_case) — xem <see cref="ApplySnakeCaseNames"/>.
/// Cấu hình riêng SQL Server (collation Vietnamese_CI_AI, mặc định SYSUTCDATETIME()) chỉ áp khi provider là SQL Server
/// để kiểm thử chạy được trên SQLite.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, TimeProvider clock)
    : DbContext(options), IAppDbContext
{
    public const string VietnameseCollation = "Vietnamese_CI_AI";

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<User> Users => Set<User>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // datetime2(3) lưu UTC; SQL Server trả về Kind=Unspecified nên gắn lại Kind=Utc khi đọc.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>().HavePrecision(3);
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>().HavePrecision(3);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var isSqlServer = Database.IsSqlServer();
        if (isSqlServer) modelBuilder.UseCollation(VietnameseCollation);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        ApplySnakeCaseNames(modelBuilder);

        if (!isSqlServer) return;
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(IHasCreatedAt).IsAssignableFrom(entity.ClrType)) continue;
            entity.FindProperty(nameof(IHasCreatedAt.CreatedAt))?.SetDefaultValueSql("SYSUTCDATETIME()");
        }
    }

    /// <summary>
    /// Khóa dòng trường tới hết giao dịch (UPDLOCK, HOLDLOCK) để đếm-rồi-thêm theo trường chạy tuần tự.
    /// SQLite (kiểm thử) không có gợi ý khóa dòng và vốn chỉ cho một giao dịch ghi tại một thời điểm, nên bỏ qua.
    /// </summary>
    public async Task LockOrganizationAsync(long organizationId, CancellationToken cancellationToken)
    {
        if (Database.CurrentTransaction is null)
            throw new InvalidOperationException("LockOrganizationAsync phải được gọi bên trong giao dịch.");
        if (!Database.IsSqlServer()) return;
        await Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM organizations WITH (UPDLOCK, HOLDLOCK) WHERE id = {organizationId}", cancellationToken);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampAndGuard();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampAndGuard();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    private void StampAndGuard()
    {
        // Cắt về mili giây cho khớp datetime2(3): giá trị trả ngay sau khi lưu giống giá trị đọc lại từ CSDL.
        var now = clock.GetUtcNow().UtcDateTime;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is AuditLog)
            {
                // audit_logs chỉ thêm, không sửa, không xóa.
                if (entry.State is EntityState.Modified or EntityState.Deleted)
                    throw new InvalidOperationException("Nhật ký kiểm toán chỉ được thêm, không sửa, không xóa.");
                if (entry.State == EntityState.Added) ((AuditLog)entry.Entity).CreatedAt = now;
                continue;
            }

            if (entry.State == EntityState.Added && entry.Entity is IHasCreatedAt created && created.CreatedAt == default)
                created.CreatedAt = now;
            if (entry.State == EntityState.Modified && entry.Entity is IHasUpdatedAt updated)
                updated.UpdatedAt = now;
        }
    }

    /// <summary>
    /// Giữ nguyên tên của DBML v2: cột snake_case (OrganizationId → organization_id), khóa ngoại
    /// <c>fk_{bảng}_{cột}</c> như khối Ref của DBML, khóa chính <c>PK_{bảng}</c>. Tên bảng khai báo trong từng cấu hình.
    /// </summary>
    private static void ApplySnakeCaseNames(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()
                        ?? throw new InvalidOperationException($"{entity.Name} chưa khai báo tên bảng.");
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));
            entity.FindPrimaryKey()?.SetName($"PK_{table}");
            foreach (var fk in entity.GetForeignKeys())
                fk.SetConstraintName($"fk_{table}_{string.Join('_', fk.Properties.Select(p => ToSnakeCase(p.Name)))}");
        }
    }

    public static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var prevLower = i > 0 && char.IsLower(name[i - 1]);
                var acronymEnd = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (prevLower || acronymEnd) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v == null ? v : v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime(),
        v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
}
