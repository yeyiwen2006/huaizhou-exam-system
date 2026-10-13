namespace Exam.Admin.Services;

/// <summary>A short-lived in-memory copy of the verified business identity for management session exchange.</summary>
public sealed class AdminSessionSnapshot
{
    internal AdminSessionSnapshot(Uri endpoint, string serverId, string username, string credential, long generation)
    { Endpoint = endpoint; ServerId = serverId; Username = username; Credential = credential; Generation = generation; }

    public Uri Endpoint { get; }
    public string ServerId { get; }
    public string Username { get; }
    public long Generation { get; }
    internal string Credential { get; }
    internal bool Matches(AdminSessionSnapshot other) => Generation == other.Generation && Endpoint == other.Endpoint &&
        ServerId == other.ServerId && Username == other.Username && Credential == other.Credential;
}
