using Microsoft.AspNetCore.SignalR;

namespace Exam.Server.Realtime;

public sealed class ExamEventHub : Hub
{
    public override Task OnConnectedAsync()
    {
        // Authentication and participation-group membership are added with A03 integration.
        // Until then this transport exposes no client-invokable business operation.
        return base.OnConnectedAsync();
    }
}
