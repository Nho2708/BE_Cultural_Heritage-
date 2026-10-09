using DSVHVN.Domain.Enums;

namespace DSVHVN.Application.Heritages;

public sealed record HeritageSummaryDto(
    long Id,
    string Name,
    string Slug,
    string? ShortDescription,
    HeritageType? HeritageType,
    RecognitionLevel? RecognitionLevel,
    int? RecognizedYear,
    string? Address,
    decimal? Latitude,
    decimal? Longitude,
    string? ThumbnailUrl,
    int? ProvinceId,
    string? ProvinceName,
    string? Region);

public sealed record HeritageDetailDto(
    long Id,
    string Name,
    string Slug,
    string? ShortDescription,
    string? Content,
    HeritageType? HeritageType,
    RecognitionLevel? RecognitionLevel,
    int? RecognizedYear,
    string? Address,
    decimal? Latitude,
    decimal? Longitude,
    string? PlaceId,
    string? ThumbnailUrl,
    int? ProvinceId,
    string? ProvinceName,
    string? Region,
    List<TimelineDto> Timelines,
    List<MediaDto> Media,
    List<HeritageReferenceDto> References);

public sealed record TimelineDto(
    long Id,
    string? Name,
    string? Description,
    int? StartYear,
    int? EndYear,
    int? SortOrder,
    List<TimelineEventDto> Events);

public sealed record TimelineEventDto(
    long Id,
    string? Name,
    string? Description,
    int StartYear,
    short? StartMonth,
    short? StartDay,
    int? EndYear,
    short? EndMonth,
    short? EndDay,
    int? SortOrder,
    List<MediaDto> Media);

public sealed record MediaDto(
    long Id,
    string? MediaType,
    string? Url,
    string? Caption,
    string? Credit,
    int? SortOrder);

public sealed record HeritageReferenceDto(
    long Id,
    string? Title,
    string? Url,
    string? SourceType);

public sealed record ProvinceSummaryDto(
    int Id,
    string? Code,
    string? Name,
    string? Region,
    int HeritageCount);

public sealed record HeritageListQuery(
    string? Keyword = null,
    int? ProvinceId = null,
    string? Region = null,
    HeritageType? HeritageType = null,
    RecognitionLevel? RecognitionLevel = null,
    int? Page = null,
    int? PageSize = null);
