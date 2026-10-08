using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Tài liệu Swagger phải sinh được cho toàn bộ controller: một action thiếu thuộc tính HTTP hoặc hai lớp trùng tên ở hai namespace
/// làm trang /swagger báo "Failed to load API definition" mà không có test nào khác bắt được.
/// </summary>
public sealed class SwaggerDocumentTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public void Swagger_document_generates_for_every_endpoint()
    {
        var provider = factory.Services.GetRequiredService<ISwaggerProvider>();

        var document = provider.GetSwagger("v1");

        Assert.NotEmpty(document.Paths);
    }
}
