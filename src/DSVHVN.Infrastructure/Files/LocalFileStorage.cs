using DSVHVN.Application.Files;
using DSVHVN.Domain.Rules;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Files;

/// <summary>
/// Kho tệp lưu trên đĩa của máy chủ. Chỉ nhận khóa do hệ thống sinh (<see cref="StoredFileRules.IsValidKey"/>), nên không thể ghi hay đọc
/// ra ngoài thư mục gốc; ghi vào tệp tạm rồi đổi tên để người xem không bao giờ thấy tệp ghi dở.
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _publicBase;

    public LocalFileStorage(IOptions<StorageOptions> options, IHostEnvironment environment)
    {
        var value = options.Value;
        RootPath = Path.GetFullPath(value.Local.RootPath, environment.ContentRootPath);
        Directory.CreateDirectory(RootPath);
        _publicBase = value.PublicBaseUrl.Trim().TrimEnd('/');
    }

    /// <summary>Thư mục gốc đã đổi thành đường dẫn đủ; Api phục vụ tệp tĩnh từ đây.</summary>
    public string RootPath { get; }

    public async Task SaveAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, content.ToArray(), cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => Task.FromResult(File.Exists(PathOf(key)));

    public Task<long?> SizeOfAsync(string key, CancellationToken cancellationToken)
    {
        var info = new FileInfo(PathOf(key));
        return Task.FromResult<long?>(info.Exists ? info.Length : null);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public string PublicUrl(string key)
    {
        if (!StoredFileRules.IsValidKey(key)) throw new ArgumentException("Khóa tệp không hợp lệ.", nameof(key));
        return _publicBase + StoredFileRules.PublicPathPrefix + key;
    }

    private string PathOf(string key)
    {
        if (!StoredFileRules.IsValidKey(key)) throw new ArgumentException("Khóa tệp không hợp lệ.", nameof(key));
        var full = Path.GetFullPath(Path.Combine(RootPath, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Khóa tệp nằm ngoài thư mục lưu trữ.", nameof(key));
        return full;
    }
}
