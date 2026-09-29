using DSVHVN.Application.Common;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using DSVHVN.Infrastructure.Persistence;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy tắc thuần: username, ngày của gói theo giờ Việt Nam, token đặt mật khẩu, tên cột DBML.</summary>
public sealed class RulesTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("nguyen.van_an")]
    [InlineData("GiaoVien01")]
    public void Username_accepts_BR01(string username) => Assert.True(UsernamePolicy.IsSatisfiedBy(username));

    [Theory]
    [InlineData(null)]
    [InlineData("ab")]
    [InlineData("nguyễn")]          // chữ có dấu
    [InlineData("co khoang")]
    [InlineData("email@truong.vn")]  // không có '@' nên đăng nhập phân biệt được email và username
    public void Username_rejects_violations(string? username) => Assert.False(UsernamePolicy.IsSatisfiedBy(username));

    [Fact]
    public void Username_length_boundaries_are_3_and_50()
    {
        Assert.True(UsernamePolicy.IsSatisfiedBy("gv." + new string('a', 47)));   // 50
        Assert.False(UsernamePolicy.IsSatisfiedBy("gv." + new string('a', 48)));  // 51
    }

    [Fact]
    public void Username_and_email_are_normalized_to_lowercase() =>
        Assert.Equal(("giaovien.a", "gv@truong.vn"), (UsernamePolicy.Normalize("  GiaoVien.A "), EmailAddress.Normalize(" GV@Truong.VN ")));

    [Fact]
    public void Vietnam_today_rolls_over_at_17h_utc()
    {
        Assert.Equal(new DateOnly(2026, 9, 29), VietnamTime.Today(new DateTimeOffset(2026, 9, 29, 16, 59, 59, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 9, 30), VietnamTime.Today(new DateTimeOffset(2026, 9, 29, 17, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Subscription_end_date_and_effective_window_follow_BR31()
    {
        var start = new DateOnly(2026, 9, 29);
        var sub = new Subscription
        {
            Status = SubscriptionStatus.ACTIVE, StartDate = start, EndDate = Subscription.EndDateFor(start, 30),
        };
        Assert.Equal(new DateOnly(2026, 10, 28), sub.EndDate);
        Assert.False(sub.IsEffectiveOn(start.AddDays(-1)));
        Assert.True(sub.IsEffectiveOn(start));
        Assert.True(sub.IsEffectiveOn(new DateOnly(2026, 10, 28)));
        Assert.False(sub.IsEffectiveOn(new DateOnly(2026, 10, 29)));

        sub.Status = SubscriptionStatus.PENDING;
        Assert.False(sub.IsEffectiveOn(start));
    }

    [Fact]
    public void Password_token_is_32_random_bytes_and_stored_as_lowercase_sha256_hex()
    {
        var raw = SecureTokens.Create();
        Assert.Equal(43, raw.Length);                     // base64url của 32 byte
        Assert.NotEqual(raw, SecureTokens.Create());
        var hash = SecureTokens.Hash(raw);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, SecureTokens.Hash(raw));
    }

    [Fact]
    public void Security_stamp_changes_with_password_hash()
    {
        Assert.Equal(16, SecurityStamps.From("hash-a").Length);
        Assert.NotEqual(SecurityStamps.From("hash-a"), SecurityStamps.From("hash-b"));
    }

    [Theory]
    [InlineData("OrganizationId", "organization_id")]
    [InlineData("MaxAiRequests", "max_ai_requests")]
    [InlineData("IpAddress", "ip_address")]
    [InlineData("Id", "id")]
    [InlineData("LastLoginAt", "last_login_at")]
    public void Column_names_follow_DBML_snake_case(string property, string column) =>
        Assert.Equal(column, AppDbContext.ToSnakeCase(property));
}
