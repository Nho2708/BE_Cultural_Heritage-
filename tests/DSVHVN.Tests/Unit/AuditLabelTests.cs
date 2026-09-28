using DSVHVN.Application.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Identity;

namespace DSVHVN.Tests.Unit;

/// <summary>Danh mục nhật ký thao tác: đủ 21 mã, mỗi mã và mỗi vai trò có nhãn tiếng Việt riêng.</summary>
public sealed class AuditLabelTests
{
    [Fact]
    public void There_are_21_action_codes_each_with_its_own_vietnamese_label()
    {
        var actions = Enum.GetValues<AuditAction>();
        Assert.Equal(21, actions.Length);
        var labels = actions.Select(AuditLabels.Of).ToList();
        Assert.All(actions, a => Assert.NotEqual(a.ToString(), AuditLabels.Of(a)));
        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    [Fact]
    public void Every_actor_role_has_a_vietnamese_label_and_matches_the_account_role()
    {
        Assert.All(Enum.GetValues<ActorRole>(), r => Assert.NotEqual(r.ToString(), AuditLabels.Of(r)));
        Assert.All(Enum.GetValues<RoleCode>(), r => Assert.Equal(r.ToString(), Roles.ToActorRole(r).ToString()));
        Assert.Equal("Học sinh", AuditLabels.Of(ActorRole.STUDENT));
    }
}
