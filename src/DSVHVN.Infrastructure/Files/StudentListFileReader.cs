using System.Text;
using ClosedXML.Excel;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Infrastructure.Files;

/// <summary>
/// Đọc tệp danh sách học sinh <c>.xlsx</c> (bảng tính đầu tiên) hoặc <c>.csv</c> UTF-8 và dựng tệp mẫu. Chỉ lấy cột đầu tiên là họ tên.
/// Tệp mẫu <c>.csv</c> có dấu BOM UTF-8 để Excel mở đúng tiếng Việt và đọc lại đúng khi nhập.
/// Chỉ đọc tối đa vài chục dòng đầu: quá số dòng cho phép thì tầng ứng dụng từ chối cả tệp.
/// </summary>
public sealed class StudentListFileReader : IStudentListFile
{
    private const string HeaderText = "Họ và tên";
    private const string NoteText = "Nhập mỗi học sinh một dòng ở cột A, bên dưới ô \"Họ và tên\". Tối đa 60 dòng mỗi lần.";
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string CsvContentType = "text/csv; charset=utf-8";

    // Đọc dư một ít so với 60 dòng để còn phát hiện tệp quá dài (tầng ứng dụng từ chối) mà không đọc cả tệp.
    private const int MaxRowsRead = ClassRules.MaxImportRows + 20;

    public IReadOnlyList<ListRow> Read(Stream content, string fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".xlsx" => ReadXlsx(content),
            ".csv" => ReadCsv(content),
            _ => throw Invalid("chỉ nhận tệp .xlsx hoặc .csv UTF-8"),
        };
    }

    public DownloadFile BuildTemplate(StudentListFormat format) => format switch
    {
        StudentListFormat.Csv => new DownloadFile(
            [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(HeaderText + "\r\n")], CsvContentType, "mau-danh-sach-hoc-sinh.csv"),
        _ => BuildXlsxTemplate(),
    };

    private static DownloadFile BuildXlsxTemplate()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Danh sách");
        sheet.Cell(1, 1).Value = HeaderText;
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 3).Value = NoteText;
        sheet.Cell(1, 3).Style.Font.Italic = true;
        sheet.Column(1).Width = 40;
        sheet.Column(3).Width = 80;
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return new DownloadFile(stream.ToArray(), XlsxContentType, "mau-danh-sach-hoc-sinh.xlsx");
    }

    private static List<ListRow> ReadXlsx(Stream content)
    {
        var bytes = ReadAll(content);
        if (bytes.Length < 4 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K')
            throw Invalid("không phải tệp .xlsx hợp lệ, hãy dùng tệp mẫu");
        try
        {
            using var workbook = new XLWorkbook(new MemoryStream(bytes));
            var sheet = workbook.Worksheets.FirstOrDefault();
            var rows = new List<ListRow>();
            if (sheet is null) return rows;

            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
            var column = sheet.FirstColumnUsed()?.ColumnNumber() ?? 1;
            for (var r = 1; r <= Math.Min(lastRow, MaxRowsRead); r++)
                rows.Add(new ListRow(r, sheet.Cell(r, column).GetFormattedString()));
            return rows;
        }
        catch (Exception ex) when (ex is not AppException)
        {
            throw Invalid("không đọc được, hãy dùng tệp mẫu");
        }
    }

    private static List<ListRow> ReadCsv(Stream content)
    {
        var bytes = ReadAll(content);
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw Invalid("tệp .csv phải lưu bằng mã hóa UTF-8 (trong Excel chọn \"CSV UTF-8\")");
        }
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        var rows = new List<ListRow>();
        foreach (var value in FirstColumn(text))
        {
            rows.Add(new ListRow(rows.Count + 1, value));
            if (rows.Count >= MaxRowsRead) break;
        }
        return rows;
    }

    // Bóc cột đầu của mỗi bản ghi CSV: hỗ trợ ô đặt trong dấu nháy kép (có thể chứa dấu phẩy, xuống dòng) và dấu ngăn , hoặc ;
    // (Excel tiếng Việt thường xuất dấu chấm phẩy). Các cột sau cột đầu bị bỏ qua.
    private static IEnumerable<string> FirstColumn(string text)
    {
        var field = new StringBuilder();
        var inQuotes = false;
        var firstDone = false;
        var pending = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            pending = true;
            if (inQuotes)
            {
                if (c != '"') { if (!firstDone) field.Append(c); }
                else if (i + 1 < text.Length && text[i + 1] == '"') { if (!firstDone) field.Append('"'); i++; }
                else inQuotes = false;
                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0 && !firstDone:
                    inQuotes = true;
                    break;
                case ',' or ';':
                    firstDone = true;
                    break;
                case '\r' or '\n':
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    yield return field.ToString();
                    field.Clear();
                    firstDone = false;
                    pending = false;
                    break;
                default:
                    if (!firstDone) field.Append(c);
                    break;
            }
        }
        if (pending) yield return field.ToString();
    }

    private static byte[] ReadAll(Stream content)
    {
        // Giới hạn dung lượng đã kiểm ở tầng ứng dụng; đọc thêm một lớp bảo vệ khi luồng không báo trước độ dài.
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = content.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > ClassRules.MaxImportFileBytes)
                throw Invalid("dung lượng tối đa 1 MB");
        }
        return buffer.ToArray();
    }

    private static AppException Invalid(string rule) =>
        AppException.Validation("file", Messages.Invalid("Tệp danh sách", rule));
}
