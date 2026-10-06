using DSVHVN.Application;
using DSVHVN.Application.Common;
using DSVHVN.Application.Files;
using DSVHVN.Application.Models;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Identity;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Files;
using DSVHVN.Infrastructure.Persistence;
using DSVHVN.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Dịch vụ mô hình 3D trên SQLite trong bộ nhớ và kho tệp giả: đo dung lượng từ kho, chặn đường dẫn ngoài kho, chỉ quản trị hệ thống,
/// gỡ tệp vừa lưu khi không ghi được dòng, giữ tệp cũ khi thay hoặc gỡ.
/// </summary>
public sealed class HeritageModelServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly MemoryFileStorage _storage = new();
    private readonly ServiceProvider _root;
    private readonly long _heritageId;
    private readonly Actor _admin;

    public HeritageModelServiceTests()
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
        var user = new User
        {
            Username = "quantri", Email = "quantri@example.vn", PasswordHash = "x", FullName = "Quản trị", RoleId = Roles.AdminId, Status = UserStatus.ACTIVE,
        };
        var heritage = new Heritage { Name = "Văn Miếu", Slug = "van-mieu", Status = ContentStatus.PUBLISHED };
        db.Users.Add(user);
        db.Heritages.Add(heritage);
        db.SaveChanges();
        _heritageId = heritage.Id;
        _admin = new Actor(user.Id, RoleCode.ADMIN, null, "127.0.0.1");
    }

    public void Dispose()
    {
        _root.Dispose();
        _connection.Dispose();
    }

    private static HeritageModelRequest Declare(string? fileUrl = null, bool? show = null) =>
        new("Khuê Văn Các", fileUrl, null, "Nguyễn Văn An", "CC BY 4.0", null, "https://sketchfab.com/models/khue-van-cac", null, show);

    private async Task<T> RunAsync<T>(Func<HeritageModelService, Task<T>> action)
    {
        await using var scope = _root.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<HeritageModelService>());
    }

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = _root.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private string PutInStore(byte[]? content = null)
    {
        var key = StoredFileRules.ModelKey(Guid.NewGuid());
        _storage.Files[key] = content ?? FileSamples.Glb();
        return key;
    }

    [Fact]
    public async Task Declaring_by_stored_path_measures_the_size_from_the_store_and_keeps_the_address_of_the_store()
    {
        var glb = FileSamples.Glb(new Dictionary<string, object?> { ["extras"] = new { note = new string('x', 300) } });
        var key = PutInStore(glb);

        var model = await RunAsync(s => s.CreateAsync(_admin, _heritageId, Declare("/files/" + key), default));

        Assert.Equal(("/files/" + key, (long)glb.Length, ContentStatus.DRAFT), (model.FileUrl, model.FileSizeBytes, model.Status));
        var row = await DbAsync(db => db.HeritageModels.SingleAsync());
        Assert.Equal((ModelFormat.GLB, "Nguyễn Văn An · CC BY 4.0 · https://sketchfab.com/models/khue-van-cac"), (row.FileFormat, row.Credit));
    }

    [Theory]
    [InlineData("https://evil.example/files/models/{key}", "không thuộc kho tệp")]
    [InlineData("/files/models/{missing}", "không có tệp này trong kho")]
    public async Task A_path_that_is_not_a_file_of_this_store_is_refused_and_nothing_is_recorded(string template, string reason)
    {
        var key = PutInStore();
        var url = template.Replace("models/{key}", key).Replace("models/{missing}", StoredFileRules.ModelKey(Guid.NewGuid()));

        var ex = await Assert.ThrowsAsync<AppException>(() => RunAsync(s => s.CreateAsync(_admin, _heritageId, Declare(url), default)));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains(reason, ex.Errors!["fileUrl"]);
        Assert.Equal(0, await DbAsync(db => db.HeritageModels.CountAsync()));
    }

    [Fact]
    public async Task Only_the_system_admin_may_use_the_service_and_nothing_is_stored_for_others()
    {
        var teacher = new Actor(99, RoleCode.TEACHER, 1, null);
        var key = PutInStore();

        var calls = new Func<HeritageModelService, Task<object?>>[]
        {
            async s => await s.CreateAsync(teacher, _heritageId, Declare("/files/" + key), default),
            async s => await s.UploadAsync(teacher, _heritageId, Declare(), new UploadedFile("a.glb", FileSamples.Glb()), default),
            async s => await s.ShowAsync(teacher, 1, default),
            async s => await s.HideAsync(teacher, 1, default),
            async s => { await s.RemoveAsync(teacher, 1, default); return null; },
        };
        foreach (var call in calls)
        {
            var ex = await Assert.ThrowsAsync<AppException>(() => RunAsync(call));
            Assert.Equal(403, ex.StatusCode);
        }
        Assert.Single(_storage.Files);   // chỉ tệp đã đặt sẵn
    }

    [Fact]
    public async Task A_refused_file_or_credit_is_checked_before_anything_is_stored()
    {
        var gif = await Assert.ThrowsAsync<AppException>(() =>
            RunAsync(s => s.UploadAsync(_admin, _heritageId, Declare(), new UploadedFile("a.glb", FileSamples.Gif()), default)));
        var license = await Assert.ThrowsAsync<AppException>(() =>
            RunAsync(s => s.UploadAsync(_admin, _heritageId, Declare() with { License = "CC BY-NC 4.0" }, new UploadedFile("a.glb", FileSamples.Glb()), default)));

        Assert.Equal((400, 400), (gif.StatusCode, license.StatusCode));
        Assert.Empty(_storage.Files);
        Assert.Equal(0, await DbAsync(db => db.HeritageModels.CountAsync()));
    }

    [Fact]
    public async Task A_storage_failure_while_uploading_records_nothing()
    {
        _storage.FailOnSave = 1;

        await Assert.ThrowsAsync<IOException>(() =>
            RunAsync(s => s.UploadAsync(_admin, _heritageId, Declare(), new UploadedFile("a.glb", FileSamples.Glb()), default)));

        Assert.Empty(_storage.Files);
        Assert.Equal(0, await DbAsync(db => db.HeritageModels.CountAsync()));
        Assert.Equal(0, await DbAsync(db => db.AuditLogs.CountAsync()));
    }

    [Fact]
    public async Task When_another_model_becomes_visible_during_the_upload_the_new_file_is_removed_and_nothing_is_recorded()
    {
        // Chen một mô hình đang hiển thị vào đúng lúc tệp vừa được lưu, như khi hai quản trị cùng thao tác.
        _storage.AfterSave = _ =>
        {
            using var scope = _root.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.HeritageModels.Add(new HeritageModel
            {
                HeritageId = _heritageId, FileUrl = "/files/" + StoredFileRules.ModelKey(Guid.NewGuid()), FileSizeBytes = 10, LicenseName = "CC0", Author = "An",
                Credit = "An · CC0 · Nhóm thực hiện", Status = ContentStatus.PUBLISHED,
            });
            db.SaveChanges();
        };

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            RunAsync(s => s.UploadAsync(_admin, _heritageId, Declare(show: true), new UploadedFile("a.glb", FileSamples.Glb()), default)));

        Assert.Equal((422, MessageCodes.Blocked), (ex.StatusCode, ex.AppMessage.Code));
        Assert.Empty(_storage.Files);   // tệp vừa lưu đã được gỡ lại
        Assert.Equal(1, await DbAsync(db => db.HeritageModels.CountAsync()));
        Assert.Equal(0, await DbAsync(db => db.AuditLogs.CountAsync()));
    }

    [Fact]
    public async Task Replacing_the_file_keeps_the_old_one_and_a_failed_replacement_changes_nothing()
    {
        var created = await RunAsync(s => s.UploadAsync(_admin, _heritageId, Declare(), new UploadedFile("a.glb", FileSamples.Glb()), default));
        var oldKey = HeritageModelRules.StoredModelKey(created.FileUrl)!;

        var replaced = await RunAsync(s => s.ReplaceFileAsync(_admin, created.Id, new UploadedFile("b.glb", FileSamples.Glb()), default));
        Assert.NotEqual(created.FileUrl, replaced.FileUrl);
        Assert.True(_storage.Files.ContainsKey(oldKey));
        Assert.Equal(2, _storage.Files.Count);

        await Assert.ThrowsAsync<AppException>(() => RunAsync(s => s.ReplaceFileAsync(_admin, created.Id, new UploadedFile("c.glb", FileSamples.Png()), default)));
        Assert.Equal(replaced.FileUrl, (await DbAsync(db => db.HeritageModels.SingleAsync())).FileUrl);
        Assert.Equal(2, _storage.Files.Count);
    }

    [Fact]
    public async Task Removing_a_model_is_a_soft_delete_that_keeps_the_file_and_frees_the_visible_place()
    {
        var first = await RunAsync(s => s.UploadAsync(_admin, _heritageId, Declare(show: true), new UploadedFile("a.glb", FileSamples.Glb()), default));

        await RunAsync(async s => { await s.RemoveAsync(_admin, first.Id, default); return 0; });
        var second = await RunAsync(s => s.UploadAsync(_admin, _heritageId, Declare(show: true), new UploadedFile("b.glb", FileSamples.Glb()), default));

        Assert.Equal(2, _storage.Files.Count);
        var rows = await DbAsync(db => db.HeritageModels.OrderBy(m => m.Id).ToListAsync());
        Assert.NotNull(rows[0].DeletedAt);
        Assert.Equal((second.Id, null), (rows[1].Id, rows[1].DeletedAt));
        Assert.Equal([second.Id], (await RunAsync(s => s.ListAsync(_heritageId, default))).Select(m => m.Id));
        var ex = await Assert.ThrowsAsync<AppException>(() => RunAsync(s => s.ShowAsync(_admin, first.Id, default)));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task The_visible_flag_follows_the_status_of_the_heritage()
    {
        var model = await RunAsync(s => s.UploadAsync(_admin, _heritageId, Declare(show: true), new UploadedFile("a.glb", FileSamples.Glb()), default));
        Assert.True(model.IsVisibleToPublic);

        await DbAsync(async db =>
        {
            (await db.Heritages.SingleAsync()).Status = ContentStatus.ARCHIVED;
            return await db.SaveChangesAsync();
        });

        var listed = (await RunAsync(s => s.ListAsync(_heritageId, default))).Single();
        Assert.Equal((ContentStatus.PUBLISHED, false), (listed.Status, listed.IsVisibleToPublic));
    }
}
