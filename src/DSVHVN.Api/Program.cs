using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application;
using DSVHVN.Application.Common;
using DSVHVN.Infrastructure;
using DSVHVN.Infrastructure.Persistence;
using DSVHVN.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services
    .AddControllers(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)
    .AddJsonOptions(o => ApiJson.Configure(o.JsonSerializerOptions))
    .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = ModelStateEnvelope.Create);

builder.Services.AddJwtAuthentication();
builder.Services.AddAuthorization(Policies.Register);
builder.Services.AddHealthChecks();

// CORS chỉ mở cho origin của ứng dụng React, không dùng "*".
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddPolicy("web", p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders(CorrelationIdMiddleware.HeaderName)));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "DSVHVN API",
        Version = "v1",
        Description = "Nền tảng giáo dục di sản văn hóa cho trường học — nền tảng: đăng nhập, đặt mật khẩu qua email, hồ sơ, " +
            "quản lý trường (ADMIN), quản lý giáo viên (ORG_ADMIN), nhật ký thao tác. CSDL v3 đủ 27 bảng.",
    });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Name = "Authorization",
        Description = "Dán access token nhận từ POST /api/v1/auth/login.",
    });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
    });
    var xml = Path.Combine(AppContext.BaseDirectory, "DSVHVN.Api.xml");
    if (File.Exists(xml)) o.IncludeXmlComments(xml);
});

var app = builder.Build();

// Thiếu khóa ký JWT ngoài Development/Testing thì dừng ngay lúc khởi động, không đợi yêu cầu đầu tiên.
_ = app.Services.GetRequiredService<JwtSigningKeyProvider>();
// Email:Mode = Smtp mà thiếu cấu hình bắt buộc (Host, FromAddress, Password…) cũng dừng ngay, thông báo liệt kê mục còn thiếu.
_ = app.Services.GetRequiredService<IEmailSender>();
await app.Services.InitializeDatabaseAsync();

// "dotnet run --project src/DSVHVN.Api -- --chi-khoi-tao-csdl": chỉ tạo/cập nhật CSDL và nạp dữ liệu khởi tạo rồi thoát.
if (args.Contains("--chi-khoi-tao-csdl")) return;

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<ExceptionEnvelopeMiddleware>();

if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
{
    // HTTPS bắt buộc, HSTS.
    app.UseHsts();
    app.UseHttpsRedirection();
}

if (app.Environment.IsDevelopment())
{
    // Swagger chỉ bật ở Development.
    app.UseSwagger();
    app.UseSwaggerUI(o => o.DocumentTitle = "DSVHVN API");
}

app.UseCors("web");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();

app.Run();

/// <summary>Đổi lỗi binding (JSON sai cú pháp, sai kiểu) thành khuôn phản hồi chung với thông điệp dữ liệu không hợp lệ.</summary>
internal static class ModelStateEnvelope
{
    public static IActionResult Create(ActionContext context)
    {
        var errors = new Dictionary<string, string>();
        foreach (var (key, entry) in context.ModelState)
        {
            if (entry.Errors.Count == 0) continue;
            var field = FieldName(key);
            errors.TryAdd(field, Messages.Invalid(field == "body" ? "Dữ liệu gửi lên" : field, "sai định dạng").Text);
        }

        var message = errors.Count > 0
            ? new AppMessage(MessageCodes.Invalid, errors.Values.First())
            : Messages.Invalid("Dữ liệu gửi lên", "sai định dạng");
        return new BadRequestObjectResult(ApiResponse.Fail(message, context.HttpContext.TraceIdentifier, errors));
    }

    private static string FieldName(string key)
    {
        var k = key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key;
        if (k.Length == 0 || k is "$" or "request") return "body";
        return char.ToLowerInvariant(k[0]) + k[1..];
    }
}

public partial class Program;
