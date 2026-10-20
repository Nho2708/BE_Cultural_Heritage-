using System.Net;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using DSVHVN.Application.Files;
using DSVHVN.Domain.Rules;
using Microsoft.Extensions.Options;

namespace DSVHVN.Infrastructure.Files;

/// <summary>
/// Kho tệp trên Cloudinary (dùng khi triển khai). Chỉ nhận khóa do hệ thống sinh (<see cref="StoredFileRules.IsValidKey"/>) và ánh xạ khóa thành mã công khai
/// (<c>public id</c>) cố định, nên địa chỉ HTTPS của tệp tính được từ khóa mà không cần hỏi Cloudinary:
/// <list type="bullet">
/// <item>Ảnh <c>images/{mã}.jpg|png|webp</c> tải lên loại image với public id <c>{thư mục}/images/{mã}</c>; địa chỉ có đuôi theo định dạng thật của ảnh.</item>
/// <item>Mô hình <c>models/{mã}.glb</c> và thuyết minh <c>thuyet-minh/{mã}.mp3</c> tải lên loại raw, public id giữ cả đuôi <c>{thư mục}/models/{mã}.glb</c>.</item>
/// <item>Ảnh thu nhỏ <c>images/{mã}-thumb.webp</c> <b>không tải thêm tệp</b>: Cloudinary tự sinh bằng địa chỉ biến đổi (<c>c_limit,w_320,q_auto</c> trên ảnh gốc),
/// nên ghi và xóa ảnh thu nhỏ là việc không làm gì, còn địa chỉ của nó là địa chỉ biến đổi.</item>
/// </list>
/// Bí mật API chỉ nằm trong đối tượng <see cref="Cloudinary"/> (ký yêu cầu ở phía máy chủ), không xuất hiện trong địa chỉ, thông điệp lỗi hay log.
/// Mọi lỗi gọi Cloudinary được đổi thành <see cref="FileStorageException"/> không kèm thông tin kết nối.
/// </summary>
public sealed class CloudinaryFileStorage : IFileStorage
{
    private const string DeliveryHost = "https://res.cloudinary.com";

    private readonly Cloudinary _cloudinary;
    private readonly string _cloudName;
    private readonly string _folder;

    /// <param name="httpClient">Chỉ để kiểm thử (HTTP giả); bình thường để trống thì dùng HttpClient mặc định của thư viện.</param>
    public CloudinaryFileStorage(IOptions<StorageOptions> options, HttpClient? httpClient = null)
    {
        var value = options.Value.Cloudinary;
        _cloudName = value.CloudName.Trim();
        _folder = string.IsNullOrWhiteSpace(value.Folder) ? CloudinaryStorageOptions.DefaultFolder : value.Folder.Trim().Trim('/');
        _cloudinary = new Cloudinary(new Account(_cloudName, value.ApiKey.Trim(), value.ApiSecret.Trim())) { Api = { Secure = true } };
        if (httpClient is not null) _cloudinary.Api.Client = httpClient;
    }

    public async Task SaveAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var target = Resolve(key);
        if (target.IsThumbnail) return;

        await using var stream = new MemoryStream(content.ToArray(), writable: false);
        var file = new FileDescription(Path.GetFileName(key), stream);
        UploadResult result;
        try
        {
            result = target.IsRaw
                ? await _cloudinary.UploadAsync(new RawUploadParams
                {
                    File = file, PublicId = target.PublicId, Overwrite = true, UniqueFilename = false, UseFilename = false,
                }, "raw", cancellationToken)
                : await _cloudinary.UploadAsync(new ImageUploadParams
                {
                    File = file, PublicId = target.PublicId, Overwrite = true, UniqueFilename = false, UseFilename = false,
                }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FileStorageException("Không gọi được Cloudinary khi tải tệp lên.", ex);
        }

        if (result.StatusCode != HttpStatusCode.OK || result.Error is not null || string.IsNullOrEmpty(result.PublicId))
            throw new FileStorageException(
                $"Cloudinary từ chối tải tệp lên (HTTP {(int)result.StatusCode}{(result.Error is null ? string.Empty : ": " + result.Error.Message)}).");
        if (!string.Equals(result.PublicId, target.PublicId, StringComparison.Ordinal))
            throw new FileStorageException("Cloudinary trả mã tệp khác với mã đã yêu cầu.");
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) =>
        await GetResourceAsync(key) is not null;

    public async Task<long?> SizeOfAsync(string key, CancellationToken cancellationToken) =>
        (await GetResourceAsync(key))?.Bytes;

    /// <summary>Hỏi Cloudinary về tệp; null nếu không có tệp. Lỗi khác đổi thành <see cref="FileStorageException"/>.</summary>
    private async Task<GetResourceResult?> GetResourceAsync(string key)
    {
        var target = Resolve(key);
        try
        {
            var result = await _cloudinary.GetResourceAsync(new GetResourceParams(target.PublicId)
            {
                ResourceType = target.IsRaw ? ResourceType.Raw : ResourceType.Image,
            });
            if (result.StatusCode == HttpStatusCode.NotFound) return null;
            if (result.StatusCode == HttpStatusCode.OK && result.Error is null) return result;
            throw new FileStorageException($"Cloudinary trả HTTP {(int)result.StatusCode} khi kiểm tra tệp.");
        }
        catch (FileStorageException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FileStorageException("Không gọi được Cloudinary khi kiểm tra tệp.", ex);
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var target = Resolve(key);
        if (target.IsThumbnail) return;

        DeletionResult result;
        try
        {
            result = await _cloudinary.DestroyAsync(new DeletionParams(target.PublicId)
            {
                ResourceType = target.IsRaw ? ResourceType.Raw : ResourceType.Image,
                Invalidate = true,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FileStorageException("Không gọi được Cloudinary khi xóa tệp.", ex);
        }

        // "not found" nghĩa là tệp đã không còn: coi như xóa xong.
        if (result.StatusCode != HttpStatusCode.OK || result.Error is not null || (result.Result is not "ok" and not "not found"))
            throw new FileStorageException($"Cloudinary không xóa được tệp (HTTP {(int)result.StatusCode}).");
    }

    public string PublicUrl(string key)
    {
        var target = Resolve(key);
        if (target.IsRaw) return $"{DeliveryHost}/{_cloudName}/raw/upload/{target.PublicId}";
        if (target.IsThumbnail)
            return $"{DeliveryHost}/{_cloudName}/image/upload/{StoredFileRules.CloudinaryThumbnailTransformation}/{target.PublicId}.{StoredFileRules.Webp}";
        return $"{DeliveryHost}/{_cloudName}/image/upload/{target.PublicId}.{ExtensionOf(key)}";
    }

    private static string ExtensionOf(string key) => key[(key.LastIndexOf('.') + 1)..];

    private Target Resolve(string key)
    {
        if (!StoredFileRules.IsValidKey(key)) throw new ArgumentException("Khóa tệp không hợp lệ.", nameof(key));
        if (StoredFileRules.IsRawKey(key))
            return new Target($"{_folder}/{key}", IsRaw: true, IsThumbnail: false);

        var withoutExtension = key[..key.LastIndexOf('.')];
        var isThumbnail = withoutExtension.EndsWith("-thumb", StringComparison.Ordinal);
        if (isThumbnail) withoutExtension = withoutExtension[..^"-thumb".Length];
        return new Target($"{_folder}/{withoutExtension}", IsRaw: false, isThumbnail);
    }

    private sealed record Target(string PublicId, bool IsRaw, bool IsThumbnail);
}
