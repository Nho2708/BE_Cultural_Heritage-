using DSVHVN.Domain.Rules;

namespace DSVHVN.Application.Students;

/// <summary>
/// Nguồn mã học sinh và mật khẩu tạm. Bản mặc định dùng bộ sinh số ngẫu nhiên an toàn (<see cref="StudentCredentials"/>);
/// tách thành giao diện để kiểm thử ép được trường hợp trùng mã.
/// </summary>
public interface IStudentCredentialGenerator
{
    string NewStudentCode();

    string NewTemporaryPassword();

    /// <summary>Mật khẩu đơn giản 6 chữ số cho lớp cấp 1.</summary>
    string NewSimplePassword();
}

public sealed class RandomStudentCredentialGenerator : IStudentCredentialGenerator
{
    public string NewStudentCode() => StudentCredentials.NewStudentCode();

    public string NewTemporaryPassword() => StudentCredentials.NewTemporaryPassword();

    public string NewSimplePassword() => StudentCredentials.NewSimplePassword();
}
