using DSVHVN.Api.Common;
using Microsoft.AspNetCore.Http;

namespace DSVHVN.Tests.Unit;

/// <summary>Đường dẫn ghi vào log lỗi không chứa mã bài học mà học sinh gõ.</summary>
public sealed class RequestLoggingTests
{
    [Theory]
    [InlineData("/api/v1/student/lessons/BH7F3K2Q", "/api/v1/student/lessons/{code}")]
    [InlineData("/api/v1/student/lessons/bh%207f3k-2q", "/api/v1/student/lessons/{code}")]
    [InlineData("/api/v1/student/class", "/api/v1/student/class")]
    [InlineData("/api/v1/teacher/lessons/12", "/api/v1/teacher/lessons/12")]
    [InlineData("/", "/")]
    public void The_lesson_code_is_masked_in_logged_paths(string path, string expected) =>
        Assert.Equal(expected, ExceptionEnvelopeMiddleware.LoggablePath(new PathString(path)));

    [Fact]
    public void An_empty_path_logs_as_empty_text() => Assert.Equal(string.Empty, ExceptionEnvelopeMiddleware.LoggablePath(default));
}
