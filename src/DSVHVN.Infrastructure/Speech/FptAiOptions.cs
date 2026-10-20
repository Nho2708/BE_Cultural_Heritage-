namespace DSVHVN.Infrastructure.Speech;

/// <summary>
/// Cấu hình dịch vụ FPT.AI Text-to-Speech (TTS).
/// </summary>
public sealed class FptAiOptions
{
    public const string Section = "FptAi";

    public string ApiKey { get; set; } = string.Empty;
    public string Voice { get; set; } = "banmai";
    public string Speed { get; set; } = "0";
    public string Endpoint { get; set; } = "https://api.fpt.ai/hmi/tts/v5";
}
