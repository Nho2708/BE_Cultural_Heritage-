namespace DSVHVN.Application.Speech;

/// <summary>
/// Dịch vụ chuyển văn bản thành giọng nói (Text-to-Speech) qua FPT.AI.
/// Trả về đường dẫn tệp âm thanh (URL) để phát trên trình duyệt hoặc thiết bị; trả null khi chưa cấu hình khóa FPT.AI
/// hoặc FPT.AI không phục vụ được, lúc đó client dùng giọng đọc có sẵn của trình duyệt.
/// </summary>
public interface ITtsService
{
    Task<string?> SynthesizeAsync(string text, CancellationToken ct = default);
}
