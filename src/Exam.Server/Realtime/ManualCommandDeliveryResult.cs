using Exam.Application.HighAvailability;
using Exam.Contracts.Commands;
using Microsoft.AspNetCore.SignalR;

namespace Exam.Server.Realtime;

/// <summary>手动重发必须在持久确认之后实际推送；只修改重试计数不会让已确认的 END 再次送达。</summary>
public sealed class ManualCommandDeliveryResult(IResult response, CommandDelivery delivery) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        if (context.RequestServices.GetService<IBusinessConfirmation>() is { } confirmation)
            await confirmation.ConfirmAsync(context.RequestAborted);
        // 正式入口注册 SignalR；不启用实时通道的接口夹具仍可独立核对返回契约。
        if (context.RequestServices.GetService<IHubContext<ExamEventHub>>() is { } hub)
            await hub.Clients.Group(ExamEventHub.GroupName(delivery.ParticipationId))
                .SendAsync("Command", delivery, context.RequestAborted);
        await response.ExecuteAsync(context);
    }
}
