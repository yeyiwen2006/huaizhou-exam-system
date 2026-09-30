using Exam.Application.Commands;
using Microsoft.AspNetCore.SignalR;

namespace Exam.Server.Realtime;

/// <summary>
/// Pushes due persistent commands to connected devices. PostgreSQL acknowledgement state remains
/// the reliability boundary; SignalR is only the transport.
/// </summary>
public sealed class ReliableCommandDispatcher(
    RealtimeConnectionRegistry connections,
    ReliableCommandService commands,
    RealtimeDeviceAuthorizer authorizer,
    RealtimeDispatchOptions options,
    IHubContext<ExamEventHub> hub,
    ILogger<ReliableCommandDispatcher> logger,
    Exam.Application.HighAvailability.IBusinessConfirmation? confirmation = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await Parallel.ForEachAsync(
                connections.Snapshot(),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.MaxConcurrentDispatches,
                    CancellationToken = stoppingToken
                },
                async (connection, cancellationToken) =>
                    await DispatchAsync(connection, cancellationToken));
        }
    }

    private async Task DispatchAsync(
        RealtimeDeviceConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            var identity = connection.Identity;
            if (!await authorizer.IsStillAuthorizedAsync(identity, cancellationToken))
            {
                connections.Remove(connection.ConnectionId);
                await hub.Clients.Client(connection.ConnectionId)
                    .SendAsync("AuthorizationRevoked", "设备激活或参试授权已失效。", cancellationToken);
                return;
            }

            var result = await commands.GetDueForDeviceAsync(
                identity.ParticipationId,
                identity.EntryVersion,
                identity.DeviceId,
                identity.AuthorizationVersion,
                identity.ServerId,
                identity.ServerEpoch,
                cancellationToken);
            if (!result.IsSuccess)
            {
                if (result.ErrorCode == Exam.Contracts.Commands.CommandErrorCode.AuthorizationRevoked)
                {
                    connections.Remove(connection.ConnectionId);
                    await hub.Clients.Client(connection.ConnectionId)
                        .SendAsync("AuthorizationRevoked", result.Message, cancellationToken);
                }

                return;
            }

            foreach (var delivery in result.Value ?? [])
            {
                if (confirmation is not null) await confirmation.ConfirmAsync(cancellationToken);
                await hub.Clients.Client(connection.ConnectionId)
                    .SendAsync("Command", delivery, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "实时指令投递失败，事件将按持久退避状态重试。ConnectionId={ConnectionId}",
                connection.ConnectionId);
        }
    }
}
