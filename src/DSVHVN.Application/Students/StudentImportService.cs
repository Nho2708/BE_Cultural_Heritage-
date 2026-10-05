using DSVHVN.Application.Classes;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Students;

/// <summary>Đọc danh sách học sinh từ văn bản dán: mỗi dòng một họ tên.</summary>
public static class StudentListInput
{
    private static readonly HashSet<string> HeaderNames =
        ["ho va ten", "ho ten", "ten hoc sinh", "ho va ten hoc sinh", "full name", "name"];

    public static IReadOnlyList<ListRow> FromText(string? text) =>
        (text ?? string.Empty).Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Select((line, i) => new ListRow(i + 1, line)).ToList();

    /// <summary>
    /// Bỏ dòng tiêu đề (dòng đầu là "Họ và tên" hoặc tương tự) và cắt các dòng trống cuối; số dòng của các dòng còn lại giữ nguyên
    /// so với nguồn. Không còn dòng nào hoặc quá 60 dòng thì ném lỗi 400.
    /// </summary>
    public static IReadOnlyList<ListRow> Prepare(IReadOnlyList<ListRow> raw)
    {
        var rows = raw.ToList();
        if (rows.Count > 0 && HeaderNames.Contains(SearchText.Fold(rows[0].Value))) rows.RemoveAt(0);
        while (rows.Count > 0 && string.IsNullOrWhiteSpace(rows[^1].Value)) rows.RemoveAt(rows.Count - 1);
        EnsureCount(rows.Count);
        return rows;
    }

    public static void EnsureCount(int count)
    {
        if (count == 0)
            throw AppException.Validation("rows", Messages.Required("ít nhất một họ tên học sinh"));
        if (count > ClassRules.MaxImportRows)
            throw AppException.Validation("rows",
                Messages.Invalid("Danh sách học sinh", $"tối đa {ClassRules.MaxImportRows} dòng mỗi lần"));
    }
}

/// <summary>
/// Nhập danh sách học sinh từ tệp theo mẫu (<c>.xlsx</c> hoặc <c>.csv</c> UTF-8), từ văn bản dán, tối đa 60 dòng mỗi lần.
/// Bước xem trước báo từng dòng lỗi (trống, quá 255 ký tự, vượt sĩ số 60) kèm lý do và cảnh báo trùng họ tên; xác nhận thì các dòng
/// hợp lệ được lưu trong một giao dịch, dòng lỗi bị bỏ qua. Họ tên trùng vẫn được thêm, phân biệt bằng mã học sinh.
/// </summary>
public sealed class StudentImportService(
    IAppDbContext db,
    IStudentListFile files,
    StudentAccountFactory factory,
    StudentService students)
{
    public const string BlankNameError = "Họ tên đang trống.";
    public const string TooLongNameError = "Họ tên dài quá 255 ký tự.";
    public const string OverCapacityError = "Vượt sĩ số tối đa 60 học sinh đang hoạt động của lớp.";
    public const string DuplicateNameWarning = "Trùng họ tên với một học sinh khác trong lớp; mã học sinh sẽ phân biệt hai em.";

    public DownloadFile Template(StudentListFormat format) => files.BuildTemplate(format);

    public async Task<ImportPreviewDto> PreviewFileAsync(Actor actor, long classId, Stream content, string? fileName, CancellationToken ct)
    {
        if (content.CanSeek && content.Length > ClassRules.MaxImportFileBytes)
            throw AppException.Validation("file", Messages.Invalid("Tệp danh sách", "dung lượng tối đa 1 MB"));
        var rows = StudentListInput.Prepare(files.Read(content, fileName ?? string.Empty));
        return await PreviewAsync(actor, classId, rows, ct);
    }

    public Task<ImportPreviewDto> PreviewTextAsync(Actor actor, long classId, string? text, CancellationToken ct) =>
        PreviewAsync(actor, classId, StudentListInput.Prepare(StudentListInput.FromText(text)), ct);

    private async Task<ImportPreviewDto> PreviewAsync(Actor actor, long classId, IReadOnlyList<ListRow> rows, CancellationToken ct)
    {
        await db.FindClassAsync(actor, classId, "nhập danh sách học sinh", tracking: false, ct);
        var remaining = ClassRules.MaxActiveStudents - await students.CountActiveAsync(classId, ct);
        var classified = Classify(rows, await ExistingNameKeysAsync(classId, ct), remaining);
        return new ImportPreviewDto(
            classified.Select(r => new ImportPreviewRowDto(r.RowNumber, r.Name, r.Errors.Count == 0, r.Errors, r.Warnings)).ToList(),
            classified.Count, classified.Count(r => r.Errors.Count == 0), classified.Count(r => r.Errors.Count > 0),
            classified.Count(r => r.Warnings.Count > 0), Math.Max(0, remaining), ClassRules.MaxActiveStudents);
    }

    /// <summary>
    /// Lưu các dòng hợp lệ trong một giao dịch có khóa lớp (kiểm lại sĩ số lúc lưu), bỏ qua dòng lỗi và báo đủ từng dòng.
    /// Không có dòng hợp lệ nào thì không lưu gì và trả 422.
    /// </summary>
    public async Task<ImportResultDto> ImportAsync(Actor actor, long classId, ImportStudentsRequest request, CancellationToken ct)
    {
        var requested = request.Rows ?? [];
        StudentListInput.EnsureCount(requested.Count);
        var rows = requested.Select((r, i) => new ListRow(r.RowNumber ?? i + 1, r.FullName)).ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockClassAsync(classId, ct);
        var schoolClass = await db.FindClassAsync(actor, classId, "nhập danh sách học sinh", tracking: true, ct);
        var remaining = ClassRules.MaxActiveStudents - await students.CountActiveAsync(classId, ct);
        var classified = Classify(rows, await ExistingNameKeysAsync(classId, ct), remaining);

        var valid = classified.Where(r => r.Errors.Count == 0).ToList();
        if (valid.Count == 0)
            throw AppException.BusinessRule(Messages.Blocked("nhập danh sách học sinh", "Không có dòng hợp lệ nào để lưu."));

        var created = await factory.CreateAsync(schoolClass, valid.Select(r => r.Name).ToList(), actor, ct);
        await tx.CommitAsync(ct);

        var skipped = classified.Where(r => r.Errors.Count > 0)
            .Select(r => new ImportSkippedRowDto(r.RowNumber, r.Name, r.Errors)).ToList();
        var message = Messages.StudentsAdded(created.Count, schoolClass.Name ?? string.Empty,
            skipped.Select(s => $"dòng {s.RowNumber} ({s.Errors[0].TrimEnd('.')})").ToList());
        return new ImportResultDto(created.Count, skipped.Count, skipped, message.Text, students.ToCreatedDto(schoolClass, created));
    }

    private async Task<HashSet<string>> ExistingNameKeysAsync(long classId, CancellationToken ct) =>
        (await db.Students.AsNoTracking().Where(s => s.ClassId == classId && s.DeletedAt == null).Select(s => s.FullName).ToListAsync(ct))
        .Select(StudentNames.Key).ToHashSet();

    private sealed record ClassifiedRow(int RowNumber, string Name, List<string> Errors, List<string> Warnings);

    private static List<ClassifiedRow> Classify(IReadOnlyList<ListRow> rows, HashSet<string> existingKeys, int remainingCapacity)
    {
        var seen = new HashSet<string>(existingKeys);
        var result = new List<ClassifiedRow>(rows.Count);
        foreach (var row in rows)
        {
            var name = StudentNames.Clean(row.Value);
            var errors = new List<string>();
            var warnings = new List<string>();
            if (name.Length == 0) errors.Add(BlankNameError);
            else if (name.Length > ClassRules.StudentNameMaxLength) errors.Add(TooLongNameError);
            else
            {
                if (remainingCapacity > 0) remainingCapacity--;
                else errors.Add(OverCapacityError);
                if (!seen.Add(StudentNames.Key(name))) warnings.Add(DuplicateNameWarning);
            }
            result.Add(new ClassifiedRow(row.RowNumber, name, errors, warnings));
        }
        return result;
    }
}
