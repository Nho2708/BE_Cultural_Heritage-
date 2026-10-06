using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Application.Files;
using DSVHVN.Application.Heritages;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Models;

/// <summary>
/// Khai báo mô hình 3D của di sản (quản trị hệ thống; tab Mô hình 3D của màn hình Biên tập di sản).
/// <list type="bullet">
/// <item>Tệp: tải <c>.glb</c> lên ngay trong lần khai báo (<see cref="ModelFileService"/> kiểm bằng nội dung: glTF 2.0 một tệp, tối đa 15 MB, không tham chiếu tệp ngoài)
/// hoặc dán đường dẫn của tệp đã có trong kho tệp (dung lượng do máy chủ đo; trang ngoài kho không được nhận). Thay tệp thì tệp cũ vẫn được giữ lại trong kho.</item>
/// <item>Ghi công bắt buộc: tác giả, giấy phép mở (CC0, CC BY, CC BY-SA, phạm vi công cộng, mô hình nhóm tự dựng) và đường dẫn nguồn; dòng ghi công "tác giả · giấy phép · nguồn" do hệ thống ghép.</item>
/// <item>Hiển thị: DRAFT (nháp) sang PUBLISHED (hiện), PUBLISHED sang ARCHIVED (ẩn) và ngược lại. Mỗi di sản chỉ một mô hình đang hiển thị (chỉ mục duy nhất có lọc bảo vệ cả khi hai người cùng bấm).
/// Mô hình chỉ tới khách khi di sản cũng đã xuất bản; di sản bị ẩn thì mô hình ẩn theo mà trạng thái của mô hình không đổi.</item>
/// <item>Gỡ: xóa mềm, giải phóng chỗ "đang hiển thị".</item>
/// <item>Thêm, thay tệp, sửa thông tin, hiện, ẩn, gỡ đều ghi nhật ký thao tác <c>HERITAGE_MODEL_CHANGED</c> kèm việc đã làm, giấy phép, dung lượng (không ghi đường dẫn tệp).</item>
/// </list>
/// </summary>
public sealed class HeritageModelService(
    IAppDbContext db, RequestValidator validator, IAuditLogger audit, TimeProvider clock, IFileStorage storage, ModelFileService files)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // Việc đã làm, ghi vào nhật ký thao tác.
    private const string Added = "added";
    private const string Replaced = "replaced";
    private const string Updated = "updated";
    private const string Shown = "shown";
    private const string Hidden = "hidden";
    private const string Removed = "removed";

    // ---- Đọc ----

    /// <summary>Mô hình của di sản (chưa gỡ): đang hiển thị trước, rồi theo thứ tự, mới thêm trước.</summary>
    public async Task<IReadOnlyList<HeritageModelDto>> ListAsync(long heritageId, CancellationToken ct)
    {
        var heritage = await db.Heritages.AsNoTracking().Where(h => h.Id == heritageId && h.DeletedAt == null)
                           .Select(h => new { h.Status }).SingleOrDefaultAsync(ct)
                       ?? throw AppException.NotFound(Messages.NotFound("xem mô hình 3D", "di sản"));
        var models = await db.HeritageModels.AsNoTracking().Where(m => m.HeritageId == heritageId && m.DeletedAt == null).ToListAsync(ct);
        return models
            .OrderByDescending(m => m.Status == ContentStatus.PUBLISHED).ThenBy(m => m.SortOrder ?? int.MaxValue).ThenByDescending(m => m.Id)
            .Select(m => ToDto(m, heritage.Status)).ToList();
    }

    // ---- Khai báo ----

    /// <summary>Khai báo mô hình bằng đường dẫn của tệp <c>.glb</c> đã có trong kho tệp.</summary>
    public async Task<HeritageModelDto> CreateAsync(Actor actor, long heritageId, HeritageModelRequest request, CancellationToken ct)
    {
        EnsureAdmin(actor);
        await validator.EnsureValidAsync(request, ct);
        var heritage = await db.FindHeritageAsync(heritageId, "thêm mô hình 3D", ct);
        var stored = await ResolveStoredAsync(request.FileUrl!, ct);
        return await AttachAsync(actor, heritage, request, stored.Url, stored.Size, ct);
    }

    /// <summary>
    /// Tải tệp <c>.glb</c> lên kho rồi khai báo luôn. Kiểm thông tin trước, kiểm nội dung tệp rồi mới lưu; ghi dòng không được thì tệp vừa lưu bị gỡ lại.
    /// </summary>
    public async Task<HeritageModelDto> UploadAsync(Actor actor, long heritageId, HeritageModelRequest request, UploadedFile? file, CancellationToken ct)
    {
        EnsureAdmin(actor);
        await validator.EnsureValidAsync(request with { FileUrl = PlaceholderUrl() }, ct);
        var heritage = await db.FindHeritageAsync(heritageId, "thêm mô hình 3D", ct);
        if (request.Show == true) await EnsureNoOtherVisibleAsync(heritage.Id, exceptId: 0, ct);   // báo trước khi nhận tệp
        var stored = await files.UploadAsync(file, ct);

        try
        {
            return await AttachAsync(actor, heritage, request, stored.Url, stored.Size, ct);
        }
        catch
        {
            await DiscardAsync(stored.Url);
            throw;
        }
    }

    private async Task<HeritageModelDto> AttachAsync(
        Actor actor, Heritage heritage, HeritageModelRequest request, string fileUrl, long size, CancellationToken ct)
    {
        var show = request.Show == true;
        if (show) await EnsureNoOtherVisibleAsync(heritage.Id, exceptId: 0, ct);

        var model = new HeritageModel
        {
            HeritageId = heritage.Id,
            FileUrl = fileUrl,
            FileFormat = ModelFormat.GLB,
            FileSizeBytes = size,
            Status = show ? ContentStatus.PUBLISHED : ContentStatus.DRAFT,
            SortOrder = request.SortOrder,
        };
        Apply(model, request.Title, request.PosterUrl, request.Author, request.License, request.LicenseUrl, request.SourceUrl);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.HeritageModels.Add(model);
        await SaveAsync(model, becomesVisible: show, ct);
        Record(actor, model, Added);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(model, heritage.Status);
    }

    // ---- Sửa, thay tệp ----

    /// <summary>
    /// Sửa thông tin và ghi công (thay toàn bộ). Có <c>FileUrl</c> thì thay tệp bằng tệp khác đã có trong kho; bỏ trống thì giữ tệp.
    /// Ghi nhật ký "thay" khi tệp đổi, "sửa" khi chỉ đổi thông tin.
    /// </summary>
    public async Task<HeritageModelDto> UpdateAsync(Actor actor, long modelId, UpdateHeritageModelRequest request, CancellationToken ct)
    {
        EnsureAdmin(actor);
        await validator.EnsureValidAsync(request, ct);
        var model = await FindAsync(modelId, "sửa mô hình 3D", ct);

        var replaced = false;
        if (!string.IsNullOrWhiteSpace(request.FileUrl))
        {
            var stored = await ResolveStoredAsync(request.FileUrl, ct);
            replaced = stored.Url != model.FileUrl;
            model.FileUrl = stored.Url;
            model.FileSizeBytes = stored.Size;
        }
        Apply(model, request.Title, request.PosterUrl, request.Author, request.License, request.LicenseUrl, request.SourceUrl);
        if (request.SortOrder is not null) model.SortOrder = request.SortOrder;

        Record(actor, model, replaced ? Replaced : Updated);
        await db.SaveChangesAsync(ct);
        return ToDto(model, model.Heritage!.Status);
    }

    /// <summary>Thay tệp của mô hình bằng tệp <c>.glb</c> tải lên (thông tin và ghi công giữ nguyên). Tệp cũ vẫn nằm trong kho.</summary>
    public async Task<HeritageModelDto> ReplaceFileAsync(Actor actor, long modelId, UploadedFile? file, CancellationToken ct)
    {
        EnsureAdmin(actor);
        var model = await FindAsync(modelId, "thay tệp mô hình 3D", ct);
        var stored = await files.UploadAsync(file, ct);
        try
        {
            model.FileUrl = stored.Url;
            model.FileSizeBytes = stored.Size;
            Record(actor, model, Replaced);
            await db.SaveChangesAsync(ct);
            return ToDto(model, model.Heritage!.Status);
        }
        catch
        {
            await DiscardAsync(stored.Url);
            throw;
        }
    }

    // ---- Hiện, ẩn, gỡ ----

    /// <summary>Hiện mô hình (từ nháp hoặc đã ẩn). Chặn khi di sản đã có mô hình khác đang hiển thị.</summary>
    public async Task<HeritageModelDto> ShowAsync(Actor actor, long modelId, CancellationToken ct)
    {
        EnsureAdmin(actor);
        const string action = "hiện mô hình 3D";
        var model = await FindAsync(modelId, action, ct);
        if (model.Status == ContentStatus.PUBLISHED)
            throw AppException.BusinessRule(Messages.Blocked(action, "Mô hình đang được hiển thị."));
        await EnsureNoOtherVisibleAsync(model.HeritageId, model.Id, ct);

        model.Status = ContentStatus.PUBLISHED;
        Record(actor, model, Shown);
        await SaveAsync(model, becomesVisible: true, ct);
        return ToDto(model, model.Heritage!.Status);
    }

    /// <summary>Ẩn mô hình đang hiển thị: không còn nút "Xem 3D" ở trang công khai; tệp và thông tin vẫn lưu.</summary>
    public async Task<HeritageModelDto> HideAsync(Actor actor, long modelId, CancellationToken ct)
    {
        EnsureAdmin(actor);
        const string action = "ẩn mô hình 3D";
        var model = await FindAsync(modelId, action, ct);
        if (model.Status != ContentStatus.PUBLISHED)
            throw AppException.BusinessRule(Messages.Blocked(action, "Mô hình không đang hiển thị."));

        model.Status = ContentStatus.ARCHIVED;
        Record(actor, model, Hidden);
        await db.SaveChangesAsync(ct);
        return ToDto(model, model.Heritage!.Status);
    }

    /// <summary>Gỡ mô hình khỏi di sản (xóa mềm). Tệp trong kho được giữ lại.</summary>
    public async Task RemoveAsync(Actor actor, long modelId, CancellationToken ct)
    {
        EnsureAdmin(actor);
        var model = await FindAsync(modelId, "gỡ mô hình 3D", ct);
        model.DeletedAt = Now;
        Record(actor, model, Removed);
        await db.SaveChangesAsync(ct);
    }

    // ---- Dụng cụ ----

    private static void EnsureAdmin(Actor actor)
    {
        if (actor.Role != RoleCode.ADMIN) throw AppException.Forbidden(Messages.Forbidden);
    }

    private async Task<HeritageModel> FindAsync(long id, string action, CancellationToken ct)
    {
        var model = await db.HeritageModels.Include(m => m.Heritage).SingleOrDefaultAsync(m => m.Id == id && m.DeletedAt == null, ct);
        return model is { Heritage.DeletedAt: null } ? model : throw AppException.NotFound(ModelMessages.NotFound(action));
    }

    /// <summary>Đường dẫn tạm chỉ để chạy luật kiểm thông tin khi tệp còn chưa tải lên.</summary>
    private string PlaceholderUrl() => storage.PublicUrl(StoredFileRules.ModelKey(Guid.Empty));

    /// <summary>
    /// Đường dẫn dán vào phải là của một tệp <c>.glb</c> đang có trong chính kho tệp này (đường dẫn tương đối <c>/files/...</c> hoặc địa chỉ đủ của kho);
    /// trả về địa chỉ chuẩn của kho và dung lượng do kho đo.
    /// </summary>
    private async Task<(string Url, long Size)> ResolveStoredAsync(string fileUrl, CancellationToken ct)
    {
        var key = HeritageModelRules.StoredModelKey(fileUrl)!;
        var url = fileUrl.Trim();
        var canonical = storage.PublicUrl(key);
        if (url != canonical && url != StoredFileRules.PublicPathPrefix + key)
            throw AppException.Validation("fileUrl", Messages.Invalid(HeritageModelValidation.FileUrlLabel, "không thuộc kho tệp của nền tảng"));

        var size = await storage.SizeOfAsync(key, ct)
                   ?? throw AppException.Validation("fileUrl", Messages.Invalid(HeritageModelValidation.FileUrlLabel,
                       "không có tệp này trong kho tệp, hãy tải tệp .glb lên trước"));
        if (size <= 0 || size > StoredFileRules.MaxModelBytes)
            throw AppException.Validation("fileUrl", Messages.Invalid("Tệp mô hình 3D", "tối đa 15 MB"));
        return (canonical, size);
    }

    private static void Apply(HeritageModel model, string? title, string? poster, string? author, string? license, string? licenseUrl, string? sourceUrl)
    {
        model.Title = ValidationRules.TrimToNull(title);
        model.PosterUrl = ValidationRules.TrimToNull(poster);
        model.Author = author!.Trim();
        model.LicenseName = license!.Trim();
        model.LicenseUrl = ValidationRules.TrimToNull(licenseUrl);
        model.SourceUrl = ValidationRules.TrimToNull(sourceUrl);
        model.Credit = HeritageModelRules.ComposeCredit(model.Author, model.LicenseName, model.SourceUrl);
    }

    private async Task EnsureNoOtherVisibleAsync(long heritageId, long exceptId, CancellationToken ct)
    {
        var other = await db.HeritageModels.AsNoTracking().AnyAsync(
            m => m.HeritageId == heritageId && m.Id != exceptId && m.DeletedAt == null && m.Status == ContentStatus.PUBLISHED, ct);
        if (other) throw AppException.BusinessRule(ModelMessages.AnotherVisible);
    }

    /// <summary>Lưu; nếu thất bại vì hai mô hình cùng hiển thị (hai người bấm cùng lúc, chỉ mục duy nhất có lọc chặn) thì báo như quy tắc một mô hình hiển thị.</summary>
    private async Task SaveAsync(HeritageModel model, bool becomesVisible, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (becomesVisible)
        {
            await EnsureNoOtherVisibleAsync(model.HeritageId, model.Id, ct);
            throw;
        }
    }

    private void Record(Actor actor, HeritageModel model, string action) =>
        audit.Record(AuditAction.HERITAGE_MODEL_CHANGED, AuditTargets.HeritageModels, model.Id == 0 ? null : model.Id, actor,
            newValue: new
            {
                action,
                heritageId = model.HeritageId,
                status = model.Status,
                license = model.LicenseName,
                sizeBytes = model.FileSizeBytes,
            });

    private async Task DiscardAsync(string url)
    {
        var key = HeritageModelRules.StoredModelKey(url);
        if (key is null) return;
        try
        {
            await storage.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception)
        {
            // Tệp thừa không được làm mất lỗi gốc.
        }
    }

    internal static HeritageModelDto ToDto(HeritageModel m, ContentStatus? heritageStatus) => new(
        m.Id, m.HeritageId, m.Title, m.FileUrl, m.FileFormat, m.FileSizeBytes, m.PosterUrl, m.LicenseName, m.LicenseUrl, m.Author, m.SourceUrl, m.Credit,
        m.Status, StatusLabel(m.Status), m.Status == ContentStatus.PUBLISHED && heritageStatus == ContentStatus.PUBLISHED, m.SortOrder, m.CreatedAt, m.UpdatedAt);

    public static string StatusLabel(ContentStatus status) => status switch
    {
        ContentStatus.PUBLISHED => "Đang hiển thị",
        ContentStatus.ARCHIVED => "Đã ẩn",
        _ => "Nháp",
    };
}
