using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Operators;
using Exam.Contracts.ServiceManagement;

namespace Exam.Admin.Services;

public sealed partial class AdminApiClient
{
    public async Task LoginWithManagementSessionAsync(ManagementBusinessSession session, CancellationToken token)
    {
        var address = NormalizeAddress(session.BusinessAddress);
        using var client = CreateClient();
        client.BaseAddress = address;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Session.Token);
        using var response = await client.GetAsync("api/v1/operators/me", token);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<OperatorAccount>>(_json, token);
        if (!response.IsSuccessStatusCode || envelope is not { Error: null, Result: { } account } ||
            envelope.ServerId != session.ServerId || envelope.NodeId != session.NodeId || envelope.ServerEpoch < session.ServerEpoch ||
            !string.Equals(account.Username, session.Session.Account.Username, StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("当前活动主机的考务服务尚未恢复。");
        Configure(address.AbsoluteUri, session.Session.Token, account.Username);
        _serverId = envelope.ServerId; _nodeId = envelope.NodeId; _serverEpoch = envelope.ServerEpoch; _account = account;
        await LearnRoutesAsync(token);
    }
}
