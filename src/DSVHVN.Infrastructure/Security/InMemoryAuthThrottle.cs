using DSVHVN.Application.Common;
using DSVHVN.Domain.Rules;
using Microsoft.Extensions.Caching.Memory;

namespace DSVHVN.Infrastructure.Security;

/// <summary>
/// Chống dò mật khẩu (sai 5 lần liên tiếp trong 15 phút → tạm khóa đăng nhập 15 phút, không đổi <c>users.status</c>) và
/// giới hạn quên mật khẩu (3 yêu cầu quên mật khẩu mỗi giờ mỗi email), đếm trong bộ nhớ máy chủ: khởi động lại thì về 0 (không có bảng
/// lưu). Đủ cho một máy chủ. Mốc thời gian lấy từ <see cref="TimeProvider"/> để kiểm thử tua giờ;
/// cache chỉ dùng để dọn mục cũ.
/// </summary>
public sealed class InMemoryAuthThrottle(IMemoryCache cache, TimeProvider clock) : IAuthThrottle
{
    private static readonly TimeSpan ForgotWindow = TimeSpan.FromHours(1);

    public TimeSpan? GetLoginLockout(string key)
    {
        if (!cache.TryGetValue(LoginKey(key), out LoginState? state) || state is null) return null;
        lock (state)
        {
            if (state.LockedUntil is not { } until) return null;
            var now = Now();
            if (until > now) return until - now;
            state.LockedUntil = null;
            state.Failures.Clear();
            return null;
        }
    }

    public bool RegisterFailedLogin(string key)
    {
        var state = cache.GetOrCreate(LoginKey(key), entry =>
        {
            entry.SlidingExpiration = AccountRules.FailedLoginWindow + AccountRules.LoginLockoutDuration;
            return new LoginState();
        })!;

        lock (state)
        {
            var now = Now();
            while (state.Failures.Count > 0 && now - state.Failures.Peek() >= AccountRules.FailedLoginWindow)
                state.Failures.Dequeue();
            state.Failures.Enqueue(now);
            if (state.Failures.Count < AccountRules.MaxFailedLogins) return false;

            state.Failures.Clear();
            state.LockedUntil = now + AccountRules.LoginLockoutDuration;
            return true;
        }
    }

    public void ResetFailedLogins(string key) => cache.Remove(LoginKey(key));

    public bool TryConsumeForgotPasswordRequest(string email)
    {
        var requests = cache.GetOrCreate(ForgotKey(email), entry =>
        {
            entry.SlidingExpiration = ForgotWindow;
            return new Queue<DateTime>();
        })!;

        lock (requests)
        {
            var now = Now();
            while (requests.Count > 0 && now - requests.Peek() >= ForgotWindow) requests.Dequeue();
            if (requests.Count >= AccountRules.MaxForgotPasswordRequestsPerHour) return false;
            requests.Enqueue(now);
            return true;
        }
    }

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private static string LoginKey(string key) => "auth:login:" + key;
    private static string ForgotKey(string email) => "auth:forgot:" + email;

    private sealed class LoginState
    {
        public Queue<DateTime> Failures { get; } = new();
        public DateTime? LockedUntil { get; set; }
    }
}
