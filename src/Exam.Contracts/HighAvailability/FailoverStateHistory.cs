namespace Exam.Contracts.HighAvailability;

public static class FailoverStateHistory
{
    public static bool Covers(ServerDeploymentState current, ServerDeploymentState completed) =>
        current.ServerId == completed.ServerId && current.Version >= completed.Version &&
        current.ServerEpoch >= completed.ServerEpoch &&
        (current.ServerEpoch != completed.ServerEpoch || current.ActiveNodeId == completed.ActiveNodeId) &&
        current.Nodes.Keys.Order(StringComparer.Ordinal).SequenceEqual(completed.Nodes.Keys.Order(StringComparer.Ordinal)) &&
        (current.Version != completed.Version || current.ServerEpoch == completed.ServerEpoch &&
            current.ProtectionMode == completed.ProtectionMode &&
            completed.Nodes.All(pair => current.Nodes[pair.Key].Role == pair.Value.Role)) &&
        completed.Audit.All(entry => current.Audit.Any(actual => actual.Action == entry.Action &&
            actual.ActorId == entry.ActorId && actual.Reason == entry.Reason && actual.NodeId == entry.NodeId &&
            actual.ServerEpoch == entry.ServerEpoch));
}
