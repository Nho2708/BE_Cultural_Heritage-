namespace DSVHVN.Application.Speech;

/// <summary>
/// Hàng đợi tạo trước giọng đọc chạy nền: nhận các đoạn chữ rồi sinh âm thanh vào bộ nhớ đệm khi máy rảnh, sau mọi yêu cầu nghe ngay.
/// Chỉ có tác dụng khi dùng giọng tự host (<c>Tts:Provider = SelfHosted</c>); các chế độ khác dùng bản không làm gì.
/// </summary>
public interface ISpeechPrefetcher
{
    /// <summary>True khi giọng tự host đang bật và hàng đợi có chạy.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Đưa các đoạn chữ vào hàng đợi (không chờ). Đoạn rỗng hoặc dài quá giới hạn bị bỏ, đoạn đã chờ sẵn trong hàng đợi không xếp lần nữa; đoạn đã có trong bộ nhớ đệm
    /// được bỏ qua khi tới lượt. Trả số đoạn thật sự được nhận vào hàng đợi.
    /// </summary>
    int Enqueue(IEnumerable<string?> texts);
}

/// <summary>Bản không làm gì cho <c>Tts:Provider</c> là <c>FptAi</c> hoặc <c>None</c>, và cho kiểm thử không dùng giọng tự host.</summary>
public sealed class NullSpeechPrefetcher : ISpeechPrefetcher
{
    public bool Enabled => false;

    public int Enqueue(IEnumerable<string?> texts) => 0;
}
