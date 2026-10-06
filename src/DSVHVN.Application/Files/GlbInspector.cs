using System.Buffers.Binary;
using System.Text.Json;

namespace DSVHVN.Application.Files;

public enum GlbProblem
{
    None,

    /// <summary>Không phải tệp glTF nhị phân (thiếu dấu hiệu "glTF"), hoặc cấu trúc hỏng.</summary>
    NotGlb,

    /// <summary>Không phải glTF 2.0.</summary>
    WrongVersion,

    /// <summary>Tham chiếu tới tài nguyên ngoài tệp (đường dẫn ra ngoài), trái quy tắc "mọi dữ liệu nằm trong tệp".</summary>
    ExternalResource,
}

/// <summary>
/// Kiểm một tệp <c>.glb</c> (glTF 2.0 đóng gói một tệp) bằng nội dung: đầu tệp đúng, độ dài khớp, khối JSON đọc được,
/// phiên bản 2.0, và không tham chiếu tài nguyên ngoài (kết cấu và dữ liệu phải nằm trong tệp).
/// </summary>
public static class GlbInspector
{
    private const uint Magic = 0x46546C67;       // "glTF"
    private const uint JsonChunk = 0x4E4F534A;   // "JSON"
    private const int HeaderLength = 12;
    private const int ChunkHeaderLength = 8;

    public static GlbProblem Inspect(ReadOnlySpan<byte> content)
    {
        if (content.Length < HeaderLength + ChunkHeaderLength) return GlbProblem.NotGlb;
        if (BinaryPrimitives.ReadUInt32LittleEndian(content) != Magic) return GlbProblem.NotGlb;
        if (BinaryPrimitives.ReadUInt32LittleEndian(content[4..]) != 2) return GlbProblem.WrongVersion;
        if (BinaryPrimitives.ReadUInt32LittleEndian(content[8..]) != (uint)content.Length) return GlbProblem.NotGlb;

        var jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(content[HeaderLength..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(content[(HeaderLength + 4)..]) != JsonChunk) return GlbProblem.NotGlb;
        if (jsonLength == 0 || jsonLength > content.Length - HeaderLength - ChunkHeaderLength) return GlbProblem.NotGlb;

        var json = content.Slice(HeaderLength + ChunkHeaderLength, (int)jsonLength).ToArray();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return GlbProblem.NotGlb;
            if (!root.TryGetProperty("asset", out var asset) || asset.ValueKind != JsonValueKind.Object) return GlbProblem.NotGlb;
            if (!asset.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String
                || version.GetString() != "2.0") return GlbProblem.WrongVersion;

            foreach (var list in new[] { "buffers", "images" })
            {
                if (!root.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("uri", out var uri)) continue;
                    // Dữ liệu nhúng dạng data: nằm trong tệp nên được chấp nhận; mọi đường dẫn khác là tham chiếu ra ngoài.
                    if (uri.ValueKind != JsonValueKind.String
                        || uri.GetString() is not { } text
                        || !text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        return GlbProblem.ExternalResource;
                }
            }
            return GlbProblem.None;
        }
        catch (JsonException)
        {
            return GlbProblem.NotGlb;
        }
    }
}
