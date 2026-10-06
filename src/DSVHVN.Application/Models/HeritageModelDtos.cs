using DSVHVN.Domain.Enums;

namespace DSVHVN.Application.Models;

/// <summary>
/// Khai báo một mô hình 3D cho di sản bằng đường dẫn tệp <c>.glb</c> đã có trong kho tệp (tải trước ở <c>POST /admin/files/models</c>) hoặc từ lần tải lên của chính di sản này.
/// Ghi công bắt buộc: <c>Author</c>, <c>License</c> (CC0, CC BY, CC BY-SA, phạm vi công cộng hoặc mô hình nhóm tự dựng) và trang nguồn <c>SourceUrl</c>
/// (mô hình nhóm tự dựng được bỏ trống). Dung lượng do máy chủ đo, không nhận từ người gửi.
/// </summary>
/// <param name="Show">true thì hiện luôn cho khách và học sinh (khi di sản đã xuất bản); bỏ trống hoặc false thì lưu nháp.</param>
public sealed record HeritageModelRequest(
    string? Title,
    string? FileUrl,
    string? PosterUrl,
    string? Author,
    string? License,
    string? LicenseUrl,
    string? SourceUrl,
    int? SortOrder,
    bool? Show);

/// <summary>
/// Sửa thông tin và ghi công của mô hình (thay toàn bộ các trường). <c>FileUrl</c> bỏ trống thì giữ tệp hiện tại; có thì thay tệp bằng tệp khác đã có trong kho
/// (tệp cũ vẫn được giữ lại trong kho). Trạng thái hiển thị đổi bằng <c>show</c> và <c>hide</c>.
/// </summary>
public sealed record UpdateHeritageModelRequest(
    string? Title,
    string? FileUrl,
    string? PosterUrl,
    string? Author,
    string? License,
    string? LicenseUrl,
    string? SourceUrl,
    int? SortOrder);

/// <summary>
/// Mô hình 3D trong tab Mô hình 3D của màn hình Biên tập di sản. <c>IsVisibleToPublic</c> true khi mô hình đang hiển thị <b>và</b> di sản đã xuất bản
/// (khi đó trang công khai có nút "Xem 3D"). <c>StatusLabel</c> là chữ hiển thị sẵn: Nháp, Đang hiển thị, Đã ẩn.
/// </summary>
public sealed record HeritageModelDto(
    long Id,
    long HeritageId,
    string? Title,
    string FileUrl,
    ModelFormat FileFormat,
    long FileSizeBytes,
    string? PosterUrl,
    string License,
    string? LicenseUrl,
    string Author,
    string? SourceUrl,
    string Credit,
    ContentStatus Status,
    string StatusLabel,
    bool IsVisibleToPublic,
    int? SortOrder,
    DateTime? CreatedAt,
    DateTime? UpdatedAt);

/// <summary>
/// Dữ liệu cho trình xem 3D dùng chung của web học tập và app: tệp <c>.glb</c> (<c>ContentType</c> model/gltf-binary), dung lượng để cảnh báo trước khi tải trên mạng di động,
/// ảnh xem trước trong lúc tải, và ghi công hiện ngay trong trình xem ở mọi ngôn ngữ (tác giả, giấy phép, nguồn không dịch).
/// Tải lỗi hoặc thiết bị không hỗ trợ thì trình xem quay về bộ ảnh của di sản kèm <see cref="ModelMessages.ViewerFallback"/>.
/// </summary>
public sealed record PublicModelDto(
    long Id,
    string? Title,
    string FileUrl,
    string ContentType,
    long FileSizeBytes,
    string? PosterUrl,
    string Author,
    string License,
    string? LicenseUrl,
    string? SourceUrl,
    string Credit);
