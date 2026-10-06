using System.Text;
using DSVHVN.Application.Files;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Files;
using DSVHVN.Tests.Support;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy tắc thuần của tệp tải lên: tên khóa, đường dẫn công khai, ảnh thu nhỏ.</summary>
public sealed class StoredFileRulesTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("images/" + Id + ".jpg", true)]
    [InlineData("images/" + Id + ".png", true)]
    [InlineData("images/" + Id + ".webp", true)]
    [InlineData("images/" + Id + "-thumb.webp", true)]
    [InlineData("models/" + Id + ".glb", true)]
    [InlineData("images/" + Id + ".gif", false)]
    [InlineData("images/" + Id + ".jpg.exe", false)]
    [InlineData("images/../" + Id + ".jpg", false)]
    [InlineData("../images/" + Id + ".jpg", false)]
    [InlineData("images\\" + Id + ".jpg", false)]
    [InlineData("/images/" + Id + ".jpg", false)]
    [InlineData("images/ten-do-nguoi-dung.jpg", false)]
    [InlineData("images/" + Id + "0.jpg", false)]
    [InlineData("other/" + Id + ".jpg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_keys_generated_by_the_system_are_valid(string? key, bool expected) =>
        Assert.Equal(expected, StoredFileRules.IsValidKey(key));

    [Theory]
    [InlineData("/files/images/" + Id + ".jpg", true)]
    [InlineData("/files/models/" + Id + ".glb", true)]
    [InlineData("/files/images/" + Id + "-thumb.webp", false)]
    [InlineData("/files/../secret.jpg", false)]
    [InlineData("/other/images/" + Id + ".jpg", false)]
    [InlineData("files/images/" + Id + ".jpg", false)]
    public void A_relative_stored_path_has_a_fixed_shape(string url, bool expected) =>
        Assert.Equal(expected, StoredFileRules.IsStoredPath(url));

    [Fact]
    public void The_preview_of_an_uploaded_image_sits_next_to_it_and_external_images_have_none()
    {
        Assert.Equal($"/files/images/{Id}-thumb.webp", StoredFileRules.ThumbnailUrlOf($"/files/images/{Id}.png"));
        Assert.Equal($"https://api.example.vn/files/images/{Id}-thumb.webp",
            StoredFileRules.ThumbnailUrlOf($"https://api.example.vn/files/images/{Id}.jpg"));
        Assert.Null(StoredFileRules.ThumbnailUrlOf("https://upload.wikimedia.org/a.jpg"));
        Assert.Null(StoredFileRules.ThumbnailUrlOf($"/files/models/{Id}.glb"));
        Assert.Null(StoredFileRules.ThumbnailUrlOf(null));
    }

    [Fact]
    public void Limits_follow_the_business_rules()
    {
        Assert.Equal(5 * 1024 * 1024, StoredFileRules.MaxImageBytes);
        Assert.Equal(15 * 1024 * 1024, StoredFileRules.MaxModelBytes);
    }
}

/// <summary>Xử lý ảnh bằng nội dung tệp: định dạng thật, bỏ EXIF, xoay đúng chiều, ảnh thu nhỏ.</summary>
public sealed class SkiaImageProcessorTests
{
    private readonly SkiaImageProcessor _processor = new();

    [Theory]
    [InlineData("jpg")]
    [InlineData("png")]
    [InlineData("webp")]
    public void The_three_allowed_formats_are_accepted_by_content(string format)
    {
        var bytes = format switch { "jpg" => FileSamples.Jpeg(), "png" => FileSamples.Png(), _ => FileSamples.Webp() };

        var result = _processor.Process(bytes);

        Assert.Equal(ImageProblem.None, result.Problem);
        Assert.Equal(format, result.Image!.Extension);
        Assert.Equal((64, 32), (result.Image.Width, result.Image.Height));
        using var stored = SKBitmap.Decode(result.Image.Content);
        Assert.Equal((64, 32), (stored.Width, stored.Height));
    }

    [Fact]
    public void The_format_comes_from_the_content_not_from_a_claimed_extension()
    {
        // Một ảnh PNG dù đặt tên gì cũng được nhận là PNG; người gọi không truyền tên tệp cho bộ xử lý.
        var result = _processor.Process(FileSamples.Png());
        Assert.Equal("png", result.Image!.Extension);
    }

    [Fact]
    public void A_gif_is_recognised_but_not_accepted()
    {
        var result = _processor.Process(FileSamples.Gif());
        Assert.Equal(ImageProblem.UnsupportedFormat, result.Problem);
        Assert.Null(result.Image);
    }

    [Fact]
    public void Text_or_a_program_renamed_to_an_image_is_unreadable()
    {
        Assert.Equal(ImageProblem.Unreadable, _processor.Process(Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>")).Problem);
        Assert.Equal(ImageProblem.Unreadable, _processor.Process("MZ\u0090\0\u0003\0\0\0"u8.ToArray()).Problem);
        Assert.Equal(ImageProblem.Unreadable, _processor.Process([]).Problem);
    }

    [Fact]
    public void A_truncated_image_is_unreadable()
    {
        var jpeg = FileSamples.Jpeg(200, 200);
        Assert.Equal(ImageProblem.Unreadable, _processor.Process(jpeg[..(jpeg.Length / 3)]).Problem);
        var png = FileSamples.Png(200, 200);
        Assert.Equal(ImageProblem.Unreadable, _processor.Process(png[..(png.Length / 2)]).Problem);
    }

    [Fact]
    public void An_image_with_too_many_pixels_is_refused_before_decoding()
    {
        // Ảnh một màu nén rất nhỏ nhưng giải ra cần bộ nhớ lớn: 6000 x 6000 = 36 triệu điểm ảnh.
        using var bitmap = new SKBitmap(new SKImageInfo(6000, 6000, SKColorType.Gray8, SKAlphaType.Opaque));
        var png = FileSamples.Encode(bitmap, SKEncodedImageFormat.Png);
        Assert.True(png.Length < StoredFileRules.MaxImageBytes);

        Assert.Equal(ImageProblem.TooLarge, _processor.Process(png).Problem);
    }

    [Fact]
    public void Exif_is_removed_from_the_stored_image()
    {
        var withExif = FileSamples.WithExif(FileSamples.Jpeg(), orientation: 1);
        Assert.True(FileSamples.Contains(withExif, FileSamples.Marker));    // tệp mẫu thật sự mang dữ liệu EXIF

        var result = _processor.Process(withExif);

        Assert.Equal(ImageProblem.None, result.Problem);
        Assert.False(FileSamples.Contains(result.Image!.Content, FileSamples.Marker));
        Assert.False(FileSamples.Contains(result.Image.Content, "Exif"));
        Assert.False(FileSamples.Contains(result.Image.Thumbnail, "Exif"));
    }

    [Fact]
    public void Exif_rotation_is_applied_to_the_pixels_before_exif_is_dropped()
    {
        // Ảnh rộng 64 x 32 ghi "xoay 90 độ theo chiều kim đồng hồ" (6): hiển thị đúng phải là ảnh cao 32 x 64,
        // góc trái trên của ảnh gốc (đỏ) chuyển sang góc phải trên.
        var result = _processor.Process(FileSamples.WithExif(FileSamples.Jpeg(64, 32), orientation: 6));

        Assert.Equal((32, 64), (result.Image!.Width, result.Image.Height));
        using var stored = SKBitmap.Decode(result.Image.Content);
        Assert.Equal((32, 64), (stored.Width, stored.Height));
        AssertNear(SKColors.Red, stored.GetPixel(28, 4));       // phải trên
        AssertNear(SKColors.Lime, stored.GetPixel(28, 60));     // phải dưới
        AssertNear(SKColors.Blue, stored.GetPixel(4, 4));       // trái trên
        AssertNear(SKColors.Yellow, stored.GetPixel(4, 60));    // trái dưới
    }

    [Fact]
    public void Exif_flip_and_half_turn_are_applied_too()
    {
        // 3: xoay 180 độ - đỏ sang góc phải dưới. 2: lật ngang - đỏ sang góc phải trên.
        using (var turned = SKBitmap.Decode(_processor.Process(FileSamples.WithExif(FileSamples.Jpeg(64, 32), 3)).Image!.Content))
            AssertNear(SKColors.Red, turned.GetPixel(60, 28));
        using (var mirrored = SKBitmap.Decode(_processor.Process(FileSamples.WithExif(FileSamples.Jpeg(64, 32), 2)).Image!.Content))
            AssertNear(SKColors.Red, mirrored.GetPixel(60, 4));
    }

    [Fact]
    public void The_thumbnail_is_a_small_webp_that_never_enlarges_small_images()
    {
        var big = _processor.Process(FileSamples.Jpeg(1200, 600)).Image!;
        using var thumb = SKBitmap.Decode(big.Thumbnail);
        Assert.Equal(SKEncodedImageFormat.Webp, SKCodec.Create(new MemoryStream(big.Thumbnail))!.EncodedFormat);
        Assert.Equal((320, 160), (thumb.Width, thumb.Height));

        var tall = _processor.Process(FileSamples.Png(300, 900)).Image!;
        using var tallThumb = SKBitmap.Decode(tall.Thumbnail);
        Assert.Equal(320, tallThumb.Height);
        Assert.InRange(tallThumb.Width, 105, 108);

        var small = _processor.Process(FileSamples.Png(64, 32)).Image!;
        using var smallThumb = SKBitmap.Decode(small.Thumbnail);
        Assert.Equal((64, 32), (smallThumb.Width, smallThumb.Height));
    }

    private static void AssertNear(SKColor expected, SKColor actual)
    {
        Assert.InRange(Math.Abs(expected.Red - actual.Red), 0, 60);
        Assert.InRange(Math.Abs(expected.Green - actual.Green), 0, 60);
        Assert.InRange(Math.Abs(expected.Blue - actual.Blue), 0, 60);
    }
}

/// <summary>Kiểm tệp .glb bằng nội dung.</summary>
public sealed class GlbInspectorTests
{
    [Fact]
    public void A_packed_gltf_2_file_is_accepted() =>
        Assert.Equal(GlbProblem.None, GlbInspector.Inspect(FileSamples.Glb()));

    [Fact]
    public void Data_embedded_in_the_file_as_a_data_uri_is_inside_the_file_and_accepted() =>
        Assert.Equal(GlbProblem.None, GlbInspector.Inspect(FileSamples.Glb(new Dictionary<string, object?>
        {
            ["images"] = new[] { new Dictionary<string, object?> { ["uri"] = "data:image/png;base64,AAAA" } },
        })));

    [Theory]
    [InlineData("https://example.com/texture.png")]
    [InlineData("texture.png")]
    [InlineData("../../etc/passwd")]
    public void Any_reference_to_a_resource_outside_the_file_is_refused(string uri)
    {
        Assert.Equal(GlbProblem.ExternalResource, GlbInspector.Inspect(FileSamples.Glb(new Dictionary<string, object?>
        {
            ["images"] = new[] { new Dictionary<string, object?> { ["uri"] = uri } },
        })));
        Assert.Equal(GlbProblem.ExternalResource, GlbInspector.Inspect(FileSamples.Glb(new Dictionary<string, object?>
        {
            ["buffers"] = new[] { new Dictionary<string, object?> { ["byteLength"] = 4, ["uri"] = uri } },
        })));
    }

    [Fact]
    public void Only_gltf_2_is_accepted() =>
        Assert.Equal(GlbProblem.WrongVersion, GlbInspector.Inspect(FileSamples.Glb(version: "1.0")));

    [Fact]
    public void Broken_or_foreign_files_are_not_glb()
    {
        Assert.Equal(GlbProblem.NotGlb, GlbInspector.Inspect([]));
        Assert.Equal(GlbProblem.NotGlb, GlbInspector.Inspect(Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"}}")));   // .gltf là JSON thuần
        Assert.Equal(GlbProblem.NotGlb, GlbInspector.Inspect(FileSamples.Png()));
        Assert.Equal(GlbProblem.NotGlb, GlbInspector.Inspect(FileSamples.Glb(declaredLength: 9999)));
        var glb = FileSamples.Glb();
        Assert.Equal(GlbProblem.NotGlb, GlbInspector.Inspect(glb[..(glb.Length - 3)]));
    }
}

/// <summary>Kho tệp trên đĩa: chỉ khóa hợp lệ, không thoát khỏi thư mục gốc, ghi nguyên tử.</summary>
public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dsvhvn_kiem_thu_kho_" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private LocalFileStorage Create(string baseUrl = "") =>
        new(Options.Create(new StorageOptions { PublicBaseUrl = baseUrl, Local = new LocalStorageOptions { RootPath = _root } }),
            new TestHostEnvironment());

    [Fact]
    public async Task A_file_can_be_saved_found_and_removed()
    {
        var storage = Create();
        var key = StoredFileRules.ImageKey(Guid.NewGuid(), "png");

        Assert.False(await storage.ExistsAsync(key, default));
        await storage.SaveAsync(key, new byte[] { 1, 2, 3 }, default);
        Assert.True(await storage.ExistsAsync(key, default));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "images"), "*.tmp"));

        await storage.DeleteAsync(key, default);
        Assert.False(await storage.ExistsAsync(key, default));
    }

    [Theory]
    [InlineData("../outside.jpg")]
    [InlineData("images/../../outside.jpg")]
    [InlineData("images/anything.jpg")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("/etc/passwd")]
    public async Task Keys_that_could_leave_the_storage_folder_are_rejected(string key)
    {
        var storage = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => storage.SaveAsync(key, new byte[] { 1 }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.ExistsAsync(key, default));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.DeleteAsync(key, default));
        Assert.Throws<ArgumentException>(() => storage.PublicUrl(key));
    }

    [Fact]
    public void The_public_url_is_relative_unless_a_base_address_is_configured()
    {
        var key = StoredFileRules.ImageKey(Guid.Parse("0123456789abcdef0123456789abcdef"), "jpg");
        Assert.Equal("/files/images/0123456789abcdef0123456789abcdef.jpg", Create().PublicUrl(key));
        Assert.Equal("https://api.example.vn/files/images/0123456789abcdef0123456789abcdef.jpg",
            Create(" https://api.example.vn/ ").PublicUrl(key));
    }
}

public sealed class StorageOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(StorageOptions options) => new StorageOptionsValidator().Validate(null, options);

    [Fact]
    public void The_default_is_local_storage_and_valid() => Assert.True(Validate(new StorageOptions()).Succeeded);

    [Fact]
    public void The_cloud_provider_is_reserved_but_not_available_yet()
    {
        var result = Validate(new StorageOptions { Provider = StorageProvider.Cloud });
        Assert.True(result.Failed);
        Assert.Contains("Cloud chưa có bản cài đặt", result.FailureMessage);
    }

    [Fact]
    public void The_root_folder_and_the_public_base_address_are_checked()
    {
        Assert.True(Validate(new StorageOptions { Local = new LocalStorageOptions { RootPath = " " } }).Failed);
        Assert.True(Validate(new StorageOptions { PublicBaseUrl = "api.example.vn" }).Failed);
        Assert.True(Validate(new StorageOptions { PublicBaseUrl = "ftp://api.example.vn" }).Failed);
        Assert.True(Validate(new StorageOptions { PublicBaseUrl = "https://api.example.vn" }).Succeeded);
    }
}
