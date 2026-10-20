namespace DSVHVN.Application.Narrations;

// Dữ liệu vào, ra của thuyết minh tạo sẵn. Mã ngôn ngữ trả ra ở phần quản trị là mã ngắn lưu trong CSDL (vi, en, ko, zh, ja) để công cụ tạo giọng
// gửi lại nguyên văn; đường dẫn vào vẫn nhận mã như các API bản dịch (zh-Hans cũng được).

/// <summary>Một câu của thuyết minh: câu gốc để hiển thị và mốc bắt đầu, kết thúc (mili giây) trong tệp.</summary>
public sealed record NarrationSentenceDto(string Text, int StartMs, int EndMs);

/// <summary>Một bản tóm tắt đang hiện công khai mà chưa có thuyết minh khớp băm: công cụ tạo giọng đọc <c>ShortText</c> rồi tải lên kèm đúng <c>TextHash</c>.</summary>
public sealed record MissingNarrationItemDto(long HeritageId, string Slug, string Name, string Language, string ShortText, string TextHash);

/// <summary>Danh sách còn thiếu của một ngôn ngữ: <c>Total</c> là tổng số còn thiếu, <c>Items</c> là tối đa <c>limit</c> mục đầu (theo mã di sản).</summary>
public sealed record MissingNarrationsDto(IReadOnlyList<MissingNarrationItemDto> Items, int Total);

/// <summary>
/// Các trường chữ của biểu mẫu tải thuyết minh lên (tệp MP3 đi riêng): băm của <c>shortText</c> lúc tạo giọng, mã giọng, thời lượng và danh sách câu
/// dưới dạng chuỗi JSON <c>[{ "text", "startMs", "endMs" }]</c>.
/// </summary>
public sealed record UploadNarrationRequest(string? TextHash, string? Voice, int? DurationMs, string? Sentences);

/// <summary>Thuyết minh vừa lưu (phản hồi của API tải lên).</summary>
public sealed record HeritageNarrationDto(long HeritageId, string Language, string AudioUrl, int DurationMs, string Voice, string TextHash, int SentenceCount);

/// <summary>
/// Thuyết minh tạo sẵn trả kèm tóm tắt công khai: tệp MP3, thời lượng, giọng và các câu để tô sáng theo thời điểm phát. Chỉ có khi tồn tại thuyết minh
/// cho đúng ngôn ngữ của bản tóm tắt được trả và băm vẫn khớp chữ; không có thì giao diện đọc bằng giọng thiết bị như trước.
/// </summary>
public sealed record PublicNarrationDto(string AudioUrl, int DurationMs, string Voice, IReadOnlyList<NarrationSentenceDto> Sentences);
