using DSVHVN.Application.Common;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DSVHVN.Infrastructure.Persistence;

/// <summary>
/// Chạy lúc khởi động Api:
/// 1) khởi tạo CSDL theo <c>Database:InitMode</c> (None | Migrate | EnsureCreated — EnsureCreated chỉ dùng cho kiểm thử);
/// 2) seed idempotent gói "Miễn phí" theo môi trường và các tài khoản ADMIN (mỗi thành viên một);
/// 3) nạp 34 tỉnh/thành và di sản từ tệp JSON khi cấu hình <c>Seed:HeritageData</c> (chỉ đặt ở Development).
/// 4 vai trò nằm trong migration (dữ liệu cố định). Mật khẩu ADMIN chỉ lấy từ cấu hình (User Secrets hoặc biến môi trường),
/// không có trong mã nguồn; thiếu mật khẩu thì bỏ qua tài khoản đó và ghi cảnh báo. Không có chức năng tạo ADMIN trên giao diện.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var config = sp.GetRequiredService<IConfiguration>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseInitializer));
        var db = sp.GetRequiredService<AppDbContext>();

        var mode = config["Database:InitMode"] ?? "None";
        switch (mode)
        {
            case "Migrate":
                await db.Database.MigrateAsync(ct);
                break;
            case "EnsureCreated":
                await db.Database.EnsureCreatedAsync(ct);
                break;
            case "None":
                break;
            default:
                throw new InvalidOperationException($"Database:InitMode '{mode}' không hợp lệ (None | Migrate | EnsureCreated).");
        }

        await SeedFreePlanAsync(db, config, logger, ct);
        await SeedSamplePlansAsync(db, config, logger, ct);
        await SeedPaymentProvidersAsync(db, logger, ct);
        await SeedAdminsAsync(db, config, sp.GetRequiredService<IPasswordHasher>(), logger, ct);
        await SeedHeritageDataAsync(db, config, sp.GetService<IHostEnvironment>()?.ContentRootPath, logger, ct);
        await BackfillHeritageSearchTextAsync(db, logger, ct);
    }

    /// <summary>
    /// Điền hai cột tìm kiếm không dấu cho di sản có trước migration thêm cột (còn trống). Dùng ExecuteUpdate để không chạm
    /// <c>updated_at</c> (thứ tự "mới cập nhật" của di sản nổi bật không bị xáo). Di sản lưu qua ứng dụng đã được điền sẵn nên
    /// bước này không làm gì sau lần đầu.
    /// </summary>
    private static async Task BackfillHeritageSearchTextAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var rows = await db.Heritages.AsNoTracking().Where(h => h.SearchText == null)
            .Select(h => new { h.Id, h.Name, h.ShortDescription, h.Address, h.Content }).ToListAsync(ct);
        foreach (var row in rows)
        {
            var name = SearchText.ComposeName(row.Name);
            var text = SearchText.Compose(row.Name, row.ShortDescription, row.Address, row.Content);
            await db.Heritages.Where(h => h.Id == row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.SearchName, name).SetProperty(h => h.SearchText, text), ct);
        }
        if (rows.Count > 0) logger.LogInformation("Điền cột tìm kiếm không dấu cho {Count} di sản", rows.Count);
    }

    /// <summary>
    /// <c>Seed:HeritageData:ProvincesFile</c> và <c>Seed:HeritageData:HeritagesFile</c>: đường dẫn tệp JSON (tương đối theo
    /// thư mục gốc của Api). Không cấu hình thì bỏ qua; cấu hình mà thiếu tệp thì ghi cảnh báo và bỏ qua.
    /// </summary>
    private static async Task SeedHeritageDataAsync(AppDbContext db, IConfiguration config, string? contentRoot, ILogger logger,
        CancellationToken ct)
    {
        var section = config.GetSection("Seed:HeritageData");
        var provincesFile = Resolve(section["ProvincesFile"], contentRoot);
        var heritagesFile = Resolve(section["HeritagesFile"], contentRoot);
        if (provincesFile is null || heritagesFile is null) return;
        if (!File.Exists(provincesFile) || !File.Exists(heritagesFile))
        {
            logger.LogWarning("Không thấy tệp dữ liệu di sản ({Provinces}, {Heritages}), bỏ qua bước nạp di sản",
                provincesFile, heritagesFile);
            return;
        }

        var result = await HeritageDataSeeder.SeedAsync(db, provincesFile, heritagesFile, ct);
        logger.LogInformation(
            "Nạp dữ liệu di sản: thêm {Provinces} tỉnh (đã có {ProvincesExisting}), {Heritages} di sản (đã có {HeritagesExisting}), " +
            "{Timelines} giai đoạn, {Events} sự kiện, {Media} ảnh, {References} nguồn",
            result.ProvincesAdded, result.ProvincesExisting, result.HeritagesAdded, result.HeritagesExisting,
            result.TimelinesAdded, result.EventsAdded, result.MediaAdded, result.ReferencesAdded);
        if (result.IgnoredFields.Count > 0)
            logger.LogInformation("Trường trong tệp không có cột tương ứng, không lưu: {Fields}", string.Join(", ", result.IgnoredFields));
        foreach (var rejected in result.Rejected)
            logger.LogWarning("Bỏ qua vì không khớp cột: {Reason}", rejected);
    }

    private static string? Resolve(string? path, string? contentRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return Path.IsPathRooted(path) || contentRoot is null ? path : Path.GetFullPath(Path.Combine(contentRoot, path));
    }

    /// <summary>
    /// Đúng một gói giá 0 tên "Miễn phí". Giá trị theo môi trường (<c>Seed:FreePlan</c>): appsettings.json
    /// (UAT, Release 1.0) 30 ngày, 2 giáo viên, 20 lượt AI; Development 365 ngày, 10 giáo viên, 300 lượt AI tới khi có chức năng quản lý gói.
    /// Đã có gói giá 0 thì không ghi đè (chức năng quản lý gói sẽ quản lý).
    /// </summary>
    private static async Task SeedFreePlanAsync(AppDbContext db, IConfiguration config, ILogger logger, CancellationToken ct)
    {
        var plans = await db.Plans.AsNoTracking().ToListAsync(ct);
        if (plans.Any(p => p.Price == 0m)) return;

        var section = config.GetSection("Seed:FreePlan");
        var plan = new Plan
        {
            Name = Plan.FreePlanName,
            Price = 0m,
            DurationDays = section.GetValue("DurationDays", 30),
            MaxTeachers = section.GetValue("MaxTeachers", 2),
            MaxAiRequests = section.GetValue("MaxAiRequests", 20),
            IsActive = true,
        };
        db.Plans.Add(plan);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seed gói {Plan}: {Days} ngày, {Teachers} giáo viên, {Ai} lượt AI",
            plan.Name, plan.DurationDays, plan.MaxTeachers, plan.MaxAiRequests);
    }

    /// <summary>
    /// <c>Seed:Plans</c>: danh sách gói trả phí mẫu { Name, Price, DurationDays, MaxTeachers, MaxAiRequests } cho môi trường phát triển và trình diễn
    /// (appsettings.json không khai báo nên UAT và Release 1.0 do quản trị hệ thống tự tạo trên màn hình Gói dịch vụ). Chỉ nạp khi chưa có gói trả phí nào;
    /// đã có thì không ghi đè, không thêm lại gói đã bị đổi tên.
    /// </summary>
    private static async Task SeedSamplePlansAsync(AppDbContext db, IConfiguration config, ILogger logger, CancellationToken ct)
    {
        var entries = config.GetSection("Seed:Plans").GetChildren().ToList();
        if (entries.Count == 0) return;
        var existing = await db.Plans.AsNoTracking().ToListAsync(ct);
        if (existing.Any(p => p.Price > 0m)) return;

        foreach (var entry in entries)
        {
            var name = entry["Name"]?.Trim();
            var price = entry.GetValue<decimal?>("Price");
            if (string.IsNullOrEmpty(name) || price is not > 0m || !BillingRules.IsValidPrice(price.Value))
            {
                logger.LogWarning("Seed:Plans: bỏ qua gói không hợp lệ (cần tên và giá nguyên đồng lớn hơn 0): {Name}", name);
                continue;
            }
            if (existing.Any(p => SearchText.Fold(p.Name) == SearchText.Fold(name))) continue;
            db.Plans.Add(new Plan
            {
                Name = name,
                Price = price,
                DurationDays = entry.GetValue("DurationDays", 365),
                MaxTeachers = entry.GetValue("MaxTeachers", 5),
                MaxAiRequests = entry.GetValue("MaxAiRequests", 100),
                IsActive = true,
            });
            logger.LogInformation("Seed gói mẫu {Plan}: {Price} đồng", name, price);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Danh mục cổng thanh toán: chỉ VNPay (sandbox) đang bật, các cổng còn lại có sẵn nhưng tắt. Chỉ thêm cổng còn thiếu theo mã, không đổi cổng đã có.
    /// </summary>
    private static async Task SeedPaymentProvidersAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var existing = (await db.PaymentProviders.AsNoTracking().Select(p => p.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = new (string Code, string Name, bool Active)[]
        {
            (BillingRules.VnPayProviderCode, "VNPay", true),
            ("MOMO", "MoMo", false),
            ("ZALOPAY", "ZaloPay", false),
            ("BANK_TRANSFER", "Chuyển khoản ngân hàng", false),
        };
        var added = 0;
        foreach (var (code, name, active) in wanted)
        {
            if (existing.Contains(code)) continue;
            db.PaymentProviders.Add(new PaymentProvider { Code = code, Name = name, IsActive = active });
            added++;
        }
        if (added == 0) return;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seed {Count} cổng thanh toán (chỉ VNPay đang bật)", added);
    }

    /// <summary>
    /// <c>Seed:Admins</c> là danh sách { Username, Email, FullName, Password }. Mật khẩu từng người, không có thì dùng
    /// <c>Seed:AdminPassword</c> chung. Đã có tài khoản cùng email hoặc username (chưa xóa mềm) thì bỏ qua, không ghi đè mật khẩu.
    /// </summary>
    private static async Task SeedAdminsAsync(
        AppDbContext db, IConfiguration config, IPasswordHasher hasher, ILogger logger, CancellationToken ct)
    {
        var sharedPassword = config["Seed:AdminPassword"];
        var added = false;
        foreach (var entry in config.GetSection("Seed:Admins").GetChildren())
        {
            var email = EmailAddress.Normalize(entry["Email"]);
            var username = UsernamePolicy.Normalize(entry["Username"]);
            if (email.Length == 0 || username.Length == 0) continue;

            if (!UsernamePolicy.IsSatisfiedBy(username))
            {
                logger.LogError("Seed:Admins: tên đăng nhập {Username} không đúng quy tắc tên đăng nhập, bỏ qua", username);
                continue;
            }

            var password = entry["Password"] is { Length: > 0 } own ? own : sharedPassword;
            if (string.IsNullOrEmpty(password))
            {
                logger.LogWarning("Chưa cấu hình mật khẩu cho ADMIN {Username} (Seed:AdminPassword), bỏ qua", username);
                continue;
            }
            if (!PasswordPolicy.IsSatisfiedBy(password))
            {
                logger.LogError("Mật khẩu seed của ADMIN {Username} không đúng quy tắc mật khẩu, bỏ qua", username);
                continue;
            }

            if (await db.Users.AnyAsync(u => u.DeletedAt == null && (u.Email == email || u.Username == username), ct)) continue;

            db.Users.Add(new User
            {
                OrganizationId = null,
                RoleId = Roles.AdminId,
                Username = username,
                Email = email,
                PasswordHash = hasher.Hash(password),
                MustChangePassword = false,
                FullName = entry["FullName"] is { Length: > 0 } name ? name.Trim() : Roles.NameOf(RoleCode.ADMIN),
                Status = UserStatus.ACTIVE,
            });
            added = true;
            logger.LogInformation("Seed tài khoản ADMIN {Username}", username);
        }

        if (added) await db.SaveChangesAsync(ct);
    }
}
