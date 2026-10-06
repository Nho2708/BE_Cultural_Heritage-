using DSVHVN.Application.Common;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Files;

/// <summary>
/// Tải tệp mô hình 3D <c>.glb</c> lên kho tệp (quản trị hệ thống). Chỉ lưu tệp và trả đường dẫn trong kho; phần gắn mô hình vào di sản
/// kèm ghi công và bật/tắt hiển thị là của <c>HeritageModelService</c>, dùng chính dịch vụ này để tải tệp rồi khai báo.
/// Kiểm bằng nội dung: glTF 2.0 đóng gói một tệp, tối đa 15 MB, mọi dữ liệu nằm trong tệp.
/// </summary>
public sealed class ModelFileService(IFileStorage storage)
{
    public const string ContentType = "model/gltf-binary";

    public async Task<StoredFileDto> UploadAsync(UploadedFile? file, CancellationToken ct)
    {
        if (file is null || file.Content.Length == 0)
            throw AppException.Validation("file", Messages.Required("tệp mô hình 3D"));
        if (file.Content.Length > StoredFileRules.MaxModelBytes)
            throw AppException.Validation("file", Messages.Invalid("Tệp mô hình 3D", "tối đa 15 MB"));

        var problem = GlbInspector.Inspect(file.Content);
        if (problem != GlbProblem.None)
            throw AppException.Validation("file", Messages.Invalid("Tệp mô hình 3D", problem switch
            {
                GlbProblem.WrongVersion => "chỉ nhận glTF 2.0",
                GlbProblem.ExternalResource => "không được tham chiếu tệp ngoài, kết cấu và dữ liệu phải nằm trong tệp .glb",
                _ => "chỉ nhận tệp .glb (glTF 2.0 đóng gói một tệp), tệp này không đọc được",
            }));

        var key = StoredFileRules.ModelKey(Guid.NewGuid());
        await storage.SaveAsync(key, file.Content, ct);
        return new StoredFileDto(storage.PublicUrl(key), null, file.Content.Length, ContentType);
    }
}
