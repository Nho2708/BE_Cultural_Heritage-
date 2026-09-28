using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DSVHVN.Application.Common;
using DSVHVN.Infrastructure.Persistence;
using DSVHVN.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Chạy cả Api thật (middleware, JWT, kiểm tài khoản mỗi yêu cầu, policy, controller). Mặc định trên SQLite trong bộ nhớ,
/// lược đồ tạo bằng EnsureCreated từ cùng model EF. Lớp con <see cref="SqlServerApiFactory"/> chạy trên một database tạm
/// của SQL Server LocalDB tạo bằng chính migration và tự xóa khi xong.
/// Seed như môi trường thật: gói "Miễn phí" (ở đây 2 giáo viên) và một ADMIN lấy mật khẩu từ cấu hình.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string AdminUsername = "admin.test";
    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "SeedPass2026";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly string? _sqlServerConnection;

    public ApiFactory() { }

    /// <param name="sqlServerConnection">Chuỗi kết nối SQL Server; có thì dùng SQL Server và áp migration thay cho SQLite.</param>
    protected ApiFactory(string sqlServerConnection) => _sqlServerConnection = sqlServerConnection;

    public CapturingEmailSender Mail { get; } = new();

    /// <summary>Cấu hình thêm của lớp con (vd đường dẫn tệp dữ liệu di sản).</summary>
    protected virtual IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var sqlServer = _sqlServerConnection is not null;
        if (!sqlServer) _connection.Open();
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = _sqlServerConnection ?? "sqlite-in-memory",
            ["Database:InitMode"] = sqlServer ? "Migrate" : "EnsureCreated",
            ["Jwt:SigningKey"] = "integration-test-signing-key-0123456789-abcdef",
            ["Seed:FreePlan:DurationDays"] = "30",
            ["Seed:FreePlan:MaxTeachers"] = "2",
            ["Seed:FreePlan:MaxAiRequests"] = "20",
            ["Seed:AdminPassword"] = AdminPassword,
            ["Seed:Admins:0:Username"] = AdminUsername,
            ["Seed:Admins:0:Email"] = AdminEmail,
            ["Seed:Admins:0:FullName"] = "Quản trị kiểm thử",
        }.Concat(ExtraSettings)));

        builder.ConfigureTestServices(services =>
        {
            if (!sqlServer)
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
                services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
            }

            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Mail);

            // TestServer không có IP client; gán sẵn để kiểm được IP đi vào audit_logs.
            services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>();
        });
    }

    private sealed class FakeRemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress ??= IPAddress.Parse("203.0.113.10");
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}

/// <summary>Khuôn phản hồi chung phía client kiểm thử.</summary>
public sealed record Envelope<T>(bool Success, T? Data, string? Message, string? ErrorCode, string? TraceId,
    Dictionary<string, string>? Errors);

public static class HttpTestExtensions
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<(HttpResponseMessage Response, Envelope<T> Body)> SendJsonAsync<T>(
        this HttpClient client, HttpMethod method, string url, object? body = null, string? accessToken = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.SendAsync(request);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(Json);
        return (response, envelope!);
    }

    public static Task<(HttpResponseMessage Response, Envelope<T> Body)> PostAsync<T>(
        this HttpClient client, string url, object? body, string? accessToken = null) =>
        client.SendJsonAsync<T>(HttpMethod.Post, url, body ?? new { }, accessToken);

    public static Task<(HttpResponseMessage Response, Envelope<T> Body)> PutAsync<T>(
        this HttpClient client, string url, object body, string? accessToken = null) =>
        client.SendJsonAsync<T>(HttpMethod.Put, url, body, accessToken);

    public static Task<(HttpResponseMessage Response, Envelope<T> Body)> GetAsync<T>(
        this HttpClient client, string url, string? accessToken = null) =>
        client.SendJsonAsync<T>(HttpMethod.Get, url, accessToken: accessToken);

    public static Task<(HttpResponseMessage Response, Envelope<T> Body)> DeleteAsync<T>(
        this HttpClient client, string url, string? accessToken = null) =>
        client.SendJsonAsync<T>(HttpMethod.Delete, url, accessToken: accessToken);
}
