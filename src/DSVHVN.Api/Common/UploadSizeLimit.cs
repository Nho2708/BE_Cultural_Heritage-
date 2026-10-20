using DSVHVN.Application.Common;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DSVHVN.Api.Common;

/// <summary>
/// Từ chối tệp tải lên vượt dung lượng cho phép bằng cùng một câu báo rõ ràng ("Tệp ảnh không hợp lệ: tối đa 5 MB.") dù tệp lớn đến đâu.
/// Giới hạn thân yêu cầu (<see cref="RequestSizeLimitAttribute"/>, <see cref="RequestFormLimitsAttribute"/>) chỉ rộng hơn tệp đúng
/// <see cref="FormHeadroom"/> để chừa chỗ cho các trường chữ; vượt giới hạn đó thì máy chủ ngắt ngay lúc MVC đọc biểu mẫu, trước khi dịch vụ
/// kịp kiểm dung lượng tệp, và lỗi đọc biểu mẫu chỉ hiện thành "Dữ liệu gửi lên không hợp lệ: sai định dạng" (tệp còn đang gửi thì trình
/// duyệt thấy kết nối bị đặt lại). Bộ lọc này chạy trước khi ràng buộc tham số: nhìn Content-Length để từ chối sớm bằng 413 kèm câu
/// rõ ràng, và đọc sẵn biểu mẫu để đổi lỗi "quá giới hạn" của máy chủ thành cùng câu đó. Dung lượng tệp vẫn do dịch vụ kiểm lại.
/// </summary>
/// <param name="fileLabel">Tên tệp trong câu báo, vd "Tệp ảnh".</param>
/// <param name="maxFileBytes">Dung lượng tối đa của tệp.</param>
/// <param name="field">Tên trường của tệp trong biểu mẫu để báo lỗi đúng ô (mặc định <c>file</c>).</param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UploadSizeLimitAttribute(string fileLabel, long maxFileBytes, string field = "file") : Attribute, IAsyncResourceFilter
{
    /// <summary>Phần chừa cho các trường chữ và phần đầu của biểu mẫu multipart, cộng vào dung lượng tệp để ra giới hạn thân yêu cầu.</summary>
    public const long FormHeadroom = 512 * 1024;

    /// <summary>
    /// Thân yêu cầu quá giới hạn nhưng không quá mức này thì được đọc hết rồi mới trả lời, để trình duyệt chắc chắn nhận được câu báo
    /// thay vì "kết nối bị đặt lại" khi tệp còn đang gửi dở. Lớn hơn nữa thì trả lời ngay, không đọc.
    /// </summary>
    private const long DrainLimit = 64L * 1024 * 1024;

    public long RequestLimit => maxFileBytes + FormHeadroom;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        var request = http.Request;
        if (!request.HasFormContentType)
        {
            // Không phải biểu mẫu: [Consumes] trả 415 như thường.
            await next();
            return;
        }

        if (request.ContentLength is { } length && length > RequestLimit)
        {
            await DrainAsync(http, length);
            throw TooLarge();
        }

        try
        {
            // Đọc sẵn (MVC dùng lại biểu mẫu đã đọc, không đọc hai lần) để bắt lỗi quá giới hạn khi không có Content-Length.
            await request.ReadFormAsync(http.RequestAborted);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw TooLarge();
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("body length limit", StringComparison.OrdinalIgnoreCase))
        {
            throw TooLarge();
        }

        await next();
    }

    private AppException TooLarge()
    {
        var message = Messages.Invalid(fileLabel, $"tối đa {maxFileBytes / (1024 * 1024)} MB");
        return new AppException(StatusCodes.Status413PayloadTooLarge, message, new Dictionary<string, string> { [field] = message.Text });
    }

    private static async Task DrainAsync(HttpContext http, long length)
    {
        if (length > DrainLimit) return;
        var bodySize = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false }) bodySize.MaxRequestBodySize = length;
        try
        {
            await http.Request.Body.CopyToAsync(Stream.Null, http.RequestAborted);
        }
        catch (IOException)
        {
            // Trình duyệt ngắt giữa chừng: vẫn trả lời như thường.
        }
    }
}
