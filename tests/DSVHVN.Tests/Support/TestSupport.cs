using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DSVHVN.Application;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Organizations;
using DSVHVN.Infrastructure.Email;
using DSVHVN.Infrastructure.Persistence;
using DSVHVN.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace DSVHVN.Tests.Support;

/// <summary>Đồng hồ tua được để kiểm thử hạn token, thời gian tạm khóa và ngày của gói mà không phải chờ thật.</summary>
public sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    /// <summary>01:00 UTC = 08:00 giờ Việt Nam ngày 29/09/2026.</summary>
    public MutableTimeProvider() : this(new DateTimeOffset(2026, 9, 29, 1, 0, 0, TimeSpan.Zero)) { }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset value) => _now = value;
}

/// <summary>
/// Giữ thư lại (thay bản ghi log) để kiểm thử đọc token trong liên kết đặt mật khẩu. Bật <see cref="FailWith"/> để giả
/// máy chủ SMTP từ chối hoặc mất mạng.
/// </summary>
public sealed class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    /// <summary>Có giá trị thì mọi lần gửi ném ngoại lệ này và không giữ thư.</summary>
    public Exception? FailWith { get; set; }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (FailWith is { } error) return Task.FromException(error);
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public int CountTo(string email) => Sent.Count(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase));

    public EmailMessage? LastTo(string email) =>
        Sent.LastOrDefault(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase));

    public string? LastTokenFor(string email)
    {
        var mail = LastTo(email);
        if (mail is null) return null;
        var match = Regex.Match(mail.TextBody, @"token=([A-Za-z0-9_\-%]+)");
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }
}

public sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "DSVHVN.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>
/// Ghép tầng Application với thành phần hạ tầng thật (băm PBKDF2, JWT, bộ đếm khóa, AccountMailer) trên SQLite
/// trong bộ nhớ; chỉ thay đồng hồ và hộp thư. Mỗi <see cref="RunAsync{T}"/> là một scope mới, như một yêu cầu HTTP.
/// </summary>
public sealed class ServiceHarness : IDisposable
{
    public const string Password = "MatKhau2026";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _root;

    public ServiceHarness(int freePlanMaxTeachers = 2)
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddApplication();
        services.AddSingleton<IHostEnvironment, TestHostEnvironment>();
        services.Configure<JwtOptions>(o => o.SigningKey = "unit-test-signing-key-0123456789-abcdef");
        services.Configure<AppLinkOptions>(_ => { });
        services.AddSingleton<JwtSigningKeyProvider>();
        services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
        services.AddSingleton<IAuthThrottle, InMemoryAuthThrottle>();
        services.AddSingleton<IEmailSender>(Mail);
        services.AddScoped<IAccountMailer, AccountMailer>();
        _root = services.BuildServiceProvider();

        using var scope = _root.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        db.Plans.Add(new Plan
        {
            Name = Plan.FreePlanName, Price = 0m, DurationDays = 30, MaxTeachers = freePlanMaxTeachers, MaxAiRequests = 20,
        });
        db.SaveChanges();
    }

    public MutableTimeProvider Clock { get; } = new();
    public CapturingEmailSender Mail { get; } = new();

    public IPasswordHasher Hasher => _root.GetRequiredService<IPasswordHasher>();

    public async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _root.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public async Task RunAsync(Func<IServiceProvider, Task> action)
    {
        await using var scope = _root.CreateAsyncScope();
        await action(scope.ServiceProvider);
    }

    public Task<T> QueryAsync<T>(Func<AppDbContext, Task<T>> query) =>
        RunAsync(sp => query(sp.GetRequiredService<AppDbContext>()));

    public Task<T> Service<TService, T>(Func<TService, Task<T>> action) where TService : notnull =>
        RunAsync(sp => action(sp.GetRequiredService<TService>()));

    public Task Service<TService>(Func<TService, Task> action) where TService : notnull =>
        RunAsync(sp => action(sp.GetRequiredService<TService>()));

    public Task<Organization> AddOrganizationAsync(string name = "Trường THCS Thử Nghiệm", bool withFreePlan = true) =>
        QueryAsync(async db =>
        {
            var org = new Organization { Name = name };
            db.Organizations.Add(org);
            await db.SaveChangesAsync();
            if (withFreePlan)
            {
                var plan = await db.Plans.FirstAsync();
                var today = Domain.Rules.VietnamTime.Today(Clock.GetUtcNow());
                db.Subscriptions.Add(new Subscription
                {
                    OrganizationId = org.Id, PlanId = plan.Id, Status = SubscriptionStatus.ACTIVE,
                    StartDate = today, EndDate = Subscription.EndDateFor(today, plan.DurationDays!.Value),
                });
                await db.SaveChangesAsync();
            }
            return org;
        });

    public Task<User> AddUserAsync(string username, RoleCode role, long? organizationId, string password = Password,
        UserStatus status = UserStatus.ACTIVE) =>
        QueryAsync(async db =>
        {
            var user = new User
            {
                Username = username,
                Email = username + "@example.vn",
                PasswordHash = Hasher.Hash(password),
                FullName = "Người dùng " + username,
                RoleId = Roles.IdOf(role),
                OrganizationId = organizationId,
                Status = status,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return user;
        });

    /// <summary>
    /// Học sinh có tài khoản như cách giáo viên thêm vào lớp: vai trò học sinh, tên đăng nhập = mã học sinh, không email,
    /// buộc đổi mật khẩu. Lớp 8A1 của giáo viên được tạo lần đầu gọi.
    /// </summary>
    public Task<(User Account, Student Student)> AddStudentAsync(long organizationId, long teacherId,
        string studentCode = "HS8K2QX7", string fullName = "Nguyễn Văn An") =>
        QueryAsync(async db =>
        {
            var schoolClass = await db.Classes.FirstOrDefaultAsync(c => c.TeacherId == teacherId);
            if (schoolClass is null)
            {
                schoolClass = new SchoolClass
                {
                    OrganizationId = organizationId, TeacherId = teacherId, Name = "8A1", Grade = 8, SchoolYear = "2026-2027",
                };
                db.Classes.Add(schoolClass);
            }

            var account = new User
            {
                OrganizationId = organizationId,
                RoleId = Roles.StudentId,
                Username = studentCode,
                Email = null,
                PasswordHash = Hasher.Hash(Password),
                MustChangePassword = true,
                FullName = fullName,
                Status = UserStatus.ACTIVE,
            };
            db.Users.Add(account);
            await db.SaveChangesAsync();

            var student = new Student
            {
                ClassId = schoolClass.Id, UserId = account.Id, StudentCode = studentCode, FullName = fullName, IsActive = true,
            };
            db.Students.Add(student);
            await db.SaveChangesAsync();
            return (account, student);
        });

    public void Dispose()
    {
        _root.Dispose();
        _connection.Dispose();
    }
}
