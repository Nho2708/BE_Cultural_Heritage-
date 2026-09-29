using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.Heritages;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Kho di sản trên SQL Server thật (database tạm của LocalDB): dữ liệu đợt 1 nạp vào DRAFT và đủ điều kiện xuất bản,
/// tìm không dấu theo collation Vietnamese_CI_AI, tranh chấp đường dẫn báo trùng thay vì lỗi hệ thống.
/// </summary>
public sealed class HeritageSqlServerTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>
{
    private const string Api = "/api/v1/admin";

    private readonly PlatformClient _api = new(factory);
    private HttpClient Http => _api.Http;

    private static async Task<T> Expect<T>(Task<(HttpResponseMessage Response, Envelope<T> Body)> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (res, body) = await call;
        Assert.True(res.StatusCode == expected, $"Mong {(int)expected}, nhận {(int)res.StatusCode} {body.ErrorCode}: {body.Message}");
        return body.Data!;
    }

    [LocalDbHeritageDataFact]
    public async Task Seeded_heritages_start_as_drafts_with_normalized_sources_and_can_all_be_published()
    {
        var admin = await _api.AdminTokenAsync();

        var provinces = await Expect(Http.GetAsync<List<ProvinceDto>>($"{Api}/provinces", admin));
        Assert.Equal(34, provinces.Count);
        Assert.Equal(("01", "Bắc"), (provinces[0].Code, provinces[0].Region));

        // Các test khác trong lớp có thể thêm di sản thử (chưa có nguồn); di sản đợt 1 nhận ra qua số nguồn.
        var all = await Expect(Http.GetAsync<PagedResult<HeritageSummaryDto>>($"{Api}/heritages?pageSize=100", admin));
        var seeded = all.Items.Where(i => i.ReferenceCount > 0).ToList();
        Assert.Equal(20, seeded.Count);
        Assert.All(seeded, i => Assert.Equal(ContentStatus.DRAFT, i.Status));

        var sourceTypes = await factory.WithDbAsync(db => db.HeritageReferences.Select(r => r.SourceType).Distinct().OrderBy(t => t).ToListAsync());
        Assert.Equal(["GOV_DOC", "WEBSITE"], sourceTypes);

        foreach (var row in seeded)
        {
            var detail = await Expect(Http.GetAsync<HeritageDetailDto>($"{Api}/heritages/{row.Id}", admin));
            Assert.True(detail.Readiness.IsReady, $"{detail.Slug} thiếu: {string.Join(", ", detail.Readiness.Missing.Select(m => m.Label))}");
            var published = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages/{row.Id}/publish", null, admin));
            Assert.Equal(ContentStatus.PUBLISHED, published.Status);
        }

        var log = await factory.WithDbAsync(db => db.AuditLogs
            .Where(l => l.TargetType == AuditTargets.Heritages && l.Action == AuditAction.HERITAGE_PUBLISHED).CountAsync());
        Assert.Equal(20, log);
        Assert.Equal(20, (await Expect(Http.GetAsync<PagedResult<HeritageSummaryDto>>($"{Api}/heritages?status=PUBLISHED&pageSize=100", admin))).TotalItems);
    }

    [LocalDbHeritageDataFact]
    public async Task Search_finds_names_with_and_without_accents()
    {
        var admin = await _api.AdminTokenAsync();

        Task<List<string?>> Names(string keyword) => Expect(Http.GetAsync<PagedResult<HeritageSummaryDto>>(
            $"{Api}/heritages?keyword={Uri.EscapeDataString(keyword)}&pageSize=100", admin))
            .ContinueWith(t => t.Result.Items.Select(i => i.Name).ToList());

        Assert.Contains("Khu Trung tâm Hoàng thành Thăng Long – Hà Nội", await Names("Hoàng thành"));
        Assert.Contains("Khu Trung tâm Hoàng thành Thăng Long – Hà Nội", await Names("hoang thanh"));        // collation bỏ dấu thanh
        Assert.Contains("Khu Trung tâm Hoàng thành Thăng Long – Hà Nội", await Names("thang long"));         // ă, â... nhờ đường dẫn không dấu
        Assert.Contains("Khu Trung tâm Hoàng thành Thăng Long – Hà Nội", await Names("THĂNG LONG"));
        Assert.Empty(await Names("khong co di san nay"));
    }

    [LocalDbHeritageDataFact]
    public async Task Two_requests_racing_for_one_slug_get_created_and_duplicate_not_a_system_error()
    {
        var admin = await _api.AdminTokenAsync();
        var slug = "tranh-chap-" + PlatformClient.Unique();

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(i =>
            Http.PostAsync<object>($"{Api}/heritages", new { name = $"Tranh chấp {i}", slug }, admin)));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], results.Select(r => r.Response.StatusCode).Order());
        Assert.Equal("DUPLICATE", results.Single(r => r.Response.StatusCode == HttpStatusCode.Conflict).Body.ErrorCode);
        Assert.Equal(1, await factory.WithDbAsync(db => db.Heritages.CountAsync(h => h.Slug == slug)));
    }

    [LocalDbHeritageDataFact]
    public async Task Timeline_of_a_seeded_heritage_is_ordered_and_years_before_common_era_are_kept()
    {
        var admin = await _api.AdminTokenAsync();
        var list = await Expect(Http.GetAsync<PagedResult<HeritageSummaryDto>>($"{Api}/heritages?pageSize=100", admin));

        foreach (var row in list.Items)
        {
            var detail = await Expect(Http.GetAsync<HeritageDetailDto>($"{Api}/heritages/{row.Id}", admin));
            foreach (var timeline in detail.Timelines)
            {
                var keys = timeline.Events.Select(e => (e.StartYear, (int)(e.StartMonth ?? 0), (int)(e.StartDay ?? 0))).ToList();
                Assert.Equal(keys.Order(), keys);
            }
            var orders = detail.Timelines.Select(t => t.SortOrder ?? int.MaxValue).ToList();
            Assert.Equal(orders.Order(), orders);
        }

        // Thêm một giai đoạn có năm âm vào bản sao rồi đọc lại đúng số âm.
        var created = await Expect(Http.PostAsync<HeritageDetailDto>($"{Api}/heritages", new { name = "Thử năm âm " + PlatformClient.Unique() }, admin),
            HttpStatusCode.Created);
        var dongSon = await Expect(Http.PostAsync<TimelineDto>($"{Api}/heritages/{created.Id}/timelines",
            new { name = "Văn hóa Đông Sơn", startYear = -700, endYear = 100 }, admin), HttpStatusCode.Created);
        var stored = await factory.WithDbAsync(db => db.Timelines.Where(t => t.Id == dongSon.Id).Select(t => new { t.StartYear, t.EndYear }).SingleAsync());
        Assert.Equal((-700, 100), (stored.StartYear, stored.EndYear));
    }

    [LocalDbFact]
    public async Task Only_the_system_admin_reaches_the_heritage_api_on_sql_server()
    {
        var (_, orgAdmin) = await _api.OrgWithAdminAsync();
        var (res, body) = await Http.GetAsync<object>($"{Api}/heritages", orgAdmin);
        Assert.Equal((HttpStatusCode.Forbidden, "FORBIDDEN"), (res.StatusCode, body.ErrorCode));
    }
}
