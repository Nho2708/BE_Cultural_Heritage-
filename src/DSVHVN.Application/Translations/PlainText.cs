using System.Text;
using System.Text.RegularExpressions;

namespace DSVHVN.Application.Translations;

/// <summary>
/// Đưa văn bản có cú pháp Markdown về văn bản thuần. Nội dung di sản được soạn bằng Markdown (tiêu đề <c>## Giới thiệu</c>, gạch đầu dòng, chữ đậm), còn bản tóm tắt
/// được hiển thị như chữ thường và được đọc thành tiếng bằng giọng của thiết bị nên không được còn ký hiệu nào của Markdown.
/// <list type="bullet">
/// <item>Tiêu đề thành một câu riêng (thêm dấu chấm nếu chưa có dấu kết câu) để ranh giới câu rõ khi tách câu đọc.</item>
/// <item>Gạch đầu dòng, đánh số, trích dẫn, dòng kẻ ngang, khung mã, chữ đậm, nghiêng, gạch ngang chữ, mã dòng, liên kết, ảnh, thẻ HTML đều bỏ ký hiệu và giữ chữ.</item>
/// <item>Giữ cách ngắt đoạn (dòng trống); khoảng trắng thừa trong dòng gộp thành một dấu cách.</item>
/// </list>
/// Cũng xử lý được văn bản đã bị gộp thành một dòng (ví dụ <c>... 2010). ## Giới thiệu Khu ...</c>): ký hiệu tiêu đề giữa dòng bị bỏ.
/// </summary>
public static partial class PlainText
{
    private static readonly char[] SentenceEnds = ['.', '!', '?', '…', '。', '！', '？', ':', ';'];

    /// <summary>Văn bản thuần của <paramref name="text"/>; null hoặc rỗng trả chuỗi rỗng.</summary>
    public static string FromMarkdown(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var result = new StringBuilder();
        var inFence = false;
        var pendingBreak = false;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (FenceLine().IsMatch(line))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence)
            {
                if (line.Length == 0 || RuleLine().IsMatch(line) || TableSeparatorLine().IsMatch(line))
                {
                    pendingBreak = result.Length > 0;
                    continue;
                }
                line = LineToPlain(line);
            }
            if (line.Length == 0) continue;

            if (result.Length > 0) result.Append(pendingBreak ? "\n\n" : "\n");
            pendingBreak = false;
            result.Append(line);
        }
        return result.ToString();
    }

    private static string LineToPlain(string line)
    {
        var heading = HeadingLine().Match(line);
        if (heading.Success)
        {
            var title = InlineToPlain(heading.Groups[1].Value);
            return title.Length == 0 ? string.Empty : EndSentence(title);
        }

        var isItem = false;
        var quote = Quote().Match(line);
        while (quote.Success)
        {
            line = line[quote.Length..];
            quote = Quote().Match(line);
        }
        var item = ListMarker().Match(line);
        if (item.Success)
        {
            line = line[item.Length..];
            isItem = true;
        }
        if (line.StartsWith('|')) line = TableLine(line);

        line = InlineToPlain(line);
        if (line.Length == 0) return string.Empty;
        return isItem ? EndSentence(line) : line;
    }

    private static string TableLine(string line)
    {
        var cells = line.Trim('|').Split('|').Select(c => c.Trim()).Where(c => c.Length > 0);
        return string.Join(", ", cells);
    }

    private static string InlineToPlain(string text)
    {
        text = Image().Replace(text, "$1");
        text = Link().Replace(text, "$1");
        text = AutoLink().Replace(text, string.Empty);
        text = HtmlTag().Replace(text, string.Empty);
        text = HeadingMarkerInLine().Replace(text, " ");
        // Chữ đậm trước chữ nghiêng, mỗi loại lặp tới khi hết (lồng nhau).
        text = Bold().Replace(text, "$1$2");
        text = Strike().Replace(text, "$1");
        text = ItalicStar().Replace(text, "$1");
        text = ItalicUnderscore().Replace(text, "$1");
        text = InlineCode().Replace(text, "$1");
        text = Escaped().Replace(text, "$1");
        text = StrayEmphasis().Replace(text, string.Empty);
        return Spaces().Replace(text, " ").Trim();
    }

    private static string EndSentence(string text) => text.IndexOfAny(SentenceEnds, text.Length - 1) >= 0 ? text : text + ".";

    [GeneratedRegex(@"^(```|~~~)")]
    private static partial Regex FenceLine();

    [GeneratedRegex(@"^([-*_]\s*){3,}$")]
    private static partial Regex RuleLine();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableSeparatorLine();

    [GeneratedRegex(@"^#{1,6}\s+(.*?)(?:\s+#+)?$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^>\s?")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^(?:[-*+•]|\d{1,2}[.)])\s+")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"<https?://[^>\s]+>")]
    private static partial Regex AutoLink();

    [GeneratedRegex(@"</?[A-Za-z][^>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"(?:(?<=\s)|^)#{1,6}(?=\s)")]
    private static partial Regex HeadingMarkerInLine();

    [GeneratedRegex(@"\*\*(.+?)\*\*|__(.+?)__")]
    private static partial Regex Bold();

    [GeneratedRegex(@"~~(.+?)~~")]
    private static partial Regex Strike();

    [GeneratedRegex(@"(?<![\p{L}\p{N}*])\*(?=\S)([^*\n]+?)(?<=\S)\*(?![\p{L}\p{N}*])")]
    private static partial Regex ItalicStar();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])_(?=\S)([^_\n]+?)(?<=\S)_(?![\p{L}\p{N}_])")]
    private static partial Regex ItalicUnderscore();

    [GeneratedRegex(@"`([^`]*)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!>~|])")]
    private static partial Regex Escaped();

    [GeneratedRegex(@"\*\*|__|~~")]
    private static partial Regex StrayEmphasis();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();
}
