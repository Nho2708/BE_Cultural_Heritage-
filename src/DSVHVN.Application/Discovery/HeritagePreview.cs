using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Application.Models;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Discovery;

/// <summary>Mã xem trước đã cấp hoặc đã đọc: mã, di sản mà mã gắn và thời điểm hết hạn (UTC).</summary>
public sealed record PreviewTicket(string Code, long HeritageId, DateTime ExpiresAt);

/// <summary>
/// Cấp và kiểm mã xem trước bản nháp di sản. Mã là chuỗi ngẫu nhiên không đoán được, <b>ký</b> bằng khóa bí mật của máy chủ và mang theo di sản cùng hạn
/// dùng, nên không cần bảng hay bộ nhớ tạm: sửa một ký tự, đổi di sản hay đổi hạn đều làm chữ ký sai. Mã hết hạn thì không dùng lại được.
/// </summary>
public interface IHeritagePreviewCodec
{
    /// <summary>Cấp mã mới cho một di sản, hạn dùng tính từ lúc cấp (mặc định 30 phút).</summary>
    PreviewTicket Issue(long heritageId);

    /// <summary>Đọc mã; null nếu mã không đọc được, chữ ký sai hoặc đã hết hạn.</summary>
    PreviewTicket? Read(string? code);
}

/// <summary>Liên kết xem trước trả cho màn hình Biên tập di sản: mã, đường dẫn trang xem trước trên web học tập và hạn dùng.</summary>
public sealed record HeritagePreviewLinkDto(string Code, string Path, DateTime ExpiresAt, int ExpiresInMinutes);

/// <summary>Bản nháp của di sản để hiện trên trang xem trước: cùng dữ liệu với trang chi tiết công khai, kèm hạn của liên kết cho dải "Bản xem trước".</summary>
public sealed record HeritagePreviewDto(DateTime ExpiresAt, string Status, PublicHeritageDetailDto Heritage);

/// <summary>
/// Xem trước bản nháp di sản (quy tắc Xem trước bản nháp di sản). Chỉ quản trị hệ thống cấp được mã (cho di sản chưa xuất bản) và chỉ quản trị hệ thống
/// đang đăng nhập đọc được bản nháp bằng mã còn hạn. Mã sai, hết hạn, di sản không còn hoặc người mở không phải quản trị hệ thống đều cùng một
/// kết quả 404 (không cho biết di sản có tồn tại hay không). Không đổi trạng thái di sản, không ghi lượt xem; việc cấp mã được ghi nhật ký thao tác.
/// </summary>
public sealed class HeritagePreviewService(
    IAppDbContext db, IHeritagePreviewCodec codec, DiscoveryService discovery, PublicModelService models, IAuditLogger audit, TimeProvider clock)
{
    public const string PathPrefix = "/xem-truoc/di-san/";

    public async Task<HeritagePreviewLinkDto> IssueAsync(Actor actor, long heritageId, CancellationToken ct)
    {
        if (actor.Role != RoleCode.ADMIN) throw AppException.Forbidden(Messages.Forbidden);

        var heritage = await db.Heritages.AsNoTracking()
                           .Where(h => h.Id == heritageId && h.DeletedAt == null)
                           .Select(h => new { h.Id, h.Status })
                           .SingleOrDefaultAsync(ct)
                       ?? throw AppException.NotFound(Messages.NotFound("xem trước di sản", "di sản"));
        if (heritage.Status == ContentStatus.PUBLISHED)
            throw AppException.BusinessRule(Messages.Blocked("xem trước di sản",
                "Di sản đã xuất bản, hãy mở trang công khai của di sản."));

        var ticket = codec.Issue(heritage.Id);
        audit.Record(AuditAction.HERITAGE_PREVIEW_ISSUED, AuditTargets.Heritages, heritage.Id, actor,
            newValue: new { expiresAt = ticket.ExpiresAt });
        await db.SaveChangesAsync(ct);

        var minutes = Math.Max(1, (int)Math.Round((ticket.ExpiresAt - clock.GetUtcNow().UtcDateTime).TotalMinutes));
        return new HeritagePreviewLinkDto(ticket.Code, PathPrefix + ticket.Code, ticket.ExpiresAt, minutes);
    }

    /// <summary><paramref name="actor"/> là null khi người mở chưa đăng nhập; mọi trường hợp không đủ điều kiện đều ném cùng một 404.</summary>
    public async Task<HeritagePreviewDto> GetAsync(Actor? actor, string? code, LanguageCode language, CancellationToken ct)
    {
        var (ticket, heritage) = await OpenAsync(actor, code, ct);
        return new HeritagePreviewDto(ticket.ExpiresAt, heritage.Status?.ToString() ?? string.Empty, await discovery.BuildDetailAsync(heritage, language, ct));
    }

    /// <summary>Timeline đầy đủ của bản nháp, cùng điều kiện với <see cref="GetAsync"/>; trang chi tiết xem trước dùng khi mở dòng thời gian.</summary>
    public async Task<PublicTimelineDto> GetTimelineAsync(Actor? actor, string? code, LanguageCode language, CancellationToken ct)
    {
        var (_, heritage) = await OpenAsync(actor, code, ct);
        return await discovery.BuildTimelineAsync(heritage, language, ct);
    }

    /// <summary>Mô hình 3D đang hiển thị của bản nháp, cùng điều kiện mở với <see cref="GetAsync"/>; không có mô hình thì 404 kèm mã MODEL_UNAVAILABLE.</summary>
    public async Task<PublicModelDto> GetModelAsync(Actor? actor, string? code, CancellationToken ct)
    {
        var (_, heritage) = await OpenAsync(actor, code, ct);
        return await models.VisibleAsync(heritage.Id, ct);
    }

    private async Task<(PreviewTicket Ticket, Heritage Heritage)> OpenAsync(Actor? actor, string? code, CancellationToken ct)
    {
        var ticket = actor is { Role: RoleCode.ADMIN } ? codec.Read(code) : null;
        var heritage = ticket is null
            ? null
            : await db.Heritages.AsNoTracking().Include(h => h.Province)
                .SingleOrDefaultAsync(h => h.Id == ticket.HeritageId && h.DeletedAt == null, ct);
        if (ticket is null || heritage is null) throw AppException.NotFound(Messages.PreviewInvalid);
        return (ticket, heritage);
    }
}
