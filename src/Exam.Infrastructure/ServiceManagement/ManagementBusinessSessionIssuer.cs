using Exam.Application.Security;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;
using Exam.Infrastructure.HighAvailability;
using Exam.Infrastructure.Postgres.HighAvailability;
using Exam.Infrastructure.Postgres.Operators;

namespace Exam.Infrastructure.ServiceManagement;

public sealed class ManagementBusinessSessionIssuer(Func<string, string?> configuration,
    string serverId, string nodeId, Func<long> currentEpoch)
{
    public async Task<ManagementBusinessSession> IssueAsync(AuthenticatedOperator actor,
        ManagementBusinessSessionRequest request, CancellationToken token)
    {
        var root = "HighAvailability:Nodes:" + request.ActiveNodeId + ":";
        var connectionString = configuration(root + "ConnectionString");
        var address = configuration(root + "BusinessHttpsUrl");
        var directory = configuration("Confirmation:Directory") ?? configuration("HighAvailability:Nodes:" + nodeId + ":ConfirmationDirectory");
        var retired = string.IsNullOrWhiteSpace(directory) ? null : new NodeRetirementStore(directory).Read();
        var minimumEpoch = Math.Max(currentEpoch(), retired?.ActiveEpoch ?? 1);
        if (string.IsNullOrWhiteSpace(connectionString) || !Uri.TryCreate(address, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != "https" || endpoint.AbsolutePath != "/" || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new DomainRuleException(ErrorCodes.InvalidInput, "活动主机不属于当前部署。");
        if (request.ExpectedEpoch < minimumEpoch)
            throw new DomainRuleException(ErrorCodes.StaleServerEpoch, "活动主机已变化，正在刷新连接。", true);
        await using var source = ManagementProbeConnection.Create(connectionString, 30);
        var state = await new PostgresFailoverStateStore(source).LoadAsync(serverId, token);
        await using var recovery = source.CreateCommand("SELECT pg_is_in_recovery()");
        if (state is null || state.ActiveNodeId != request.ActiveNodeId || state.ServerEpoch < request.ExpectedEpoch ||
            state.ServerEpoch < minimumEpoch || (bool)(await recovery.ExecuteScalarAsync(token) ?? true))
            throw new DomainRuleException(ErrorCodes.NotActiveServer, "目标尚未成为当前活动主机。", true);
        var session = await new PostgresOperatorAccounts(source).IssueAuthenticatedSessionAsync(actor.Username, token);
        return new(serverId, state.ActiveNodeId, state.ServerEpoch, endpoint.AbsoluteUri, session);
    }
}
