using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DSVHVN.Application;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Billing;
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

/// <summary>Giữ thư lại (thay bản ghi log) để kiểm thử đọc token trong liên kết đặt mật khẩu.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<(string To, string Subject, string Body)> Sent { get; } = new();

    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        Sent.Enqueue((to, subject, body));
        return Task.CompletedTask;
    }

    public int CountTo(string email) => Sent.Count(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase));

    public (string To, string Subject, string Body)? LastTo(string email)
    {
        var mail = Sent.LastOrDefault(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase));
        return mail.Body is null ? null : mail;
    }

    public string? LastTokenFor(string email)
    {
        var mail = LastTo(email);
        if (mail is null) return null;
        var match = Regex.Match(mail.Value.Body, @"token=([A-Za-z0-9_\-%]+)");
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
                    StartDate = today, EndDate = Subscription.EndDateFor(today, plan.DurationDays),
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

    public void Dispose()
    {
        _root.Dispose();
        _connection.Dispose();
    }
}
