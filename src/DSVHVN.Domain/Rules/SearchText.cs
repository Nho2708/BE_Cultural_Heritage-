using System.Globalization;
using System.Text;

namespace DSVHVN.Domain.Rules;

/// <summary>
/// Chuẩn hóa chữ để tìm kiếm không phân biệt hoa thường và dấu. Collation <c>Vietnamese_CI_AI</c> của SQL Server chỉ bỏ dấu thanh,
/// còn coi ă, â, ê, ô, ơ, ư, đ là chữ riêng ("thang" không khớp "thăng"), nên không thể dựa vào collation. Thay vào đó cả nội dung
/// lẫn từ khóa cùng được "gấp" về dạng ASCII thường: bỏ mọi dấu (đ thành d), hạ chữ thường, mọi ký tự không phải chữ hoặc số
/// thành một dấu cách. Cột <c>heritages.search_text</c> và <c>heritages.search_name</c> lưu sẵn dạng này để tìm bằng LIKE.
/// Cả hai cột bắt đầu bằng một dấu cách, và mỗi trường trong <c>search_text</c> cũng đứng sau một dấu cách, nên mẫu <c>"% từ%"</c>
/// khớp đầu mỗi từ ("thang lo" tìm ra "Thăng Long", còn "hang" thì không khớp "thang").
/// </summary>
public static class SearchText
{
    /// <summary>Từ khóa tìm kiếm công khai dài 2-100 ký tự.</summary>
    public const int KeywordMinLength = 2;
    public const int KeywordMaxLength = 100;

    /// <summary>Từ khóa tách thành nhiều từ, mỗi từ phải có trong di sản; quá số này thì các từ thừa bị bỏ.</summary>
    public const int MaxKeywordTokens = 8;

    /// <summary>Dấu ngăn giữa các trường trong <c>search_text</c>; không bao giờ có trong từ khóa đã gấp.</summary>
    public const char FieldSeparator = '|';

    /// <summary>Dạng gấp của chuỗi: chữ thường không dấu, các đoạn ký tự khác chữ và số thu về một dấu cách, bỏ dấu cách hai đầu.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var c = ch switch { 'đ' or 'Đ' => 'd', _ => char.ToLowerInvariant(ch) };
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(c);
            }
            else
            {
                pendingSpace = true;
            }
        }
        return sb.ToString();
    }

    /// <summary>Các từ của từ khóa sau khi gấp, không lặp, tối đa <see cref="MaxKeywordTokens"/> từ.</summary>
    public static IReadOnlyList<string> Tokens(string? keyword) =>
        Fold(keyword).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().Take(MaxKeywordTokens).ToList();

    /// <summary>Mẫu LIKE khớp đầu một từ: dữ liệu luôn có dấu cách đứng trước mỗi từ đầu trường nên không cần thêm nhánh cho từ đầu tiên.</summary>
    public static string WordPrefixPattern(string foldedText) => "% " + foldedText + "%";

    /// <summary>Nội dung cột <c>search_name</c>: tên đã gấp, có một dấu cách đứng đầu (rỗng nếu tên rỗng).</summary>
    public static string ComposeName(string? name)
    {
        var folded = Fold(name);
        return folded.Length == 0 ? string.Empty : " " + folded;
    }

    /// <summary>
    /// Nội dung cột <c>search_text</c>: tên, mô tả ngắn, địa chỉ và nội dung chi tiết, mỗi phần gấp riêng rồi nối bằng dấu ngăn,
    /// bắt đầu bằng một dấu cách.
    /// </summary>
    public static string Compose(string? name, string? shortDescription, string? address, string? content) =>
        " " + string.Join($" {FieldSeparator} ", Fold(name), Fold(shortDescription), Fold(address), Fold(content));
}
