using System.Text;
using ClosedXML.Excel;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Files;

namespace DSVHVN.Tests.Unit;

/// <summary>Đọc tệp danh sách học sinh .csv (UTF-8, dấu ngăn , hoặc ;, ô trong nháy) và .xlsx, dựng tệp mẫu, từ chối tệp sai.</summary>
public sealed class StudentListFileReaderTests
{
    private readonly StudentListFileReader _reader = new();

    private IReadOnlyList<ListRow> ReadCsv(string text, bool bom = false) =>
        _reader.Read(new MemoryStream([.. (bom ? Encoding.UTF8.GetPreamble() : []), .. Encoding.UTF8.GetBytes(text)]), "ds.csv");

    private static byte[] Workbook(Action<IXLWorksheet> fill)
    {
        using var wb = new XLWorkbook();
        fill(wb.Worksheets.Add("Sheet1"));
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Csv_reads_the_first_column_of_every_line_keeping_blank_lines_and_numbering()
    {
        var rows = ReadCsv("Họ và tên\r\nNguyễn Văn An\r\n\r\nTrần Thị Bé\n", bom: true);
        Assert.Equal([(1, "Họ và tên"), (2, "Nguyễn Văn An"), (3, ""), (4, "Trần Thị Bé")], rows.Select(r => (r.RowNumber, r.Value)));
    }

    [Theory]
    [InlineData("An;x;y\nBình;z", new[] { "An", "Bình" })]
    [InlineData("An,x,y\nBình,z", new[] { "An", "Bình" })]
    [InlineData("\"Lê, Văn Cường\";x\n\"Có \"\"nháy\"\" kép\"", new[] { "Lê, Văn Cường", "Có \"nháy\" kép" })]
    [InlineData("\"Hai\ndòng\"\nBa", new[] { "Hai\ndòng", "Ba" })]
    [InlineData("An\rBình\r", new[] { "An", "Bình" })]
    public void Csv_handles_delimiters_quotes_and_line_endings(string text, string[] expected) =>
        Assert.Equal(expected, ReadCsv(text).Select(r => r.Value));

    [Fact]
    public void Csv_that_is_not_utf8_is_rejected_with_a_hint()
    {
        var latin1 = Encoding.Latin1.GetBytes("Trén Vàn\r\n");
        var ex = Assert.Throws<AppException>(() => _reader.Read(new MemoryStream(latin1), "ds.csv"));
        Assert.Equal((400, "INVALID"), (ex.StatusCode, ex.AppMessage.Code));
        Assert.Contains("UTF-8", ex.AppMessage.Text);
    }

    [Fact]
    public void Xlsx_reads_the_first_used_column_of_the_first_sheet_including_numbers_and_blank_rows()
    {
        var bytes = Workbook(ws =>
        {
            ws.Cell(1, 2).Value = "Họ và tên";
            ws.Cell(2, 2).Value = "Bùi Thị Lan";
            ws.Cell(4, 2).Value = "Ngô Văn Mạnh";
            ws.Cell(2, 3).Value = "bỏ qua";
        });
        var rows = _reader.Read(new MemoryStream(bytes), "DS.XLSX");
        Assert.Equal([(1, "Họ và tên"), (2, "Bùi Thị Lan"), (3, ""), (4, "Ngô Văn Mạnh")], rows.Select(r => (r.RowNumber, r.Value)));
    }

    [Fact]
    public void Xlsx_that_is_empty_gives_no_rows() =>
        Assert.Empty(_reader.Read(new MemoryStream(Workbook(_ => { })), "ds.xlsx"));

    [Fact]
    public void Xlsx_reading_stops_a_little_after_sixty_rows_so_the_caller_can_refuse_the_file()
    {
        var bytes = Workbook(ws => { for (var r = 1; r <= 500; r++) ws.Cell(r, 1).Value = "HS " + r; });
        Assert.Equal(ClassRules.MaxImportRows + 20, _reader.Read(new MemoryStream(bytes), "ds.xlsx").Count);
    }

    [Theory]
    [InlineData("ds.txt")]
    [InlineData("ds.xls")]
    [InlineData("ds")]
    [InlineData("")]
    public void Other_file_types_are_rejected(string fileName)
    {
        var ex = Assert.Throws<AppException>(() => _reader.Read(new MemoryStream([1, 2, 3]), fileName));
        Assert.Equal("Tệp danh sách không hợp lệ: chỉ nhận tệp .xlsx hoặc .csv UTF-8.", ex.AppMessage.Text);
    }

    [Fact]
    public void Xlsx_that_is_not_a_workbook_is_rejected()
    {
        Assert.Throws<AppException>(() => _reader.Read(new MemoryStream(Encoding.UTF8.GetBytes("Nguyễn Văn An")), "ds.xlsx"));
        Assert.Throws<AppException>(() => _reader.Read(new MemoryStream([(byte)'P', (byte)'K', 1, 2, 3, 4]), "ds.xlsx"));
    }

    [Fact]
    public void Files_over_one_megabyte_are_rejected_even_without_a_known_length()
    {
        var big = new byte[ClassRules.MaxImportFileBytes + 1];
        Array.Fill(big, (byte)'A');
        var ex = Assert.Throws<AppException>(() => _reader.Read(new NonSeekableStream(big), "ds.csv"));
        Assert.Contains("1 MB", ex.AppMessage.Text);
    }

    [Fact]
    public void Templates_have_one_header_column_no_sample_students_and_read_back()
    {
        var csv = _reader.BuildTemplate(StudentListFormat.Csv);
        Assert.Equal(("mau-danh-sach-hoc-sinh.csv", "text/csv; charset=utf-8"), (csv.FileName, csv.ContentType));
        Assert.Equal(Encoding.UTF8.GetPreamble(), csv.Content.Take(3).ToArray());   // BOM để Excel mở đúng tiếng Việt
        Assert.Equal([(1, "Họ và tên")], _reader.Read(new MemoryStream(csv.Content), "mau.csv").Select(r => (r.RowNumber, r.Value)));

        var xlsx = _reader.BuildTemplate(StudentListFormat.Xlsx);
        Assert.Equal("mau-danh-sach-hoc-sinh.xlsx", xlsx.FileName);
        Assert.Contains("spreadsheetml.sheet", xlsx.ContentType);
        Assert.Equal([(1, "Họ và tên")], _reader.Read(new MemoryStream(xlsx.Content), "mau.xlsx").Select(r => (r.RowNumber, r.Value)));
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
