using DSVHVN.Domain.Common;
using DSVHVN.Domain.Enums;

namespace DSVHVN.Domain.Heritages;

/// <summary>
/// Bảng <c>heritage_models</c>: mô hình 3D giấy phép mở của một di sản (tệp <c>.glb</c>, glTF 2.0 đóng gói một tệp), do quản trị hệ thống khai báo
/// ở tab Mô hình 3D của màn hình Biên tập di sản. Bắt buộc ghi công (tác giả, giấy phép, nguồn). Mỗi di sản có tối đa một mô hình đang hiển thị
/// (<see cref="ContentStatus.PUBLISHED"/>); mô hình đã gỡ chỉ bị xóa mềm. Mô hình chỉ hiện cho khách và học sinh khi di sản cũng đang xuất bản.
/// </summary>
public class HeritageModel : IHasCreatedAt, IHasUpdatedAt, ISoftDeletable
{
    public long Id { get; set; }

    public long HeritageId { get; set; }
    public Heritage? Heritage { get; set; }

    /// <summary>Tên mô hình (tiếng Việt); trình xem hiện tên di sản theo ngôn ngữ đã chọn nên tên này không dịch.</summary>
    public string? Title { get; set; }

    /// <summary>Đường dẫn tệp trong kho tệp của nền tảng (<c>/files/models/{mã}.glb</c> hoặc địa chỉ đầy đủ của kho), không bao giờ là trang ngoài.</summary>
    public string FileUrl { get; set; } = string.Empty;

    public ModelFormat FileFormat { get; set; } = ModelFormat.GLB;

    /// <summary>Dung lượng tệp (byte) do máy chủ đo, để cảnh báo trước khi tải trên mạng di động.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>Ảnh xem trước hiện trong lúc tải mô hình: đường dẫn http hoặc https, hoặc ảnh đã tải lên kho.</summary>
    public string? PosterUrl { get; set; }

    /// <summary>Giấy phép mở: CC0, CC BY, CC BY-SA (kèm số phiên bản), phạm vi công cộng hoặc mô hình nhóm tự dựng.</summary>
    public string LicenseName { get; set; } = string.Empty;

    public string? LicenseUrl { get; set; }

    public string Author { get; set; } = string.Empty;

    /// <summary>Trang nguồn nơi tải mô hình; mô hình nhóm tự dựng có thể bỏ trống.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>Dòng ghi công hiện dưới trình xem: "tác giả · giấy phép · nguồn". Không dịch.</summary>
    public string Credit { get; set; } = string.Empty;

    /// <summary>DRAFT (chưa hiện), PUBLISHED (hiện cho khách và học sinh khi di sản xuất bản), ARCHIVED (ẩn).</summary>
    public ContentStatus Status { get; set; }

    public int? SortOrder { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    /// <summary>Thời điểm gỡ mô hình (xóa mềm).</summary>
    public DateTime? DeletedAt { get; set; }
}
