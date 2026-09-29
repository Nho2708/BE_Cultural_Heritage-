using DSVHVN.Application.Common;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Heritages;

/// <summary>
/// Khai báo nguồn tham chiếu của di sản (văn bản nhà nước, website, sách). Di sản chỉ xuất bản được khi có ít nhất một nguồn,
/// nên không xóa được nguồn cuối cùng của di sản đang xuất bản. Tiêu đề nên theo mẫu "Tên tài liệu — Cơ quan hoặc tác giả,
/// truy cập dd/mm/yyyy"; bài lấy từ Cục Di sản văn hóa ghi rõ "Cục Di sản văn hóa". Hai điều này là quy trình của nhóm, hệ thống chỉ nhắc.
/// </summary>
public sealed class HeritageReferenceService(IAppDbContext db, RequestValidator validator, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<ReferenceDto> AddAsync(long heritageId, ReferenceRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var heritage = await db.FindHeritageAsync(heritageId, "thêm nguồn tham chiếu", ct);
        var reference = new HeritageReference { HeritageId = heritageId };
        Apply(reference, request);
        db.HeritageReferences.Add(reference);
        heritage.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return HeritageMapper.ToDto(reference);
    }

    public async Task<ReferenceDto> UpdateAsync(long referenceId, ReferenceRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var reference = await db.FindReferenceAsync(referenceId, "sửa nguồn tham chiếu", ct);
        Apply(reference, request);
        reference.Heritage!.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return HeritageMapper.ToDto(reference);
    }

    public async Task DeleteAsync(long referenceId, CancellationToken ct)
    {
        const string action = "xóa nguồn tham chiếu";
        var reference = await db.FindReferenceAsync(referenceId, action, ct);
        var heritage = reference.Heritage!;
        if (heritage.Status == ContentStatus.PUBLISHED
            && await db.HeritageReferences.CountAsync(r => r.HeritageId == heritage.Id, ct) <= 1)
            throw AppException.BusinessRule(Messages.Blocked(action,
                "Đây là nguồn cuối cùng của di sản đang xuất bản. Hãy thêm nguồn khác trước hoặc ẩn di sản."));
        db.HeritageReferences.Remove(reference);
        heritage.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
    }

    private static void Apply(HeritageReference reference, ReferenceRequest r)
    {
        reference.Title = r.Title!.Trim();
        reference.Url = ValidationRules.TrimToNull(r.Url);
        reference.SourceType = r.SourceType!.Trim();
    }
}
