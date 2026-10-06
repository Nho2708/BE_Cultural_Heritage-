using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.Discovery;
using DSVHVN.Application.Heritages;
using DSVHVN.Domain.Enums;
using DSVHVN.Infrastructure.Security;
using DSVHVN.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Integration;

/// <summary>Api với đồng hồ tua được cho mã xem trước (chỉ mã xem trước dùng đồng hồ này, token đăng nhập vẫn dùng giờ thật).</summary>
public sealed class PreviewApiFactory : ApiFactory
{
    public MutableTimeProvider PreviewClock { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHeritagePreviewCodec>();
            services.AddSingleton<IHeritagePreviewCodec>(sp => new HeritagePreviewCodec(
                sp.GetRequiredService<JwtSigningKeyProvider>(), Options.Create(new PreviewOptions()), PreviewClock));
        });
    }
}

/// <summary>
/// Xem trước bản nháp di sản: quản trị hệ thống cấp liên kết 30 phút cho di sản chưa xuất bản và đọc bản nháp bằng đúng dữ liệu của trang chi tiết công khai;
/// mã sai, hết hạn, thiếu đăng nhập quản trị hệ thống đều cùng một 404; mọi phản hồi không lập chỉ mục và không lưu đệm; bản nháp không lọt ra API công khai.
/// </summary>
public sealed class HeritagePreviewApiTests(PreviewApiFactory factory) : IClassFixture<PreviewApiFactory>
{
    private const string Invalid = "PREVIEW_INVALID";
    private readonly DiscoveryTestKit _k = new(factory);
    private HttpClient Http => _k.Http;

    private static string Issue(long id) => $"{DiscoveryTestKit.Admin}/heritages/{id}/preview";
    private static string Read(string code) => $"/api/v1/preview/heritages/{code}";

    private async Task<(string Admin, HeritageDetailDto Draft)> DraftAsync(string label = "Bản nháp")
    {
        var admin = await _k.Api.AdminTokenAsync();
        var draft = await _k.DraftAsync(admin, new DiscoveryTestKit.Spec(label + " " + DiscoveryTestKit.Unique()));
        return (admin, draft);
    }

    private async Task<string> CodeAsync(string admin, long id) =>
        (await DiscoveryTestKit.Expect(Http.PostAsync<HeritagePreviewLinkDto>(Issue(id), null, admin))).Code;

    private static void AssertNeverIndexedOrCached(HttpResponseMessage response)
    {
        Assert.Contains("noindex", string.Join(",", response.Headers.GetValues("X-Robots-Tag")));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Admin_gets_a_thirty_minute_link_for_a_draft_and_reads_the_draft_with_the_same_data_as_the_public_page()
    {
        var (admin, draft) = await DraftAsync();

        var (issued, issuedBody) = await Http.PostAsync<HeritagePreviewLinkDto>(Issue(draft.Id), null, admin);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        Assert.Contains("no-store", issued.Headers.CacheControl?.ToString());
        var link = issuedBody.Data!;
        Assert.Equal("/xem-truoc/di-san/" + link.Code, link.Path);
        Assert.Equal(30, link.ExpiresInMinutes);
        Assert.InRange(link.ExpiresAt, DateTime.UtcNow.AddMinutes(29), DateTime.UtcNow.AddMinutes(31));

        var (res, body) = await Http.GetAsync<HeritagePreviewDto>(Read(link.Code), admin);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        AssertNeverIndexedOrCached(res);
        var preview = body.Data!;
        Assert.Equal(link.ExpiresAt, preview.ExpiresAt, TimeSpan.FromSeconds(1));
        Assert.Equal("DRAFT", preview.Status);
        var page = preview.Heritage;
        Assert.Equal((draft.Id, draft.Slug, draft.Name, draft.ShortDescription, draft.Content),
            (page.Id, page.Slug, page.Name, page.ShortDescription, page.Content));
        Assert.Equal(draft.ThumbnailUrl, page.ThumbnailUrl);
        Assert.Single(page.Media);
        Assert.Single(page.References);
        Assert.NotNull(page.Province);
    }

    [Fact]
    public async Task The_preview_shows_the_saved_draft_and_follows_later_edits_and_never_changes_the_status()
    {
        var (admin, draft) = await DraftAsync();
        var code = await CodeAsync(admin, draft.Id);
        var before = await factory.WithDbAsync(db => db.Heritages.AsNoTracking().Where(h => h.Id == draft.Id)
            .Select(h => new { h.Status, h.UpdatedAt }).SingleAsync());

        _ = await DiscoveryTestKit.Expect(Http.GetAsync<HeritagePreviewDto>(Read(code), admin));
        var after = await factory.WithDbAsync(db => db.Heritages.AsNoTracking().Where(h => h.Id == draft.Id)
            .Select(h => new { h.Status, h.UpdatedAt }).SingleAsync());
        Assert.Equal(before, after);
        Assert.Equal(ContentStatus.DRAFT, after.Status);

        await DiscoveryTestKit.Expect(Http.PutAsync<HeritageDetailDto>($"{DiscoveryTestKit.Admin}/heritages/{draft.Id}", new
        {
            name = draft.Name + " đã sửa",
            shortDescription = "Mô tả mới",
            content = draft.Content,
            heritageType = draft.HeritageType?.ToString(),
            recognitionLevel = draft.RecognitionLevel?.ToString(),
            recognizedYear = draft.RecognizedYear,
            provinceId = draft.Province?.Id,
            address = draft.Address,
            latitude = draft.Latitude,
            longitude = draft.Longitude,
        }, admin));

        var edited = (await DiscoveryTestKit.Expect(Http.GetAsync<HeritagePreviewDto>(Read(code), admin))).Heritage;
        Assert.Equal((draft.Name + " đã sửa", "Mô tả mới"), (edited.Name, edited.ShortDescription));
    }

    [Fact]
    public async Task The_preview_timeline_of_a_draft_is_readable_with_the_same_rules()
    {
        var (admin, draft) = await DraftAsync();
        var phase = await _k.AddPhaseAsync(admin, draft.Id, "Thời Lý", 1010, 1225);
        await _k.AddEventAsync(admin, phase.Id, "Dời đô", 1010);
        var code = await CodeAsync(admin, draft.Id);

        var (res, body) = await Http.GetAsync<PublicTimelineDto>(Read(code) + "/timeline", admin);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        AssertNeverIndexedOrCached(res);
        Assert.Equal(draft.Slug, body.Data!.Slug);
        Assert.Equal("Dời đô", Assert.Single(Assert.Single(body.Data.Phases).Events).Name);

        var (anon, anonBody) = await Http.GetAsync<PublicTimelineDto>(Read(code) + "/timeline");
        Assert.Equal((HttpStatusCode.NotFound, Invalid), (anon.StatusCode, anonBody.ErrorCode));
    }

    [Fact]
    public async Task A_draft_is_not_visible_through_the_public_api_even_while_a_preview_link_exists()
    {
        var (admin, draft) = await DraftAsync("Chỉ xem trước");
        var code = await CodeAsync(admin, draft.Id);

        var (detail, _) = await Http.GetAsync<PublicHeritageDetailDto>($"{DiscoveryTestKit.Public}/heritages/{draft.Slug}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        var search = await DiscoveryTestKit.Expect(Http.GetAsync<PagedResult<HeritageCardDto>>(
            $"{DiscoveryTestKit.Public}/heritages?keyword={Uri.EscapeDataString(draft.Name!)}"));
        Assert.Empty(search.Items);

        // Mã xem trước không dùng được như đường dẫn của trang công khai.
        var (res, _) = await Http.GetAsync<object>($"{DiscoveryTestKit.Public}/heritages/{code}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Everyone_but_a_signed_in_system_admin_gets_the_same_404_for_a_valid_code_a_wrong_code_and_no_code()
    {
        var (admin, draft) = await DraftAsync();
        var code = await CodeAsync(admin, draft.Id);
        var orgAdminToken = (await _k.Api.OrgWithAdminAsync()).OrgAdminToken;
        var (_, teacherToken) = await _k.Api.TeacherAsync(orgAdminToken);

        var attempts = new List<(string Name, string Url, string? Token)>
        {
            ("anonymous with a valid code", Read(code), null),
            ("teacher with a valid code", Read(code), teacherToken),
            ("school admin with a valid code", Read(code), orgAdminToken),
            ("admin with a wrong code", Read(code[..^2] + (code[^2] == 'A' ? "B" : "A") + code[^1]), admin),
            ("admin with nonsense", Read("khong-phai-ma"), admin),
            ("admin with a code of nobody", Read(new string('A', 22) + "." + new string('B', 43)), admin),
        };

        var results = new List<(HttpStatusCode, string?, string?)>();
        foreach (var (name, url, token) in attempts)
        {
            var (res, body) = await Http.GetAsync<HeritagePreviewDto>(url, token);
            Assert.True(res.StatusCode == HttpStatusCode.NotFound, name);
            Assert.Null(body.Data);
            AssertNeverIndexedOrCached(res);
            results.Add((res.StatusCode, body.ErrorCode, body.Message));
        }
        Assert.Single(results.Distinct());
        Assert.Equal(Invalid, results[0].Item2);
        Assert.Contains("hết hạn hoặc không hợp lệ", results[0].Item3);
    }

    [Fact]
    public async Task A_code_dies_thirty_minutes_after_it_was_issued_and_a_new_click_gives_a_new_code()
    {
        var (admin, draft) = await DraftAsync();
        factory.PreviewClock.Set(DateTimeOffset.UtcNow);
        var code = await CodeAsync(admin, draft.Id);

        factory.PreviewClock.Set(DateTimeOffset.UtcNow.AddMinutes(29));
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync<HeritagePreviewDto>(Read(code), admin)).Response.StatusCode);

        factory.PreviewClock.Set(DateTimeOffset.UtcNow.AddMinutes(31));
        var (res, body) = await Http.GetAsync<HeritagePreviewDto>(Read(code), admin);
        Assert.Equal((HttpStatusCode.NotFound, Invalid), (res.StatusCode, body.ErrorCode));
        AssertNeverIndexedOrCached(res);

        var fresh = await CodeAsync(admin, draft.Id);
        Assert.NotEqual(code, fresh);
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync<HeritagePreviewDto>(Read(fresh), admin)).Response.StatusCode);
        factory.PreviewClock.Set(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_code_opens_only_its_own_heritage()
    {
        var (admin, first) = await DraftAsync("Một");
        var second = await _k.DraftAsync(admin, new DiscoveryTestKit.Spec("Hai " + DiscoveryTestKit.Unique()));
        var code = await CodeAsync(admin, first.Id);

        var preview = await DiscoveryTestKit.Expect(Http.GetAsync<HeritagePreviewDto>(Read(code), admin));
        Assert.Equal(first.Id, preview.Heritage.Id);
        Assert.NotEqual(second.Id, preview.Heritage.Id);
    }

    [Fact]
    public async Task A_deleted_heritage_no_longer_opens()
    {
        var (admin, draft) = await DraftAsync();
        var code = await CodeAsync(admin, draft.Id);
        await DiscoveryTestKit.Expect(Http.DeleteAsync<object>($"{DiscoveryTestKit.Admin}/heritages/{draft.Id}", admin));

        var (res, body) = await Http.GetAsync<HeritagePreviewDto>(Read(code), admin);
        Assert.Equal((HttpStatusCode.NotFound, Invalid), (res.StatusCode, body.ErrorCode));
    }

    [Fact]
    public async Task Only_a_system_admin_can_issue_a_link_and_only_for_an_unpublished_heritage_that_exists()
    {
        var (admin, draft) = await DraftAsync();
        var orgAdmin = (await _k.Api.OrgWithAdminAsync()).OrgAdminToken;
        var (_, teacher) = await _k.Api.TeacherAsync(orgAdmin);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.PostAsync<object>(Issue(draft.Id), null)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.PostAsync<object>(Issue(draft.Id), null, teacher)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.PostAsync<object>(Issue(draft.Id), null, orgAdmin)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsync<object>(Issue(987654321), null, admin)).Response.StatusCode);

        await DiscoveryTestKit.Expect(Http.PostAsync<HeritageDetailDto>($"{DiscoveryTestKit.Admin}/heritages/{draft.Id}/publish", null, admin));
        var (res, body) = await Http.PostAsync<object>(Issue(draft.Id), null, admin);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "BLOCKED"), (res.StatusCode, body.ErrorCode));
        Assert.Contains("trang công khai", body.Message);
    }

    [Fact]
    public async Task An_archived_heritage_can_be_previewed_before_it_is_restored()
    {
        var (admin, draft) = await DraftAsync();
        await DiscoveryTestKit.Expect(Http.PostAsync<HeritageDetailDto>($"{DiscoveryTestKit.Admin}/heritages/{draft.Id}/publish", null, admin));
        await DiscoveryTestKit.Expect(Http.PostAsync<HeritageDetailDto>($"{DiscoveryTestKit.Admin}/heritages/{draft.Id}/archive", null, admin));

        var code = await CodeAsync(admin, draft.Id);
        var preview = await DiscoveryTestKit.Expect(Http.GetAsync<HeritagePreviewDto>(Read(code), admin));
        Assert.Equal("ARCHIVED", preview.Status);
    }

    [Fact]
    public async Task Issuing_a_link_is_written_to_the_audit_log_without_the_code()
    {
        var (admin, draft) = await DraftAsync();
        var code = await CodeAsync(admin, draft.Id);

        var row = await factory.WithDbAsync(db => db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditAction.HERITAGE_PREVIEW_ISSUED && a.TargetId == draft.Id).SingleAsync());
        Assert.Equal((ActorRole.ADMIN, "heritages"), (row.ActorRole, row.TargetType));
        Assert.NotNull(row.UserId);
        Assert.DoesNotContain(code, row.NewValue ?? string.Empty);
        Assert.DoesNotContain(code, row.OldValue ?? string.Empty);
    }
}
