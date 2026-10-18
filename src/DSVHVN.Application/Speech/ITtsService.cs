namespace DSVHVN.Application.Speech;

/// <summary>
/// Dịch vụ chuyển văn bản thành giọng nói (Text-to-Speech) qua cloud service (FPT.AI / Google Cloud TTS fallback).
/// Trả về đường dẫn tệp âm thanh (URL) để phát trên trình duyệt hoặc thiết bị.
/// </summary>
public interface ITtsService
{
    Task<string?> SynthesizeAsync(string text, CancellationToken ct = default);
    Task<byte[]?> GetAudioBytesAsync(string hash, CancellationToken ct = default);
}
