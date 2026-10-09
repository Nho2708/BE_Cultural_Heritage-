namespace DSVHVN.Application.Common;

/// <summary>
/// Dịch vụ định vị tọa độ: giải mã địa chỉ thành tọa độ GPS (WGS 84) chuẩn.
/// </summary>
public interface IGeocodingService
{
    /// <summary>
    /// Giải mã chuỗi địa chỉ thành cặp tọa độ (Vĩ độ - Latitude, Kinh độ - Longitude).
    /// </summary>
    Task<(decimal? Latitude, decimal? Longitude, bool Success)> ResolveAddressAsync(
        string address,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Kết nối tới dịch vụ AI (Python FastAPI) để xử lý tóm tắt đa cấp độ, sinh câu hỏi và hỗ trợ du khách.
/// </summary>
public interface IAiServiceClient
{
    /// <summary>
    /// Gửi bài viết di sản sang AI Service để tóm tắt và xử lý theo cấp độ học &amp; ngôn ngữ.
    /// </summary>
    Task<string> ProcessHeritageAsync(
        string text,
        int level,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}
