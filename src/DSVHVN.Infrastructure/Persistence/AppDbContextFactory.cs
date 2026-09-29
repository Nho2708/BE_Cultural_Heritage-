using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DSVHVN.Infrastructure.Persistence;

/// <summary>
/// Cho công cụ <c>dotnet ef</c> tạo DbContext lúc thiết kế (tạo migration, update database) mà không phải khởi động cả Api.
/// Chuỗi kết nối lấy từ biến môi trường <c>ConnectionStrings__Default</c>, không có thì dùng LocalDB
/// <c>sep490_dsvhvn_v2_dev</c> của máy phát triển (CSDL mới của hướng tổ chức; không đụng dsvhvn_dev, sep490_dsvhvn_dev).
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public const string LocalDbConnection =
        "Server=(localdb)\\MSSQLLocalDB;Database=sep490_dsvhvn_v2_dev;Trusted_Connection=True;TrustServerCertificate=True";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(string.IsNullOrWhiteSpace(connection) ? LocalDbConnection : connection)
            .Options;
        return new AppDbContext(options, TimeProvider.System);
    }
}
