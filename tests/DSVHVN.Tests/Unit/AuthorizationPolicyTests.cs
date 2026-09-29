using System.Security.Claims;
using DSVHVN.Api.Security;
using DSVHVN.Domain.Enums;
using DSVHVN.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace DSVHVN.Tests.Unit;

/// <summary>Ma trận 3 vai trò × 4 policy, dựng đúng cấu hình <see cref="Policies.Register"/> mà Api dùng.</summary>
public sealed class AuthorizationPolicyTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly IAuthorizationService _authz;

    public AuthorizationPolicyTests()
    {
        _services = new ServiceCollection().AddLogging().AddAuthorization(Policies.Register).BuildServiceProvider();
        _authz = _services.GetRequiredService<IAuthorizationService>();
    }

    public void Dispose() => _services.Dispose();

    private static ClaimsPrincipal Signed(RoleCode role) => new(new ClaimsIdentity(
        [new Claim(JwtClaimNames.Subject, "1"), new Claim(JwtClaimNames.Role, role.ToString())],
        authenticationType: "Bearer", nameType: JwtClaimNames.Name, roleType: JwtClaimNames.Role));

    private static readonly ClaimsPrincipal Guest = new(new ClaimsIdentity());

    public static TheoryData<string, RoleCode, bool> Matrix => new()
    {
        { Policies.Authenticated, RoleCode.ADMIN, true },
        { Policies.Authenticated, RoleCode.ORG_ADMIN, true },
        { Policies.Authenticated, RoleCode.TEACHER, true },
        { Policies.Admin, RoleCode.ADMIN, true },
        { Policies.Admin, RoleCode.ORG_ADMIN, false },
        { Policies.Admin, RoleCode.TEACHER, false },
        { Policies.OrgAdmin, RoleCode.ADMIN, false },
        { Policies.OrgAdmin, RoleCode.ORG_ADMIN, true },
        { Policies.OrgAdmin, RoleCode.TEACHER, false },
        { Policies.Teacher, RoleCode.ADMIN, false },
        { Policies.Teacher, RoleCode.ORG_ADMIN, false },
        { Policies.Teacher, RoleCode.TEACHER, true },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Role_matrix(string policy, RoleCode role, bool allowed)
    {
        var result = await _authz.AuthorizeAsync(Signed(role), resource: null, policy);
        Assert.Equal(allowed, result.Succeeded);
    }

    [Theory]
    [InlineData(Policies.Authenticated)]
    [InlineData(Policies.Admin)]
    [InlineData(Policies.OrgAdmin)]
    [InlineData(Policies.Teacher)]
    public async Task Guest_is_denied_every_policy(string policy)
    {
        var result = await _authz.AuthorizeAsync(Guest, resource: null, policy);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Endpoints_require_login_unless_marked_anonymous()
    {
        var options = new AuthorizationOptions();
        Policies.Register(options);
        Assert.NotNull(options.FallbackPolicy);
        Assert.Contains(options.FallbackPolicy!.Requirements,
            r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }
}
