using System.Net.Http.Headers;
using System.Text.Json;
using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Contracts.ServiceManagement;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>Observe the running host's startup result; status polling never replays the examination database.</summary>
public sealed class RunningBusinessMaterialProbe(HttpClient http, Uri businessAddress,
    string serverId, string nodeId, Func<long> epoch, Func<string> peerToken, TimeProvider? time = null) : IStartupReadinessProbe
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private HighAvailabilityMaterialProof? _lastProof;

    private bool Current(HighAvailabilityMaterialProof proof) =>
        proof.ServerId == serverId && proof.NodeId == nodeId && proof.Epoch == epoch() &&
        _time.GetUtcNow() - proof.ObservedAt < HighAvailabilityStatus.ObservationLifetime &&
        proof.ObservedAt <= _time.GetUtcNow().AddSeconds(5);

    public async Task<IReadOnlyList<StartupCheckResult>> RunAsync(ServiceConfiguration configuration, CancellationToken token)
    {
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
            if (proof is not null && Current(proof))
                lock (_gate)
                    if (_lastProof is null || proof.Epoch != _lastProof.Epoch || proof.ObservedAt >= _lastProof.ObservedAt)
                        _lastProof = proof;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { }
        // A failed read is not a failed certificate or recovery check. Reuse only
        // this node's unexpired proof at the current epoch; never extend its time.
        HighAvailabilityMaterialProof? current;
        lock (_gate) current = _lastProof is { } prior && Current(prior) ? prior : null;
        var keys = current?.KeysReady == true; var certificate = current?.CertificateReady == true;
        return [new("node-certificate-private-key", certificate, certificate ? "运行节点证书及私钥已核实。" : "运行节点证书状态尚未核实。", true),
            new("recovery-snapshot", keys, keys ? "运行节点的启动恢复核对已通过。" : current is null ? "暂未取得运行节点的启动状态。" : "运行节点的启动恢复核对尚未通过。", true)];
    }
}
