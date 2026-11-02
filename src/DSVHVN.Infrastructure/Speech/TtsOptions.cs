using DSVHVN.Domain.Rules;

namespace DSVHVN.Infrastructure.Speech;

/// <summary>Nguồn giọng đọc của máy chủ (<c>Tts:Provider</c>).</summary>
public enum TtsProvider
{
    /// <summary>FPT.AI (dịch vụ ngoài, không còn là mặc định). Chưa có khóa thì trả liên kết null.</summary>
    FptAi,

    /// <summary>
    /// Mặc định: giọng tự host, gọi Dịch vụ AI nội bộ (VieNeu-TTS), lưu MP3 vào kho tệp và nhớ đệm trong bảng <c>speech_clips</c>.
    /// Máy không có Dịch vụ AI (máy dev, máy demo chưa cài) thì trả liên kết null để giao diện dùng giọng của thiết bị.
    /// </summary>
    SelfHosted,

    /// <summary>Tắt giọng của máy chủ: luôn trả liên kết null, giao diện dùng giọng của thiết bị.</summary>
    None,
}

/// <summary>
/// Cấu hình giọng đọc (mục <c>Tts</c>). <see cref="Provider"/> chọn dịch vụ lúc tạo (không đọc ngay lúc đăng ký nên cấu hình ghi đè về sau vẫn có hiệu lực).
/// Mặc định <c>SelfHosted</c> cho mọi máy (Google Cloud, máy demo, máy dev); không máy nào dùng FPT.AI trừ khi cấu hình <c>FptAi</c> rõ ràng.
/// </summary>
public sealed class TtsOptions
{
    public const string Section = "Tts";

    public TtsProvider Provider { get; set; } = TtsProvider.SelfHosted;

    /// <summary>Giọng của chế độ <c>SelfHosted</c>; mặc định "Mai Anh" của VieNeu-TTS.</summary>
    public string Voice { get; set; } = SpeechRules.DefaultVoice;

    /// <summary>Thời gian tối đa một yêu cầu nghe ngay chờ giọng máy chủ (giây, được lẻ); hết hạn thì trả null nhưng việc sinh vẫn chạy tiếp và được nhớ đệm.</summary>
    public double WaitSeconds { get; set; } = SpeechRules.DefaultWaitSeconds;

    /// <summary>Thời gian chờ tối đa của một lần gọi Dịch vụ AI để sinh một đoạn (giây). Máy yếu sinh chậm gấp vài lần thời lượng nên đặt rộng hơn thời gian chờ của người nghe.</summary>
    public double AiTimeoutSeconds { get; set; } = 120;

    /// <summary>Nghỉ bao lâu (giây) sau một đoạn tạo trước bị lỗi (vd Dịch vụ AI đang nạp mô hình) trước khi thử đoạn kế hoặc thử lại đoạn đó.</summary>
    public double PrefetchRetrySeconds { get; set; } = 5;

    /// <summary>Số lần tối đa thử một đoạn tạo trước (kể cả lần đầu); hết số lần thì bỏ đoạn đó, người nghe vẫn yêu cầu được ngay.</summary>
    public int PrefetchMaxAttempts { get; set; } = 3;

    public TimeSpan Wait => TimeSpan.FromSeconds(Math.Max(0.01, WaitSeconds));

    public TimeSpan PrefetchRetry => TimeSpan.FromSeconds(Math.Max(0, PrefetchRetrySeconds));

    public TimeSpan AiTimeout => TimeSpan.FromSeconds(Math.Max(0.05, AiTimeoutSeconds));
}
