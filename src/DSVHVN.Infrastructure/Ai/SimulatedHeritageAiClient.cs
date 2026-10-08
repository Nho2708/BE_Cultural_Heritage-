using System.Text;
using System.Text.RegularExpressions;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Infrastructure.Ai;

/// <summary>
/// BẢN GIẢ LẬP của Dịch vụ AI tóm tắt và dịch di sản (chọn bằng <c>Ai:Mode = Simulated</c>, là mặc định). Dịch vụ AI thật chưa có; bản này thực hiện đúng giao diện
/// <see cref="IHeritageAiClient"/> của hợp đồng để toàn bộ luồng (ghi nhật ký, kiểm kết quả, chờ duyệt, duyệt, bản dịch máy, hiển thị công khai) chạy được.
/// Không gọi mạng, không gọi nhà cung cấp mô hình nào, không dùng khóa API nào.
/// <para>
/// Tóm tắt: ghép từ chính chữ đã có của di sản (mô tả ngắn, nội dung, tên các giai đoạn), không thêm dữ kiện mới; bản ngắn tối đa khoảng 100 từ, bản chi tiết tối đa khoảng 350 từ,
/// cắt ở ranh giới câu. Dịch: bản giả lập không dịch thật mà gắn tiền tố mã ngôn ngữ đích vào từng phần chữ (<c>[en] Hoàng thành</c>), giữ nguyên mã giai đoạn, sự kiện, ảnh.
/// Tất định: cùng đầu vào cho cùng đầu ra. Chất lượng chữ chỉ ở mức mẫu để thử luồng; bản thật dùng mô hình ngôn ngữ và quản trị hệ thống vẫn duyệt bản tiếng Việt, tiếng Anh.
/// </para>
/// </summary>
public sealed partial class SimulatedHeritageAiClient : IHeritageAiClient
{
    public const string Name = "simulated-heritage-translator";

    private const int ShortWords = 100;
    private const int DetailedWords = 350;

    public string ModelName => Name;

    public Task<AiSummarizeResult> SummarizeHeritageAsync(AiSummarizeRequest request, string correlationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var source = request.Source;
        // Nội dung di sản viết bằng Markdown (tiêu đề "## ...", gạch đầu dòng, chữ đậm); tóm tắt là văn bản thuần nên bỏ ký hiệu trước khi ghép.
        var shortDescription = PlainText.FromMarkdown(source.ShortDescription);
        var content = PlainText.FromMarkdown(source.Content);
        var body = string.Join(' ', new[] { shortDescription, content }.Where(t => t.Length > 0));
        if (string.IsNullOrWhiteSpace(source.Name) || body.Length == 0)
            throw new AiServiceException("Di sản chưa có nội dung tiếng Việt để tóm tắt.", retryable: false);

        var shortText = TakeWords($"{source.Name.Trim()}. {body}", ShortWords);
        var paragraphs = new List<string>();
        if (shortDescription.Length > 0) paragraphs.Add($"{source.Name.Trim()}. {shortDescription}");
        else paragraphs.Add(source.Name.Trim() + ".");
        if (content.Length > 0) paragraphs.Add(content);
        if (source.Timelines.Count > 0)
            paragraphs.Add("Các giai đoạn lịch sử: " + string.Join("; ", source.Timelines.Take(6).Select(t => t.Name.Trim())) + ".");
        var detailed = TakeWords(string.Join("\n\n", paragraphs), DetailedWords);

        var usage = new AiUsage(WordCount(body) , WordCount(shortText) + WordCount(detailed));
        return Task.FromResult(new AiSummarizeResult(shortText, detailed, Name, usage));
    }

    public Task<AiTranslateResult> TranslateHeritageAsync(AiTranslateRequest request, string correlationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.TargetLanguage) || Languages.TryParse(request.TargetLanguage) is null)
            throw new AiServiceException("Ngôn ngữ đích không hợp lệ.", retryable: false);
        var tag = request.TargetLanguage.Trim();
        string? Tr(string? text) => string.IsNullOrWhiteSpace(text) ? null : TagText(tag, text);

        var c = request.Content;
        var content = new AiHeritageText(
            Tr(c.Name) ?? $"[{tag}]",
            Tr(c.ShortDescription),
            Tr(c.Content),
            c.Timelines.Select(t => t with { Name = Tr(t.Name) ?? $"[{tag}]", Description = Tr(t.Description) }).ToList(),
            c.Events.Select(e => e with { Name = Tr(e.Name) ?? $"[{tag}]", Description = Tr(e.Description) }).ToList(),
            c.Media.Select(m => m with { Caption = Tr(m.Caption) ?? $"[{tag}]" }).ToList());
        var summary = request.Summary is null
            ? null
            : new AiSummaryText(Tr(request.Summary.ShortText)!, Tr(request.Summary.DetailedText)!);

        var input = WordCount(c.Content) + WordCount(c.ShortDescription) + WordCount(request.Summary?.DetailedText);
        return Task.FromResult(new AiTranslateResult(content, summary, Name, new AiUsage(input, input)));
    }

    /// <summary>
    /// Gắn mã ngôn ngữ vào đầu chữ. Chữ Markdown giữ ký hiệu đầu dòng ở đầu (<c>## [en] Giới thiệu</c>, không phải <c>[en] ## Giới thiệu</c>) để tiêu đề vẫn là tiêu đề
    /// khi hiển thị; bản tóm tắt là văn bản thuần nên chỉ có tiền tố.
    /// </summary>
    public static string TagText(string tag, string text)
    {
        var trimmed = text.Trim();
        var marker = MarkdownLineStart().Match(trimmed);
        return $"{trimmed[..marker.Length]}[{tag}] {trimmed[marker.Length..]}";
    }

    [GeneratedRegex(@"^(?:#{1,6}\s+|[-*+]\s+)?")]
    private static partial Regex MarkdownLineStart();

    /// <summary>Lấy tối đa <paramref name="maxWords"/> từ đầu, ưu tiên dừng ở cuối câu nếu đã đủ khoảng ba phần tư số từ; cắt giữa câu thì thêm dấu chấm.</summary>
    internal static string TakeWords(string text, int maxWords)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= maxWords) return string.Join(' ', words).Trim();

        var taken = words.Take(maxWords).ToList();
        var floor = maxWords * 3 / 4;
        for (var i = taken.Count - 1; i >= floor; i--)
        {
            if (taken[i].EndsWith('.') || taken[i].EndsWith('!') || taken[i].EndsWith('?'))
                return string.Join(' ', taken.Take(i + 1));
        }
        var cut = new StringBuilder(string.Join(' ', taken).TrimEnd(',', ';', ':', '-', ' '));
        cut.Append('.');
        return cut.ToString();
    }

    private static int WordCount(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
