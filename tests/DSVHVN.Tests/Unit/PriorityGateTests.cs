using DSVHVN.Infrastructure.Speech;

namespace DSVHVN.Tests.Unit;

/// <summary>Cổng một việc mỗi lúc của giọng đọc tự host: việc ưu tiên cao vào trước việc nền, cùng bậc thì vào trước ra trước, nâng bậc và hủy khi đang chờ.</summary>
public sealed class PriorityGateTests
{
    private static async Task<IDisposable> HoldAsync(PriorityGate gate) =>
        await gate.AcquireAsync(new PriorityGate.Request(high: true), CancellationToken.None);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Điều kiện không thành sau 4 giây.");
    }

    [Fact]
    public async Task A_free_gate_is_taken_at_once_and_only_one_holder_at_a_time()
    {
        var gate = new PriorityGate();
        using var first = await HoldAsync(gate);

        var second = gate.AcquireAsync(new PriorityGate.Request(true), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, gate.Waiting);

        first.Dispose();
        using var _ = await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task High_priority_waiters_go_before_background_ones_and_each_tier_is_first_in_first_out()
    {
        var gate = new PriorityGate();
        var order = new List<string>();
        var holder = await HoldAsync(gate);

        async Task Run(string name, bool high)
        {
            using var _ = await gate.AcquireAsync(new PriorityGate.Request(high), CancellationToken.None);
            lock (order) order.Add(name);
        }

        var tasks = new List<Task>();
        foreach (var (name, high) in new[] { ("nền 1", false), ("nền 2", false), ("nghe 1", true), ("nghe 2", true) })
        {
            tasks.Add(Run(name, high));
            await WaitUntilAsync(() => gate.Waiting == tasks.Count);
        }

        holder.Dispose();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["nghe 1", "nghe 2", "nền 1", "nền 2"], order);
    }

    [Fact]
    public async Task Promoting_a_waiting_background_request_puts_it_ahead_of_later_high_priority_requests()
    {
        var gate = new PriorityGate();
        var order = new List<string>();
        var holder = await HoldAsync(gate);

        var background = new PriorityGate.Request(high: false);
        var listener = new PriorityGate.Request(high: true);

        async Task Run(string name, PriorityGate.Request request)
        {
            using var _ = await gate.AcquireAsync(request, CancellationToken.None);
            lock (order) order.Add(name);
        }

        var first = Run("nền", background);
        await WaitUntilAsync(() => gate.Waiting == 1);
        var second = Run("nghe", listener);
        await WaitUntilAsync(() => gate.Waiting == 2);

        // Có người cần nghe đúng đoạn chữ mà việc nền đang chờ: việc nền thành ưu tiên cao và đứng trước vì xếp hàng sớm hơn.
        background.Promote();
        holder.Dispose();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["nền", "nghe"], order);
    }

    [Fact]
    public async Task Cancelling_a_waiting_request_leaves_the_gate_to_the_next_one()
    {
        var gate = new PriorityGate();
        var holder = await HoldAsync(gate);
        using var cancel = new CancellationTokenSource();

        var cancelled = gate.AcquireAsync(new PriorityGate.Request(true), cancel.Token);
        var next = gate.AcquireAsync(new PriorityGate.Request(false), CancellationToken.None);
        await WaitUntilAsync(() => gate.Waiting == 2);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, gate.Waiting);

        holder.Dispose();
        using var acquired = await next.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Releasing_twice_does_not_hand_the_gate_to_two_waiters()
    {
        var gate = new PriorityGate();
        var holder = await HoldAsync(gate);
        var a = gate.AcquireAsync(new PriorityGate.Request(true), CancellationToken.None);
        var b = gate.AcquireAsync(new PriorityGate.Request(true), CancellationToken.None);
        await WaitUntilAsync(() => gate.Waiting == 2);

        holder.Dispose();
        holder.Dispose();
        using var first = await a.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.False(b.IsCompleted);
    }
}
