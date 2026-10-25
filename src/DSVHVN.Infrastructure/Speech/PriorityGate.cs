namespace DSVHVN.Infrastructure.Speech;

/// <summary>
/// Cổng cho phép đúng một việc chạy cùng lúc (một lần gọi Dịch vụ AI sinh giọng) và xếp hàng những việc còn lại theo hai bậc: yêu cầu nghe ngay (ưu tiên cao)
/// luôn được chọn trước việc tạo trước chạy nền, cùng bậc thì vào trước ra trước. Việc đang chạy không bị ngắt: ưu tiên chỉ quyết định ai là người kế tiếp.
/// Yêu cầu đang chờ có thể được nâng bậc (<see cref="Request.Promote"/>) khi có người cần nghe ngay đúng đoạn chữ mà việc tạo trước đang chờ.
/// </summary>
public sealed class PriorityGate
{
    /// <summary>Một lượt xin chạy: giữ bậc ưu tiên hiện tại, nâng lên cao được nhưng không hạ xuống.</summary>
    public sealed class Request(bool high)
    {
        private volatile bool _high = high;

        public bool High => _high;

        public void Promote() => _high = true;
    }

    private sealed class Waiter(Request request)
    {
        public Request Request { get; } = request;

        public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _lock = new();
    private readonly List<Waiter> _waiters = [];
    private bool _busy;

    /// <summary>Số việc đang chờ (không tính việc đang chạy); để kiểm thử và ghi log.</summary>
    public int Waiting
    {
        get
        {
            lock (_lock) return _waiters.Count;
        }
    }

    /// <summary>Chờ tới lượt rồi trả đối tượng giải phóng; gọi <see cref="IDisposable.Dispose"/> để nhường cho việc kế tiếp. Hủy khi đang chờ thì ném <see cref="OperationCanceledException"/> và không giữ cổng.</summary>
    public async Task<IDisposable> AcquireAsync(Request request, CancellationToken ct)
    {
        Waiter waiter;
        lock (_lock)
        {
            if (!_busy)
            {
                _busy = true;
                return new Releaser(this);
            }
            waiter = new Waiter(request);
            _waiters.Add(waiter);
        }

        using var registration = ct.Register(() =>
        {
            lock (_lock) _waiters.Remove(waiter);
            waiter.Signal.TrySetCanceled(ct);
        });
        await waiter.Signal.Task;
        // Quyền giữ cổng đã được chuyển thẳng cho người này (cờ bận không hạ xuống giữa hai việc).
        return new Releaser(this);
    }

    private void Release()
    {
        lock (_lock)
        {
            while (_waiters.Count > 0)
            {
                var index = _waiters.FindIndex(w => w.Request.High);
                if (index < 0) index = 0;
                var next = _waiters[index];
                _waiters.RemoveAt(index);
                if (next.Signal.TrySetResult()) return;
            }
            _busy = false;
        }
    }

    private sealed class Releaser(PriorityGate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
