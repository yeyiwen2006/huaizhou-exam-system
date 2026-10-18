using System.Net.Http.Headers;
using System.Text.Json;
using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Contracts.ServiceManagement;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>Observe the running host's startup result; status polling never replays the examination database.</summary>
public sealed class RunningBusinessMaterialProbe(HttpClient http, Uri businessAddress,
    string serverId, string nodeId, Func<long> epoch, Func<string> peerToken) : IStartupReadinessProbe
{
    public async Task<IReadOnlyList<StartupCheckResult>> RunAsync(ServiceConfiguration configuration, CancellationToken token)
    {
        var keys = false; var certificate = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(businessAddress, "replication/v1/readiness"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", peerToken());
            request.Headers.Add("X-Exam-Server-Id", serverId);
            request.Headers.Add("X-Exam-Node-Id", nodeId);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(16384, token);
            await using var body = await response.Content.ReadAsStreamAsync(token);
            var proof = await JsonSerializer.DeserializeAsync<HighAvailabilityMaterialProof>(body, JsonDefaults.Create(), token);
            if (proof is not null && proof.ServerId == serverId && proof.NodeId == nodeId && proof.Epoch == epoch() &&
                DateTimeOffset.UtcNow - proof.ObservedAt <= TimeSpan.FromSeconds(10) && proof.ObservedAt <= DateTimeOffset.UtcNow.AddSeconds(5))
            { keys = proof.KeysReady; certificate = proof.CertificateReady; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { }
        return [new("node-certificate-private-key", certificate, certificate ? "运行节点证书及私钥已核实。" : "运行节点证书状态尚未核实。", true),
            new("recovery-snapshot", keys, keys ? "运行节点的启动恢复核对已通过。" : "运行节点的启动恢复核对尚未通过。", true)];
    }
}
