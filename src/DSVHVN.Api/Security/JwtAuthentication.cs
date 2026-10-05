using System.Globalization;
using System.Security.Claims;
using DSVHVN.Application.StudentAuth;
using Microsoft.AspNetCore.Authorization;
using DSVHVN.Api.Common;
using DSVHVN.Application.Auth;
using DSVHVN.Application.Common;
using DSVHVN.Domain.Identity;
using DSVHVN.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DSVHVN.Api.Security;

/// <summary>
/// Chỉ có access token. Ngoài chữ ký và hạn dùng, mỗi yêu cầu còn đối chiếu CSDL:
/// tài khoản còn tồn tại, chưa xóa mềm, ACTIVE, trường chưa ngừng, vai trò và trường trong token khớp CSDL, dấu bảo mật
/// khớp mật khẩu hiện tại. Nhờ vậy khóa tài khoản, ngừng trường, đổi mật khẩu có hiệu lực ngay dù token chưa hết hạn.
/// Với học sinh còn kiểm thêm học sinh (chưa vô hiệu, chưa xóa) và lớp (chưa xóa) nên vô hiệu, xóa học sinh, xóa lớp có hiệu lực ngay;
/// học sinh đang bị buộc đổi mật khẩu chỉ qua được các endpoint không đòi <see cref="Policies.StudentReady"/>.
/// Lỗi trả 401 theo khuôn phản hồi chung: thông điệp "không đăng nhập được" kèm lý do khi tài khoản bị chặn,
/// thông điệp "không có quyền" cho các trường hợp còn lại.
/// </summary>
public static class JwtAuthentication
{
    private const string BlockedMessageKey = "dsvhvn.auth.blocked";
    private const string PasswordChangeRequiredKey = "dsvhvn.auth.password-change-required";

    /// <summary>Claim do máy chủ thêm vào phiên (không nằm trong token) khi học sinh đang bị buộc đổi mật khẩu.</summary>
    public const string PasswordChangeRequiredClaim = "dsvhvn_password_change_required";

    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>, JwtSigningKeyProvider>((o, jwt, keys) =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Value.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Value.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = keys.Key,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = JwtClaimNames.Name,
                    RoleClaimType = JwtClaimNames.Role,
                };
                o.Events = new JwtBearerEvents
                {
                    OnTokenValidated = CheckAccountAsync,
                    OnChallenge = async ctx =>
                    {
                        ctx.HandleResponse();
                        ctx.Response.Headers.WWWAuthenticate = "Bearer";
                        var message = ctx.HttpContext.Items[BlockedMessageKey] is AppMessage blocked
                            ? blocked
                            : Messages.Forbidden;
                        await ApiJson.WriteFailAsync(ctx.HttpContext, StatusCodes.Status401Unauthorized, message);
                    },
                    OnForbidden = ctx => ApiJson.WriteFailAsync(ctx.HttpContext, StatusCodes.Status403Forbidden,
                        MustChangePasswordFirst(ctx.HttpContext) ? Messages.PasswordChangeRequired : Messages.Forbidden),
                };
            });
        return services;
    }

    // Chỉ khi học sinh đang bị buộc đổi mật khẩu gọi một endpoint của khu học (đòi StudentReady) mới nhắc đổi mật khẩu;
    // gọi endpoint của vai trò khác vẫn là "không có quyền".
    private static bool MustChangePasswordFirst(HttpContext context) =>
        context.Items.ContainsKey(PasswordChangeRequiredKey)
        && context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == Policies.StudentReady) == true;

    private static async Task CheckAccountAsync(TokenValidatedContext ctx)
    {
        var principal = ctx.Principal;
        if (!long.TryParse(principal?.FindFirst(JwtClaimNames.Subject)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId))
        {
            ctx.Fail("Token thiếu claim sub.");
            return;
        }

        var db = ctx.HttpContext.RequestServices.GetRequiredService<IAppDbContext>();
        var account = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.DeletedAt == null)
            .Select(u => new
            {
                u.Status,
                u.RoleId,
                u.OrganizationId,
                OrganizationDeactivated = u.Organization != null && u.Organization.DeletedAt != null,
                u.PasswordHash,
                u.MustChangePassword,
                Student = db.Students.Where(s => s.UserId == u.Id)
                    .Select(s => new { s.IsActive, Deleted = s.DeletedAt != null, ClassDeleted = s.Class!.DeletedAt != null })
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ctx.HttpContext.RequestAborted);

        if (account is null)
        {
            ctx.Fail("Tài khoản không còn tồn tại.");
            return;
        }

        var isStudent = account.RoleId == Roles.StudentId;
        var blocked = isStudent
            ? StudentAccess.BlockedReason(account.Status, account.OrganizationDeactivated, account.Student?.IsActive,
                account.Student?.Deleted ?? true, account.Student?.ClassDeleted ?? true) is { } studentReason
                ? Messages.StudentCannotSignIn(studentReason)
                : null
            : AccountAccess.BlockedReason(account.Status, account.OrganizationDeactivated) is { } reason
                ? Messages.CannotSignIn(reason)
                : null;
        if (blocked is not null)
        {
            ctx.HttpContext.Items[BlockedMessageKey] = blocked;
            ctx.Fail("Tài khoản không còn được dùng hệ thống.");
            return;
        }

        var orgClaim = principal!.FindFirst(JwtClaimNames.Organization)?.Value;
        var sameRole = string.Equals(Roles.CodeOf(account.RoleId).ToString(), principal.FindFirst(JwtClaimNames.Role)?.Value, StringComparison.Ordinal);
        var sameOrg = string.Equals(account.OrganizationId?.ToString(CultureInfo.InvariantCulture), orgClaim, StringComparison.Ordinal);
        var sameStamp = string.Equals(SecurityStamps.From(account.PasswordHash),
            principal.FindFirst(JwtClaimNames.SecurityStamp)?.Value, StringComparison.Ordinal);
        if (!sameRole || !sameOrg || !sameStamp)
        {
            ctx.Fail("Token không còn khớp tài khoản (vai trò, trường hoặc mật khẩu đã đổi), cần đăng nhập lại.");
            return;
        }

        if (isStudent && account.MustChangePassword)
        {
            principal.AddIdentity(new ClaimsIdentity([new Claim(PasswordChangeRequiredClaim, "true")]));
            ctx.HttpContext.Items[PasswordChangeRequiredKey] = true;
        }
    }
}
