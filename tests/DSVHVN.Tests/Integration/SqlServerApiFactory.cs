using Microsoft.Data.SqlClient;

namespace DSVHVN.Tests.Integration;

/// <summary>SQL Server LocalDB của máy phát triển; kiểm thử trên CSDL thật chỉ chạy khi có LocalDB.</summary>
public static class LocalDb
{
    public const string Server = "(localdb)\\MSSQLLocalDB";

    private static readonly Lazy<bool> Reachable = new(() =>
    {
        try
        {
            using var connection = new SqlConnection(ConnectionString("master") + ";Connect Timeout=15");
            connection.Open();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    });

    public static bool Available => Reachable.Value;

    public static string ConnectionString(string database) =>
        $"Server={Server};Database={database};Trusted_Connection=True;TrustServerCertificate=True";

    /// <summary>Tên database tạm riêng cho mỗi lần chạy; không bao giờ trùng CSDL thật của nhóm.</summary>
    public static string NewTemporaryName() => "dsvhvn_kiem_thu_" + Guid.NewGuid().ToString("N")[..12];

    public static void Drop(string database)
    {
        if (!database.StartsWith("dsvhvn_kiem_thu_", StringComparison.Ordinal))
            throw new InvalidOperationException($"Chỉ xóa database tạm của kiểm thử, không xóa {database}.");
        SqlConnection.ClearAllPools();
        using var connection = new SqlConnection(ConnectionString("master"));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"IF DB_ID('{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END";
        command.ExecuteNonQuery();
    }

    public static bool Exists(string database)
    {
        using var connection = new SqlConnection(ConnectionString("master"));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @name";
        command.Parameters.AddWithValue("@name", database);
        return (int)command.ExecuteScalar()! > 0;
    }
}

/// <summary>Bỏ qua (Skip) test khi máy không có SQL Server LocalDB, để bộ test vẫn chạy được ở máy khác.</summary>
public sealed class LocalDbFactAttribute : FactAttribute
{
    public LocalDbFactAttribute()
    {
        if (!LocalDb.Available) Skip = "Máy này không có SQL Server LocalDB.";
    }
}

/// <summary>Như <see cref="LocalDbFactAttribute"/>, thêm điều kiện có thư mục dữ liệu di sản của nhóm (doc/du-lieu-di-san).</summary>
public sealed class LocalDbHeritageDataFactAttribute : FactAttribute
{
    public LocalDbHeritageDataFactAttribute()
    {
        if (!LocalDb.Available) Skip = "Máy này không có SQL Server LocalDB.";
        else if (SqlServerApiFactory.HeritageDataDirectory is null) Skip = "Không thấy thư mục dữ liệu di sản doc/du-lieu-di-san.";
    }
}

/// <summary>
/// Api chạy trên một database tạm của SQL Server LocalDB: tạo bằng migration khởi tạo khi Api khởi động,
/// nạp dữ liệu tỉnh/thành và di sản nếu tìm thấy tệp dữ liệu của nhóm, và tự xóa database khi xong.
/// </summary>
public sealed class SqlServerApiFactory : ApiFactory
{
    public SqlServerApiFactory() : this(LocalDb.NewTemporaryName()) { }

    private SqlServerApiFactory(string databaseName) : base(LocalDb.ConnectionString(databaseName)) =>
        DatabaseName = databaseName;

    public string DatabaseName { get; }

    /// <summary>Thư mục dữ liệu di sản của nhóm (doc/du-lieu-di-san), tìm ngược lên từ thư mục chạy test; null nếu không có.</summary>
    public static string? HeritageDataDirectory { get; } = FindHeritageData();

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings => HeritageDataDirectory is null
        ? []
        :
        [
            new("Seed:HeritageData:ProvincesFile", Path.Combine(HeritageDataDirectory, "provinces.json")),
            new("Seed:HeritageData:HeritagesFile", Path.Combine(HeritageDataDirectory, "seed-dot-1.json")),
        ];

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && LocalDb.Available) LocalDb.Drop(DatabaseName);
    }

    private static string? FindHeritageData()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "doc", "du-lieu-di-san");
            if (File.Exists(Path.Combine(candidate, "seed-dot-1.json")) && File.Exists(Path.Combine(candidate, "provinces.json")))
                return candidate;
        }
        return null;
    }
}
