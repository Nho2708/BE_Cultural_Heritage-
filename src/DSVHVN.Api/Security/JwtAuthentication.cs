using System.Globalization;
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
/// Lỗi trả 401 theo khuôn phản hồi chung: thông điệp "không đăng nhập được" kèm lý do khi tài khoản bị chặn,
/// thông điệp "không có quyền" cho các trường hợp còn lại.
/// </summary>
public static class JwtAuthentication
{
    private const string BlockedReasonKey = "dsvhvn.auth.blocked";

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
                        var message = ctx.HttpContext.Items[BlockedReasonKey] is string reason
                            ? Messages.CannotSignIn(reason)
                            : Messages.Forbidden;
                        await ApiJson.WriteFailAsync(ctx.HttpContext, StatusCodes.Status401Unauthorized, message);
                    },
                    OnForbidden = ctx =>
                        ApiJson.WriteFailAsync(ctx.HttpContext, StatusCodes.Status403Forbidden, Messages.Forbidden),
                };
            });
        return services;
    }

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
            })
            .SingleOrDefaultAsync(ctx.HttpContext.RequestAborted);

        if (account is null)
        {
            ctx.Fail("Tài khoản không còn tồn tại.");
            return;
        }

        if (AccountAccess.BlockedReason(account.Status, account.OrganizationDeactivated) is { } reason)
        {
            ctx.HttpContext.Items[BlockedReasonKey] = reason;
            ctx.Fail("Tài khoản không còn được dùng hệ thống.");
            return;
        }

        var orgClaim = principal!.FindFirst(JwtClaimNames.Organization)?.Value;
        var sameRole = string.Equals(Roles.CodeOf(account.RoleId).ToString(), principal.FindFirst(JwtClaimNames.Role)?.Value, StringComparison.Ordinal);
        var sameOrg = string.Equals(account.OrganizationId?.ToString(CultureInfo.InvariantCulture), orgClaim, StringComparison.Ordinal);
        var sameStamp = string.Equals(SecurityStamps.From(account.PasswordHash),
            principal.FindFirst(JwtClaimNames.SecurityStamp)?.Value, StringComparison.Ordinal);
        if (!sameRole || !sameOrg || !sameStamp)
            ctx.Fail("Token không còn khớp tài khoản (vai trò, trường hoặc mật khẩu đã đổi), cần đăng nhập lại.");
    }
}
