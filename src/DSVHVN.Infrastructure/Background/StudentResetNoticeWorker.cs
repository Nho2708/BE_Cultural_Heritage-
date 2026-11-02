using System.Threading.Channels;
using DSVHVN.Application.Common;
using DSVHVN.Application.StudentAuth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DSVHVN.Infrastructure.Background;

/// <summary>
/// Hàng đợi và tác vụ nền gửi thư báo giáo viên chủ nhiệm khi học sinh xin đặt lại mật khẩu. Xếp vào hàng đợi là trả ngay nên yêu cầu của học sinh tốn thời gian như nhau
/// dù mã học sinh có thật hay không (gửi thư qua SMTP có thể mất vài giây). Hàng đợi trong bộ nhớ, tối đa 500 thư chờ: đầy thì bỏ thư mới và ghi cảnh báo (bộ giới hạn tần suất
/// ở lớp dịch vụ đã chặn việc dồn thư); lỗi gửi chỉ ghi log, không thử lại, giáo viên vẫn thấy học sinh cần đặt lại mật khẩu trên màn hình lớp. Khởi động lại thì thư chưa gửi mất.
/// </summary>
public sealed class StudentResetNoticeWorker(IServiceScopeFactory scopes, ILogger<StudentResetNoticeWorker> logger) : BackgroundService, IStudentResetNoticeQueue
{
    private const int Capacity = 500;

    private readonly Channel<StudentResetNotice> _queue = Channel.CreateBounded<StudentResetNotice>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    public void Enqueue(StudentResetNotice notice)
    {
        if (!_queue.Writer.TryWrite(notice))
            logger.LogWarning("Hàng đợi thư báo giáo viên đã đầy, bỏ một thư báo học sinh quên mật khẩu.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var notice in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IAccountMailer>().SendStudentPasswordResetRequestToTeacherAsync(
                        notice.TeacherEmail, notice.TeacherName, notice.StudentCode, notice.StudentName, notice.ClassName, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Không ghi địa chỉ email hay mã học sinh vào log.
                    logger.LogError(ex, "Gửi thư báo giáo viên về học sinh quên mật khẩu thất bại.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
