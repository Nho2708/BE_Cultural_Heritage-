using DSVHVN.Api.Common;
using DSVHVN.Api.Security;
using DSVHVN.Application;
using DSVHVN.Application.Common;
using DSVHVN.Application.Files;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure;
using DSVHVN.Infrastructure.Files;
using DSVHVN.Infrastructure.Persistence;
using DSVHVN.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
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
builder.Services.AddPublicRateLimit();

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
        Title = "Vietnam Heritage API",
        Version = "v1",
        Description = "Nền tảng giáo dục di sản văn hóa cho trường học — nền tảng: đăng nhập, đặt mật khẩu qua email, hồ sơ, " +
            "quản lý trường (ADMIN), quản lý giáo viên (ORG_ADMIN), nhật ký thao tác, kho di sản (ADMIN), khám phá di sản công khai (/api/v1/public, không cần đăng nhập), lớp học và học sinh của giáo viên (/api/v1/teacher), bài học cho lớp theo cấp với AI soạn nội dung và mã bài học (/api/v1/teacher/lessons), đăng nhập và học của học sinh, mở bài học bằng mã, làm bài kiểm tra theo cấp và xem kết quả (/api/v1/student), ngân hàng câu hỏi 8 dạng và bài kiểm tra của bài học với phát hành, đóng, nhân bản (/api/v1/teacher/quizzes), thống kê theo lớp và gợi ý, kiểm duyệt ngân hàng câu hỏi và tổng quan hệ thống của quản trị, tải ảnh và mô hình 3D lên kho tệp, phục vụ tệp tại /files, thuyết minh tạo sẵn (MP3 giọng tự host) của tóm tắt di sản: danh sách còn thiếu và tải lên (/api/v1/admin/narrations, /api/v1/admin/heritages/{id}/narrations/{language}), trả kèm tóm tắt công khai, quản lý gói dịch vụ (/api/v1/admin/plans), mua, gia hạn, nâng cấp, hủy gói và hóa đơn của trường qua VNPay sandbox (/api/v1/org/billing, /api/v1/org/invoices), IPN và trang trả về của VNPay (/api/v1/payments/vnpay), khách tham quan không tài khoản: thiết bị ẩn danh, lịch sử, gợi ý cho bạn và gần bạn (/api/v1/public/guest), quảng cáo Google có thưởng mở tóm tắt và nghe thuyết minh: cấu hình, trạng thái mở khóa và báo thưởng của web (/api/v1/public/ads), xác minh phía máy chủ của AdMob (/api/v1/ads/admob/ssv), thống kê cho quản trị hệ thống (/api/v1/admin/ads/stats), biểu mẫu liên hệ của trường gửi email tới quản trị hệ thống (/api/v1/public/school-inquiries). CSDL v3 đủ 27 bảng cộng các bảng của v4 thêm dần theo từng luồng.",
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
app.UseMiddleware<PreviewNoIndexMiddleware>();
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
    app.UseSwaggerUI(o => o.DocumentTitle = "Vietnam Heritage API");
}

app.UseCors("web");
app.UseStoredFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();

app.Run();

/// <summary>
/// Phục vụ tệp tải lên (ảnh, mô hình 3D, thuyết minh MP3) từ kho tệp trên đĩa qua đường dẫn <c>/files/...</c> khi dùng nhà cung cấp Local. Có kiểm soát: chỉ các đuôi
/// jpg, png, webp, glb, mp3; không liệt kê thư mục; tệp lạ không được phục vụ; tên tệp là mã ngẫu nhiên không đổi nội dung nên cho trình duyệt giữ lâu.
/// </summary>
internal static class StoredFilesExtensions
{
    public static IApplicationBuilder UseStoredFiles(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IFileStorage>() is not LocalFileStorage local) return app;

        var types = new FileExtensionContentTypeProvider(new Dictionary<string, string>
        {
            [".jpg"] = "image/jpeg",
            [".png"] = "image/png",
            [".webp"] = "image/webp",
            [".glb"] = "model/gltf-binary",
            [".mp3"] = NarrationRules.AudioContentType,
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(local.RootPath),
            RequestPath = StoredFileRules.PublicPathPrefix.TrimEnd('/'),
            ContentTypeProvider = types,
            ServeUnknownFileTypes = false,
            OnPrepareResponse = context =>
            {
                var headers = context.Context.Response.Headers;
                headers.CacheControl = "public, max-age=31536000, immutable";
                headers["Cross-Origin-Resource-Policy"] = "cross-origin";
            },
        });
        return app;
    }
}

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
