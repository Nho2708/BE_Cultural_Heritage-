using DSVHVN.Application;
using DSVHVN.Application.Common;
using DSVHVN.Application.Files;
using DSVHVN.Application.Heritages;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Files;
using DSVHVN.Infrastructure.Persistence;
using DSVHVN.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DSVHVN.Tests.Unit;

/// <summary>Kho tệp giả trong bộ nhớ, ép được lỗi ở lần ghi thứ n để kiểm đường hoàn tác.</summary>
internal sealed class MemoryFileStorage : IFileStorage
{
    public Dictionary<string, byte[]> Files { get; } = [];
    public int? FailOnSave { get; set; }
    /// <summary>Chạy ngay sau mỗi lần ghi thành công, để kiểm thử chen một thay đổi vào giữa các bước của dịch vụ.</summary>
    public Action<string>? AfterSave { get; set; }
    private int _saves;

    public Task SaveAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (++_saves == FailOnSave) throw new IOException("Đĩa đầy (giả lập).");
        Files[key] = content.ToArray();
        AfterSave?.Invoke(key);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Files.ContainsKey(key));

    public Task<long?> SizeOfAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<long?>(Files.TryGetValue(key, out var content) ? content.Length : null);

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        Files.Remove(key);
        return Task.CompletedTask;
    }

    public string PublicUrl(string key) => "/files/" + key;
}

/// <summary>Dịch vụ thư viện ảnh khi tải tệp lên: lưu hai tệp rồi ghi dòng, hoàn tác khi lỗi, giữ tệp khi xóa ảnh.</summary>
public sealed class HeritageMediaUploadTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly MemoryFileStorage _storage = new();
    private readonly ServiceProvider _root;
    private readonly long _heritageId;

    public HeritageMediaUploadTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddApplication();
        services.AddSingleton<IFileStorage>(_storage);
        services.AddSingleton<IImageProcessor, SkiaImageProcessor>();
        _root = services.BuildServiceProvider();

        using var scope = _root.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        var heritage = new Heritage { Name = "Thành cổ", Slug = "thanh-co", Status = ContentStatus.DRAFT };
        db.Heritages.Add(heritage);
        db.SaveChanges();
        _heritageId = heritage.Id;
    }

    public void Dispose()
    {
        _root.Dispose();
        _connection.Dispose();
    }

    private static UploadMediaRequest Request(bool? thumbnail = null) =>
        new("Cổng thành", "Nguyễn Văn An", "CC BY 4.0", "Nhóm tự chụp", null, thumbnail);

    private async Task<T> RunAsync<T>(Func<HeritageMediaService, Task<T>> action)
    {
        await using var scope = _root.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<HeritageMediaService>());
    }

    [Fact]
    public async Task A_successful_upload_stores_the_image_and_its_preview_and_records_one_row()
    {
        var dto = await RunAsync(s => s.UploadToHeritageAsync(_heritageId, Request(thumbnail: true),
            new UploadedFile("a.png", FileSamples.WithExif(FileSamples.Jpeg(), 1)), default));

        Assert.Equal(2, _storage.Files.Count);
        Assert.True(_storage.Files.ContainsKey(dto.Url!["/files/".Length..]));
        Assert.True(_storage.Files.ContainsKey(dto.PreviewUrl!["/files/".Length..]));
        Assert.True(dto.IsThumbnail);
        await using var scope = _root.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(dto.Url, (await db.Heritages.SingleAsync()).ThumbnailUrl);
        Assert.Equal(1, await db.Media.CountAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_failed_save_removes_whatever_was_already_stored_and_records_nothing(int failingSave)
    {
        _storage.FailOnSave = failingSave;

        await Assert.ThrowsAsync<IOException>(() => RunAsync(s => s.UploadToHeritageAsync(_heritageId, Request(),
            new UploadedFile("a.jpg", FileSamples.Jpeg()), default)));

        Assert.Empty(_storage.Files);
        await using var scope = _root.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Media.CountAsync());
    }

    [Fact]
    public async Task Removing_the_photo_does_not_touch_the_stored_files()
    {
        var dto = await RunAsync(s => s.UploadToHeritageAsync(_heritageId, Request(),
            new UploadedFile("a.jpg", FileSamples.Jpeg()), default));

        await RunAsync(async s => { await s.DeleteAsync(dto.Id, default); return 0; });

        Assert.Equal(2, _storage.Files.Count);
        await using var scope = _root.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Media.CountAsync());
    }

    [Fact]
    public async Task A_refused_file_is_checked_before_anything_is_stored()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => RunAsync(s => s.UploadToHeritageAsync(_heritageId, Request(),
            new UploadedFile("a.jpg", FileSamples.Gif()), default)));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("file", ex.Errors!.Keys);
        Assert.Empty(_storage.Files);
    }

    [Fact]
    public async Task A_valid_model_is_stored_under_a_generated_name_and_never_under_the_given_one()
    {
        await using var scope = _root.CreateAsyncScope();
        var models = new ModelFileService(_storage);

        var stored = await models.UploadAsync(new UploadedFile("../../ten-nguy-hiem.glb", FileSamples.Glb()), default);

        var key = Assert.Single(_storage.Files.Keys);
        Assert.True(StoredFileRules.IsValidKey(key));
        Assert.Equal("/files/" + key, stored.Url);
    }
}
