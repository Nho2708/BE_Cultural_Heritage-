using System.Net;
using DSVHVN.Application.Heritages;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Dựng dữ liệu cho test khám phá công khai qua chính API quản trị (tạo, ảnh đại diện, nguồn, giai đoạn, sự kiện, xuất bản),
/// nên cột tìm kiếm không dấu được tính như khi chạy thật.
/// </summary>
public sealed class DiscoveryTestKit(ApiFactory factory)
{
    public const string Admin = "/api/v1/admin";
    public const string Public = "/api/v1/public";

    public PlatformClient Api { get; } = new(factory);
    public HttpClient Http => Api.Http;
    public ApiFactory Factory { get; } = factory;

    public static string Unique() => PlatformClient.Unique();

    public static string Words(int count) => string.Join(' ', Enumerable.Repeat("từ", count));

    /// <summary>Mô tả một di sản thử; mặc định là di sản vật thể cấp quốc gia ở Hà Nội có tọa độ.</summary>
    public sealed record Spec(
        string Name,
        string Type = "TANGIBLE",
        string Level = "NATIONAL",
        string ProvinceCode = "01",
        decimal? Latitude = 21.033333m,
        decimal? Longitude = 105.85m,
        string? ShortDescription = null,
        string? Address = "1 Phố Cổ, Hà Nội",
        string? Extra = null,
        string? Slug = null);

    public async Task<int> ProvinceIdAsync(string code)
    {
        var (name, region) = code switch
        {
            "01" => ("Thành phố Hà Nội", "Bắc"),
            "46" => ("Thành phố Huế", "Trung"),
            "48" => ("Thành phố Đà Nẵng", "Trung"),
            "79" => ("Thành phố Hồ Chí Minh", "Nam"),
            _ => ("Tỉnh thử " + code, "Bắc"),
        };
        return await Factory.WithDbAsync(async db =>
        {
            var province = await db.Provinces.FirstOrDefaultAsync(p => p.Code == code);
            if (province is null)
            {
                province = new DSVHVN.Domain.Heritages.Province { Code = code, Name = name, Region = region };
                db.Provinces.Add(province);
                await db.SaveChangesAsync();
            }
            return province.Id;
        });
    }

    public static async Task<T> Expect<T>(Task<(HttpResponseMessage Response, Envelope<T> Body)> call,
        HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (res, body) = await call;
        Assert.True(res.StatusCode == expected, $"Mong {(int)expected}, nhận {(int)res.StatusCode} {body.ErrorCode}: {body.Message}");
        return body.Data!;
    }

    public static async Task<Envelope<object>> Fail(Task<(HttpResponseMessage Response, Envelope<object> Body)> call,
        HttpStatusCode expected, string errorCode)
    {
        var (res, body) = await call;
        Assert.Equal((expected, errorCode), (res.StatusCode, body.ErrorCode));
        Assert.False(body.Success);
        return body;
    }

    /// <summary>Tạo di sản nháp đủ thông tin, một ảnh đại diện và một nguồn (xuất bản được ngay), chưa xuất bản.</summary>
    public async Task<HeritageDetailDto> DraftAsync(string token, Spec spec)
    {
        var provinceId = await ProvinceIdAsync(spec.ProvinceCode);
        var body = new
        {
            name = spec.Name,
            slug = spec.Slug,
            shortDescription = spec.ShortDescription ?? "Mô tả ngắn của " + spec.Name,
            content = (spec.Extra is null ? "" : spec.Extra + " ") + Words(210),
            heritageType = spec.Type,
            recognitionLevel = spec.Level,
            recognizedYear = spec.Level == "NONE" ? (int?)null : 1999,
            provinceId,
            address = spec.Address,
            latitude = spec.Latitude,
            longitude = spec.Longitude,
        };
        var heritage = await Expect(Http.PostAsync<HeritageDetailDto>($"{Admin}/heritages", body, token), HttpStatusCode.Created);
        await Expect(Http.PostAsync<MediaDto>($"{Admin}/heritages/{heritage.Id}/media", new
        {
            mediaType = "IMAGE",
            url = $"https://upload.wikimedia.org/{Unique()}.jpg",
            caption = "Ảnh minh họa " + spec.Name,
            author = "Nguyễn Văn An",
            license = "CC BY-SA 4.0",
            source = "Wikimedia Commons",
            setAsThumbnail = true,
        }, token), HttpStatusCode.Created);
        await Expect(Http.PostAsync<ReferenceDto>($"{Admin}/heritages/{heritage.Id}/references", new
        {
            title = "Quyết định xếp hạng — Thủ tướng Chính phủ, truy cập 26/09/2026",
            url = "https://vanban.chinhphu.vn/qd-1272",
            sourceType = "GOV_DOC",
        }, token), HttpStatusCode.Created);
        return await Expect(Http.GetAsync<HeritageDetailDto>($"{Admin}/heritages/{heritage.Id}", token));
    }

    public async Task<HeritageDetailDto> PublishedAsync(string token, Spec spec)
    {
        var draft = await DraftAsync(token, spec);
        return await Expect(Http.PostAsync<HeritageDetailDto>($"{Admin}/heritages/{draft.Id}/publish", null, token));
    }

    public async Task<TimelineDto> AddPhaseAsync(string token, long heritageId, string name, int? start, int? end, int? order = null) =>
        await Expect(Http.PostAsync<TimelineDto>($"{Admin}/heritages/{heritageId}/timelines",
            new { name, description = "Mô tả " + name, startYear = start, endYear = end, sortOrder = order }, token), HttpStatusCode.Created);

    public async Task<EventDto> AddEventAsync(string token, long timelineId, string name, int year, int? month = null, int? day = null,
        int? endYear = null, int? endMonth = null, int? endDay = null) =>
        await Expect(Http.PostAsync<EventDto>($"{Admin}/timelines/{timelineId}/events", new
        {
            name,
            description = "Mô tả " + name,
            startYear = year,
            startMonth = month,
            startDay = day,
            endYear,
            endMonth,
            endDay,
        }, token), HttpStatusCode.Created);
}
