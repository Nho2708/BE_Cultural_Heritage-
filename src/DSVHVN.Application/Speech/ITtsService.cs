namespace DSVHVN.Application.Speech;

/// <summary>
/// Dịch vụ chuyển văn bản thành giọng nói (Text-to-Speech). Nguồn giọng chọn bằng <c>Tts:Provider</c>: giọng tự host có bộ nhớ đệm bền (VieNeu-TTS qua Dịch vụ AI, mặc định), FPT.AI hoặc tắt.
/// Trả về đường dẫn tệp âm thanh (URL) để phát trên trình duyệt hoặc thiết bị; trả null khi không có giọng của máy chủ (chưa cấu hình, tắt, lỗi, chưa sinh kịp),
/// lúc đó client dùng giọng đọc có sẵn của thiết bị.
/// </summary>
public interface ITtsService
{
    Task<string?> SynthesizeAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Đường dẫn tệp âm thanh của chữ nếu đã có sẵn trong bộ nhớ đệm của giọng hiện tại; không tạo gì mới. null khi chưa có, khi nguồn giọng không có bộ nhớ đệm
    /// dùng chung, hoặc khi tra lỗi.
    /// </summary>
    Task<string?> FindCachedAsync(string text, CancellationToken ct = default);
}
