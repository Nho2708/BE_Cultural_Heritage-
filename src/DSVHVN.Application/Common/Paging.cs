using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Common;

/// <summary>Phân trang: <c>page</c> từ 1, <c>pageSize</c> mặc định 20, tối đa 100; trả <c>totalItems</c>, <c>totalPages</c>.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalItems, int TotalPages);

public static class Paging
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public static (int Page, int PageSize) Normalize(int? page, int? pageSize)
    {
        var p = page ?? 1;
        var size = pageSize ?? DefaultPageSize;
        if (p < 1) throw AppException.Validation("page", Messages.Invalid("Trang", "phải từ 1 trở lên"));
        if (size is < 1 or > MaxPageSize)
            throw AppException.Validation("pageSize", Messages.Invalid("Số dòng mỗi trang", $"phải từ 1 đến {MaxPageSize}"));
        return (p, size);
    }

    /// <summary>Đếm tổng rồi lấy một trang; <paramref name="query"/> phải đã sắp xếp.</summary>
    public static async Task<PagedResult<T>> ToPagedAsync<T>(this IQueryable<T> query, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Normalize(page, pageSize);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<T>(items, p, size, total, (int)Math.Ceiling(total / (double)size));
    }
}
