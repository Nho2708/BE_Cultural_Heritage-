using DSVHVN.Application.Files;
using DSVHVN.Domain.Rules;
using SkiaSharp;

namespace DSVHVN.Infrastructure.Files;

/// <summary>
/// Xử lý ảnh tải lên bằng SkiaSharp (giấy phép MIT). Định dạng xác định bằng nội dung tệp; ảnh được giải mã rồi mã hóa lại,
/// nên mọi dữ liệu đi kèm (EXIF, GPS, ghi chú PNG, hồ sơ màu nhúng) đều không còn trong bản lưu. Chiều xoay theo EXIF được áp dụng vào điểm ảnh trước khi bỏ EXIF.
/// </summary>
public sealed class SkiaImageProcessor : IImageProcessor
{
    private const int JpegQuality = 90;
    private const int WebpQuality = 90;
    private const int ThumbnailQuality = 78;

    public ImageProcessResult Process(byte[] content) => Run(content, (upright, encoded, extension) =>
    {
        var stored = Encode(upright, encoded, encoded == SKEncodedImageFormat.Jpeg ? JpegQuality : WebpQuality);
        using var small = Thumbnail(upright);
        var thumbnail = Encode(small, SKEncodedImageFormat.Webp, ThumbnailQuality);
        if (stored is null || thumbnail is null) return ImageProcessResult.Failure(ImageProblem.Unreadable);

        return ImageProcessResult.Success(new ProcessedImage(extension, stored, thumbnail, upright.Width, upright.Height));
    });

    public ImageProcessResult ProcessAvatar(byte[] content) => Run(content, (upright, _, _) =>
    {
        using var square = SquareAvatar(upright);
        var stored = Encode(square, SKEncodedImageFormat.Webp, WebpQuality);
        if (stored is null) return ImageProcessResult.Failure(ImageProblem.Unreadable);

        return ImageProcessResult.Success(new ProcessedImage(StoredFileRules.Webp, stored, [], square.Width, square.Height));
    });

    /// <summary>
    /// Giải mã trọn vẹn, đưa về đúng chiều rồi giao ảnh cho <paramref name="encode"/>. Việc kiểm định dạng theo nội dung, giới hạn điểm ảnh
    /// và bỏ EXIF (do giải mã rồi mã hóa lại) dùng chung cho mọi loại ảnh.
    /// </summary>
    private static ImageProcessResult Run(byte[] content, Func<SKBitmap, SKEncodedImageFormat, string, ImageProcessResult> encode)
    {
        try
        {
            using var data = SKData.CreateCopy(content);
            using var codec = SKCodec.Create(data);
            if (codec is null) return ImageProcessResult.Failure(ImageProblem.Unreadable);

            var encoded = codec.EncodedFormat;
            var extension = encoded switch
            {
                SKEncodedImageFormat.Jpeg => StoredFileRules.Jpeg,
                SKEncodedImageFormat.Png => StoredFileRules.Png,
                SKEncodedImageFormat.Webp => StoredFileRules.Webp,
                _ => null,
            };
            if (extension is null) return ImageProcessResult.Failure(ImageProblem.UnsupportedFormat);

            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0) return ImageProcessResult.Failure(ImageProblem.Unreadable);
            if ((long)info.Width * info.Height > StoredFileRules.MaxImagePixels) return ImageProcessResult.Failure(ImageProblem.TooLarge);

            var target = new SKImageInfo(info.Width, info.Height, SKImageInfo.PlatformColorType,
                info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
            using var decoded = new SKBitmap(target);
            // Chỉ nhận ảnh giải mã trọn vẹn: ảnh cụt (tải dở, cố ý cắt) trả mã khác Success dù vẫn ra được một phần điểm ảnh.
            if (codec.GetPixels(target, decoded.GetPixels()) != SKCodecResult.Success)
                return ImageProcessResult.Failure(ImageProblem.Unreadable);

            using var upright = ApplyOrigin(decoded, codec.EncodedOrigin);
            return encode(upright, encoded, extension);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Tệp hỏng hoặc cố ý làm hỏng bộ giải mã: coi như không đọc được, không để lộ chi tiết.
            return ImageProcessResult.Failure(ImageProblem.Unreadable);
        }
    }

    /// <summary>Cắt hình vuông ở giữa (cạnh bằng cạnh ngắn của ảnh) rồi thu nhỏ còn tối đa <see cref="StoredFileRules.AvatarMaxSide"/> điểm ảnh; ảnh nhỏ hơn không bị phóng to.</summary>
    private static SKBitmap SquareAvatar(SKBitmap source)
    {
        var side = Math.Min(source.Width, source.Height);
        var left = (source.Width - side) / 2;
        var top = (source.Height - side) / 2;
        var size = Math.Min(side, StoredFileRules.AvatarMaxSide);

        var result = new SKBitmap(new SKImageInfo(size, size, source.ColorType, source.AlphaType));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, new SKRect(left, top, left + side, top + side), new SKRect(0, 0, size, size),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), null);
        return result;
    }

    private static byte[]? Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data?.ToArray();
    }

    /// <summary>Ảnh vừa khung <see cref="StoredFileRules.ThumbnailMaxSide"/> điểm ảnh, giữ tỉ lệ, không phóng to ảnh nhỏ.</summary>
    private static SKBitmap Thumbnail(SKBitmap source)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (longest <= StoredFileRules.ThumbnailMaxSide) return source.Copy();
        var scale = (double)StoredFileRules.ThumbnailMaxSide / longest;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return source.Resize(new SKImageInfo(width, height, source.ColorType, source.AlphaType),
                   new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
               ?? throw new InvalidOperationException("Không thu nhỏ được ảnh.");
    }

    /// <summary>Đưa điểm ảnh về đúng chiều hiển thị theo giá trị xoay/lật của EXIF (8 trường hợp).</summary>
    private static SKBitmap ApplyOrigin(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return source.Copy();

        float w = source.Width, h = source.Height;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };

        var result = new SKBitmap(new SKImageInfo(swap ? source.Height : source.Width, swap ? source.Width : source.Height,
            source.ColorType, source.AlphaType));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(matrix);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        return result;
    }
}
