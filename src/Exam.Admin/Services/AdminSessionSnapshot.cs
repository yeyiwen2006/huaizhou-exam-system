namespace Exam.Admin.Services;

/// <summary>A short-lived in-memory copy of the verified business identity for management session exchange.</summary>
public sealed class AdminSessionSnapshot
{
    internal AdminSessionSnapshot(Uri endpoint, string serverId, string username, string credential, long generation, string? nodeId = null, long serverEpoch = 0)
    { Endpoint = endpoint; ServerId = serverId; Username = username; Credential = credential; Generation = generation; NodeId = nodeId; ServerEpoch = serverEpoch; }

    public Uri Endpoint { get; }
    public string ServerId { get; }
    public string Username { get; }
    public string? NodeId { get; }
    public long ServerEpoch { get; }
    public long Generation { get; }
    internal string Credential { get; }
    internal bool SameAuthentication(AdminSessionSnapshot other) => ServerId == other.ServerId &&
        Username == other.Username && Credential == other.Credential;
    internal bool Matches(AdminSessionSnapshot other) => Generation == other.Generation && Endpoint == other.Endpoint && NodeId == other.NodeId && ServerEpoch == other.ServerEpoch &&
        ServerId == other.ServerId && Username == other.Username && Credential == other.Credential;
}
