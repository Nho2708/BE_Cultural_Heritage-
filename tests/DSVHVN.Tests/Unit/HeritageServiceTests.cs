using DSVHVN.Application.Common;
using DSVHVN.Application.Heritages;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Infrastructure.Persistence;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DSVHVN.Tests.Unit;

public sealed class HeritageServiceTests : IDisposable
{
    private readonly ServiceHarness _h = new();

    public HeritageServiceTests()
    {
        // Seed dữ liệu mẫu cho bài test
        _h.RunAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var hanoi = new Province { Id = 1, Code = "HN", Name = "Hà Nội", Region = "Bắc" };
            var quangnam = new Province { Id = 2, Code = "QN", Name = "Quảng Nam", Region = "Trung" };
            db.Provinces.AddRange(hanoi, quangnam);

            var thăngLong = new Heritage
            {
                Id = 1,
                Name = "Khu Trung tâm Hoàng thành Thăng Long - Hà Nội",
                Slug = "hoang-thanh-thang-long",
                ShortDescription = "Di sản văn hóa thế giới tại Hà Nội",
                Content = "Nội dung chi tiết về Hoàng thành Thăng Long...",
                HeritageType = HeritageType.TANGIBLE,
                RecognitionLevel = RecognitionLevel.UNESCO,
                RecognizedYear = 2010,
                Address = "19C Hoàng Diệu, Ba Đình, Hà Nội",
                Latitude = 21.0364m,
                Longitude = 105.8402m,
                Status = ContentStatus.PUBLISHED,
                ProvinceId = 1,
                Timelines =
                [
                    new Timeline
                    {
                        Name = "Thời Lý - Trần",
                        StartYear = 1010,
                        EndYear = 1400,
                        SortOrder = 1,
                        Events =
                        [
                            new TimelineEvent
                            {
                                Name = "Dời đô về Thăng Long",
                                StartYear = 1010,
                                SortOrder = 1,
                            }
                        ]
                    }
                ],
                Media =
                [
                    new Media
                    {
                        MediaType = "IMAGE",
                        Url = "https://example.com/thanglong.jpg",
                        Caption = "Đoan Môn Hoàng thành",
                        SortOrder = 1
                    }
                ],
                References =
                [
                    new HeritageReference
                    {
                        Title = "Cục Di sản văn hóa",
                        Url = "http://dsvh.gov.vn",
                        SourceType = "GOV_DOC"
                    }
                ]
            };

            var hoiAnDraft = new Heritage
            {
                Id = 2,
                Name = "Đô thị cổ Hội An (Bản nháp)",
                Slug = "do-thi-co-hoi-an-nhap",
                ShortDescription = "Di sản đang soạn",
                Status = ContentStatus.DRAFT, // Chỉ bản nháp
                ProvinceId = 2
            };

            db.Heritages.AddRange(thăngLong, hoiAnDraft);
            await db.SaveChangesAsync();
        }).GetAwaiter().GetResult();
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task List_OnlyReturnsPublishedHeritages_AndFiltersCorrectly()
    {
        var result = await _h.Service<HeritageService, PagedResult<HeritageSummaryDto>>(
            s => s.ListAsync(new HeritageListQuery(), default));

        // Chỉ trả về Hoàng thành Thăng Long (PUBLISHED), không trả về Hội An (DRAFT)
        var item = Assert.Single(result.Items);
        Assert.Equal("hoang-thanh-thang-long", item.Slug);
        Assert.Equal(21.0364m, item.Latitude);
        Assert.Equal(105.8402m, item.Longitude);
        Assert.Equal("Hà Nội", item.ProvinceName);
        Assert.Equal("Bắc", item.Region);
    }

    [Fact]
    public async Task List_FiltersByRegion_ReturnsMatching()
    {
        var resultNorth = await _h.Service<HeritageService, PagedResult<HeritageSummaryDto>>(
            s => s.ListAsync(new HeritageListQuery(Region: "Bắc"), default));
        Assert.Single(resultNorth.Items);

        var resultSouth = await _h.Service<HeritageService, PagedResult<HeritageSummaryDto>>(
            s => s.ListAsync(new HeritageListQuery(Region: "Nam"), default));
        Assert.Empty(resultSouth.Items);
    }

    [Fact]
    public async Task GetBySlug_ReturnsFullDetailsWithTimelinesAndMedia()
    {
        var detail = await _h.Service<HeritageService, HeritageDetailDto>(
            s => s.GetBySlugAsync("hoang-thanh-thang-long", default));

        Assert.Equal("Khu Trung tâm Hoàng thành Thăng Long - Hà Nội", detail.Name);
        Assert.Equal(2010, detail.RecognizedYear);
        Assert.Equal("Hà Nội", detail.ProvinceName);

        var timeline = Assert.Single(detail.Timelines);
        Assert.Equal("Thời Lý - Trần", timeline.Name);
        var evt = Assert.Single(timeline.Events);
        Assert.Equal("Dời đô về Thăng Long", evt.Name);

        var media = Assert.Single(detail.Media);
        Assert.Equal("Đoan Môn Hoàng thành", media.Caption);

        var reference = Assert.Single(detail.References);
        Assert.Equal("Cục Di sản văn hóa", reference.Title);
    }

    [Fact]
    public async Task GetBySlug_NonExistent_ThrowsNotFoundAppException()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            _h.Service<HeritageService, HeritageDetailDto>(
                s => s.GetBySlugAsync("khong-ton-tai-slug", default)));

        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task ListProvinces_ReturnsCorrectHeritageCounts()
    {
        var provinces = await _h.Service<HeritageService, List<ProvinceSummaryDto>>(
            s => s.ListProvincesAsync(default));

        Assert.Equal(2, provinces.Count);

        var hanoi = provinces.First(p => p.Code == "HN");
        Assert.Equal(1, hanoi.HeritageCount); // Có 1 di sản PUBLISHED

        var quangnam = provinces.First(p => p.Code == "QN");
        Assert.Equal(0, quangnam.HeritageCount); // Hội An là DRAFT nên count = 0
    }
}
