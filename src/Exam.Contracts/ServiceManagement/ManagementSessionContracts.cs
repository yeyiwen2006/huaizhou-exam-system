using Exam.Contracts.Operators;

namespace Exam.Contracts.ServiceManagement;

public sealed record ManagementPeerSessionRequest(string SourceNodeId, string Credential);
public sealed record ManagementBusinessSessionRequest(string ActiveNodeId, long ExpectedEpoch);
public sealed record ManagementBusinessSession(string ServerId, string NodeId, long ServerEpoch,
    string BusinessAddress, OperatorSession Session);
