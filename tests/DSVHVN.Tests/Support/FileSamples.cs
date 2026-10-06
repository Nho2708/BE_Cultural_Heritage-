using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace DSVHVN.Tests.Support;

/// <summary>Tệp mẫu dựng bằng mã cho kiểm thử tải tệp: ảnh có EXIF, tệp giả đuôi, tệp .glb đủ kiểu.</summary>
public static class FileSamples
{
    /// <summary>Ảnh chia bốn góc màu khác nhau (trái trên đỏ, phải trên xanh lá, trái dưới xanh dương, phải dưới vàng) để kiểm chiều xoay.</summary>
    public static SKBitmap Quadrants(int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint();
        void Fill(SKColor color, float left, float top, float right, float bottom)
        {
            paint.Color = color;
            canvas.DrawRect(left, top, right - left, bottom - top, paint);
        }
        Fill(SKColors.Red, 0, 0, width / 2f, height / 2f);
        Fill(SKColors.Lime, width / 2f, 0, width, height / 2f);
        Fill(SKColors.Blue, 0, height / 2f, width / 2f, height);
        Fill(SKColors.Yellow, width / 2f, height / 2f, width, height);
        return bitmap;
    }

    public static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality = 90)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    public static byte[] Jpeg(int width = 64, int height = 32) { using var b = Quadrants(width, height); return Encode(b, SKEncodedImageFormat.Jpeg); }

    public static byte[] Png(int width = 64, int height = 32) { using var b = Quadrants(width, height); return Encode(b, SKEncodedImageFormat.Png); }

    public static byte[] Webp(int width = 64, int height = 32) { using var b = Quadrants(width, height); return Encode(b, SKEncodedImageFormat.Webp); }

    public static byte[] Gif() => Convert.FromBase64String("R0lGODlhAQABAIAAAAUEBAAAACwAAAAAAQABAAACAkQBADs=");

    /// <summary>Chèn đoạn EXIF (vị trí xoay và một mẩu "GPS" dễ nhận ra) ngay sau dấu mở đầu của tệp JPEG.</summary>
    public static byte[] WithExif(byte[] jpeg, ushort orientation)
    {
        var tiff = new List<byte>();
        tiff.AddRange("MM"u8.ToArray());
        tiff.AddRange(new byte[] { 0, 42, 0, 0, 0, 8 });                      // đầu TIFF, IFD đầu tại byte 8
        tiff.AddRange(new byte[] { 0, 1 });                                    // một mục
        tiff.AddRange(new byte[] { 0x01, 0x12, 0, 3, 0, 0, 0, 1 });            // tag 0x0112 (xoay), kiểu SHORT, số lượng 1
        tiff.AddRange(new byte[] { (byte)(orientation >> 8), (byte)orientation, 0, 0 });
        tiff.AddRange(new byte[] { 0, 0, 0, 0 });                              // hết IFD
        tiff.AddRange(Encoding.ASCII.GetBytes(Marker));                        // dữ liệu thừa sau IFD, thay cho tọa độ GPS

        var payload = new List<byte>();
        payload.AddRange("Exif\0\0"u8.ToArray());
        payload.AddRange(tiff);

        var segment = new List<byte> { 0xFF, 0xE1, (byte)((payload.Count + 2) >> 8), (byte)(payload.Count + 2) };
        segment.AddRange(payload);

        var result = new List<byte>(jpeg.Length + segment.Count);
        result.AddRange(jpeg.AsSpan(0, 2).ToArray());
        result.AddRange(segment);
        result.AddRange(jpeg.AsSpan(2).ToArray());
        return [.. result];
    }

    /// <summary>Chuỗi đặt vào phần EXIF của tệp mẫu; còn thấy chuỗi này trong tệp lưu nghĩa là EXIF chưa bị bỏ.</summary>
    public const string Marker = "GPS-LAT-10.7769N-LON-106.7009E";

    public static bool Contains(byte[] haystack, string text) => haystack.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;

    /// <summary>Tệp .glb tối thiểu hợp lệ: khối JSON glTF 2.0 (thêm phần <paramref name="extra"/> nếu có) và một khối nhị phân nhỏ.</summary>
    public static byte[] Glb(object? extra = null, string version = "2.0", uint? declaredLength = null, bool withBinary = true)
    {
        var json = new Dictionary<string, object?>
        {
            ["asset"] = new Dictionary<string, object?> { ["version"] = version },
            ["buffers"] = new[] { new Dictionary<string, object?> { ["byteLength"] = 4 } },
        };
        if (extra is IDictionary<string, object?> more) foreach (var (k, v) in more) json[k] = v;

        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(json));
        var padded = jsonBytes.Concat(Enumerable.Repeat((byte)' ', (4 - jsonBytes.Length % 4) % 4)).ToArray();
        var binary = new byte[] { 1, 2, 3, 4 };

        using var stream = new MemoryStream();
        var length = 12 + 8 + padded.Length + (withBinary ? 8 + binary.Length : 0);
        WriteUInt(stream, 0x46546C67);
        WriteUInt(stream, 2);
        WriteUInt(stream, declaredLength ?? (uint)length);
        WriteUInt(stream, (uint)padded.Length);
        WriteUInt(stream, 0x4E4F534A);
        stream.Write(padded);
        if (withBinary)
        {
            WriteUInt(stream, (uint)binary.Length);
            WriteUInt(stream, 0x004E4942);
            stream.Write(binary);
        }
        return stream.ToArray();
    }

    private static void WriteUInt(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }
}
