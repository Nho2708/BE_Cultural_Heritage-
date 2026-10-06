using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Models;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static DSVHVN.Tests.Integration.DiscoveryTestKit;
using static DSVHVN.Tests.Integration.HeritageModelTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Mô hình 3D trên SQL Server thật (database tạm của LocalDB, lược đồ do migration tạo): luồng tải, hiện, đọc công khai và lọc danh sách quản lý,
/// ràng buộc của chính CSDL (CHECK, chỉ mục duy nhất có lọc, khóa ngoại) và hai quản trị cùng hiện hai mô hình một lúc.
/// </summary>
public sealed class HeritageModelSqlServerTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>
{
    private readonly HeritageModelTestKit _k = new(factory);
    private HttpClient Http => _k.Http;

    [LocalDbFact]
    public async Task Upload_show_read_and_filter_work_end_to_end_on_sql_server()
    {
        var admin = await _k.AdminAsync();
        var tag = Unique();
        var with = await _k.Discovery.PublishedAsync(admin, new Spec($"Có mô hình 3D {tag}"));
        var without = await _k.Discovery.PublishedAsync(admin, new Spec($"Chưa có mô hình 3D {tag}"));
        var glb = FileSamples.Glb();

        var model = await _k.UploadModelAsync(admin, with.Id, show: true, glb: glb,
            fields: Credit(author: "Nguyễn Thị Hương", license: "CC BY-SA 4.0", sourceUrl: "https://sketchfab.com/models/văn-miếu"));

        // Chữ tiếng Việt và dấu "·" của dòng ghi công lưu và đọc lại nguyên vẹn.
        var row = await factory.WithDbAsync(db => db.HeritageModels.AsNoTracking().SingleAsync(m => m.Id == model.Id));
        Assert.Equal(("Nguyễn Thị Hương · CC BY-SA 4.0 · https://sketchfab.com/models/văn-miếu", "Khuê Văn Các", ModelFormat.GLB, (long)glb.Length),
            (row.Credit, row.Title, row.FileFormat, row.FileSizeBytes));
        Assert.Equal((ContentStatus.PUBLISHED, null), (row.Status, row.DeletedAt));
        Assert.NotNull(row.CreatedAt);

        Assert.True(await _k.HasModelAsync(with.Slug!));
        Assert.False(await _k.HasModelAsync(without.Slug!));
        Assert.Equal(model.Credit, (await _k.VisibleModelAsync(with.Slug!)).Credit);

        async Task<List<long>> Ids(string query) =>
            (await Expect(Http.GetAsync<PagedResult<HeritageSummaryDto>>(
                $"{AdminRoot}/heritages?keyword={Uri.EscapeDataString(tag)}&pageSize=50{query}", admin))).Items.Select(h => h.Id).Order().ToList();
        Assert.Equal([with.Id, without.Id], await Ids(string.Empty));
        Assert.Equal([with.Id], await Ids("&hasModel=true"));
        Assert.Equal([without.Id], await Ids("&hasModel=false"));

        // Ẩn rồi xóa mềm: không còn ở đâu, tệp vẫn nằm trong kho.
        await _k.HideAsync(admin, model.Id);
        Assert.Empty(await Ids("&hasModel=true"));
        await Expect(Http.DeleteAsync<object>($"{AdminRoot}/models/{model.Id}", admin));
        Assert.True(File.Exists(_k.FilePath(model.FileUrl)));
        Assert.NotNull((await factory.WithDbAsync(db => db.HeritageModels.AsNoTracking().SingleAsync(m => m.Id == model.Id))).DeletedAt);
        Assert.Equal(3, await factory.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == AuditAction.HERITAGE_MODEL_CHANGED && l.TargetId == model.Id)));   // thêm, ẩn, gỡ
    }

    [LocalDbFact]
    public async Task The_database_itself_allows_one_visible_model_per_heritage_and_refuses_bad_rows()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.DraftHeritageAsync(admin);
        var other = await _k.DraftHeritageAsync(admin);
        var now = DateTime.UtcNow;

        async Task Insert(long heritageId, string status = "PUBLISHED", long size = 100, string format = "GLB", DateTime? deletedAt = null) =>
            await factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO heritage_models (heritage_id, file_url, file_format, file_size_bytes, license_name, author, credit, status, created_at, deleted_at)
                VALUES ({heritageId}, N'/files/models/x.glb', {format}, {size}, N'CC0', N'An', N'An · CC0 · Nhóm thực hiện', {status}, {now}, {deletedAt})"));
        async Task Refused(Func<Task> action, string constraint)
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(action);
            var inner = error as SqlException ?? error.InnerException as SqlException;
            Assert.NotNull(inner);
            Assert.Contains(constraint, inner!.Message);
        }

        await Insert(heritage.Id);
        await Refused(() => Insert(heritage.Id), "UX_heritage_models_visible");               // hai mô hình cùng hiển thị
        await Insert(heritage.Id, status: "DRAFT");                                           // nháp, đã ẩn, đã gỡ thì không chiếm chỗ
        await Insert(heritage.Id, status: "ARCHIVED");
        await Insert(heritage.Id, deletedAt: now);
        await Insert(other.Id);                                                               // di sản khác có mô hình hiển thị riêng
        await Refused(() => Insert(other.Id, status: "DRAFT", size: 0), "CK_heritage_models_file_size");
        await Refused(() => Insert(other.Id, status: "DRAFT", size: -5), "CK_heritage_models_file_size");
        await Refused(() => Insert(other.Id, status: "HIDDEN"), "CK_heritage_models_status");
        await Refused(() => Insert(other.Id, status: "DRAFT", format: "GLTF"), "CK_heritage_models_file_format");
        await Refused(() => Insert(long.MaxValue, status: "DRAFT"), "fk_heritage_models_heritage_id");

        // Di sản chỉ có mô hình (không ảnh, không giai đoạn) không xóa cứng được vì còn mô hình trỏ tới.
        var bare = "tran-quoc-" + Unique();
        await factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO heritages (name, slug, status, created_at) VALUES (N'Trấn Quốc', {bare}, 'DRAFT', {now})"));
        var bareId = await factory.WithDbAsync(db => db.Heritages.Where(h => h.Slug == bare).Select(h => h.Id).SingleAsync());
        await Insert(bareId, status: "DRAFT");
        await Refused(() => factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM heritages WHERE id = {bareId}")),
            "fk_heritage_models_heritage_id");

        Assert.Equal(4, await factory.WithDbAsync(db => db.HeritageModels.CountAsync(m => m.HeritageId == heritage.Id)));
        // Ẩn mô hình đang hiển thị thì mô hình khác hiện được.
        await factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE heritage_models SET status = 'ARCHIVED' WHERE heritage_id = {heritage.Id} AND status = 'PUBLISHED'"));
        await Insert(heritage.Id);
    }

    [LocalDbFact]
    public async Task Two_admins_showing_two_models_at_the_same_time_leave_exactly_one_visible()
    {
        var admin = await _k.AdminAsync();
        for (var round = 0; round < 3; round++)
        {
            var heritage = await _k.PublishedHeritageAsync(admin);
            var a = await _k.UploadModelAsync(admin, heritage.Id);
            var b = await _k.UploadModelAsync(admin, heritage.Id);

            var results = await Task.WhenAll(
                Http.PostAsync<HeritageModelDto>($"{AdminRoot}/models/{a.Id}/show", null, admin),
                Http.PostAsync<HeritageModelDto>($"{AdminRoot}/models/{b.Id}/show", null, admin));

            Assert.Equal(1, results.Count(r => r.Response.StatusCode == HttpStatusCode.OK));
            Assert.All(results.Where(r => r.Response.StatusCode != HttpStatusCode.OK), r =>
                Assert.Equal((HttpStatusCode.UnprocessableEntity, MessageCodes.Blocked, ModelMessages.AnotherVisible.Text), (r.Response.StatusCode, r.Body.ErrorCode, r.Body.Message)));
            Assert.Equal(1, (await _k.ListAsync(admin, heritage.Id)).Count(m => m.Status == ContentStatus.PUBLISHED));
        }
    }

    [LocalDbFact]
    public async Task Two_uploads_declared_visible_at_the_same_time_leave_one_visible_and_no_stray_file()
    {
        var admin = await _k.AdminAsync();
        var heritage = await _k.PublishedHeritageAsync(admin);
        var filesBefore = Directory.Exists(Path.Combine(factory.StorageRoot, "models")) ? Directory.GetFiles(Path.Combine(factory.StorageRoot, "models")).Length : 0;
        var fields = new Dictionary<string, string>(Credit()) { ["show"] = "true" };

        var results = await Task.WhenAll(
            _k.UploadAsync<HeritageModelDto>(HttpMethod.Post, $"{AdminRoot}/heritages/{heritage.Id}/models/upload", admin, FileSamples.Glb(), fields),
            _k.UploadAsync<HeritageModelDto>(HttpMethod.Post, $"{AdminRoot}/heritages/{heritage.Id}/models/upload", admin, FileSamples.Glb(), fields));

        Assert.Equal(1, results.Count(r => r.Response.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, results.Count(r => r.Response.StatusCode == HttpStatusCode.UnprocessableEntity));
        Assert.Single(await _k.ListAsync(admin, heritage.Id));
        var filesAfter = Directory.GetFiles(Path.Combine(factory.StorageRoot, "models")).Length;
        Assert.Equal(filesBefore + 1, filesAfter);   // tệp của lần bị từ chối đã được gỡ lại
    }
}
