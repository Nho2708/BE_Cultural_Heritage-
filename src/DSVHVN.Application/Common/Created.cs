namespace DSVHVN.Application.Common;

/// <summary>Kết quả tạo tài khoản (kèm trường nếu có): dữ liệu trả về và email đã nhận liên kết đặt mật khẩu.</summary>
public sealed record Created<T>(T Data, string NotifiedEmail);
