using System.Text;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Common;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Lessons;
using DSVHVN.Domain.Organizations;
using DSVHVN.Domain.Questions;
using DSVHVN.Domain.Quizzes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DSVHVN.Infrastructure.Persistence;

/// <summary>
/// DbContext của toàn hệ thống: đủ 27 bảng của CSDL v3 (doc/database/dsvhvn-v3.dbml), 8 nhóm.
/// Lược đồ chỉ đổi qua migration EF Core mới. Tên bảng, tên cột giữ đúng DBML (snake_case) — xem <see cref="ApplySnakeCaseNames"/>.
/// Cấu hình riêng SQL Server (collation Vietnamese_CI_AI) chỉ áp khi provider là SQL Server để kiểm thử chạy được trên SQLite.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, TimeProvider clock)
    : DbContext(options), IAppDbContext
{
    public const string VietnameseCollation = "Vietnamese_CI_AI";

    // 1. Auth / Tổ chức
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<User> Users => Set<User>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    // 2. Lớp / Học sinh
    public DbSet<SchoolClass> Classes => Set<SchoolClass>();
    public DbSet<Student> Students => Set<Student>();

    // 3. Di sản
    public DbSet<Province> Provinces => Set<Province>();
    public DbSet<Heritage> Heritages => Set<Heritage>();
    public DbSet<Timeline> Timelines => Set<Timeline>();
    public DbSet<TimelineEvent> Events => Set<TimelineEvent>();
    public DbSet<Media> Media => Set<Media>();
    public DbSet<HeritageReference> HeritageReferences => Set<HeritageReference>();

    // 4. Bài học + AI
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<LessonHeritage> LessonHeritages => Set<LessonHeritage>();
    public DbSet<AiGeneration> AiGenerations => Set<AiGeneration>();

    // 5. Ngân hàng câu hỏi
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<Answer> Answers => Set<Answer>();

    // 6. Quiz
    public DbSet<Quiz> Quizzes => Set<Quiz>();
    public DbSet<QuizQuestion> QuizQuestions => Set<QuizQuestion>();

    // 7. Làm bài / Kết quả
    public DbSet<Attempt> Attempts => Set<Attempt>();
    public DbSet<AttemptAnswer> AttemptAnswers => Set<AttemptAnswer>();

    // 8. Thanh toán
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<PaymentProvider> PaymentProviders => Set<PaymentProvider>();
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // datetime2(3) lưu UTC; SQL Server trả về Kind=Unspecified nên gắn lại Kind=Utc khi đọc.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>().HavePrecision(3);
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>().HavePrecision(3);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        if (Database.IsSqlServer()) modelBuilder.UseCollation(VietnameseCollation);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        ApplySnakeCaseNames(modelBuilder);
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
            if (entry.Entity is AuditLog log)
            {
                // audit_logs chỉ thêm, không sửa, không xóa.
                if (entry.State is EntityState.Modified or EntityState.Deleted)
                    throw new InvalidOperationException("Nhật ký thao tác chỉ được thêm, không sửa, không xóa.");
                if (entry.State == EntityState.Added) log.CreatedAt = now;
                continue;
            }

            if (entry.State == EntityState.Added && entry.Entity is IHasCreatedAt created && created.CreatedAt is null)
                created.CreatedAt = now;
            if (entry.State == EntityState.Modified && entry.Entity is IHasUpdatedAt updated)
                updated.UpdatedAt = now;
        }
    }

    /// <summary>
    /// Giữ nguyên tên của CSDL v3: cột snake_case (OrganizationId → organization_id), khóa ngoại
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
