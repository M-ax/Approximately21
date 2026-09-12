using System;

namespace Approximately21.Networking;

public enum GameMessageKind
{
    SyncRequest,
    Welcome,
    Command,
    Snapshot,
    TableRemoved,
    CommandResult
}

public abstract record GameMessage<TSnapshot> where TSnapshot : class
{
    public string Protocol { get; init; }
    public int ProtocolVersion { get; init; }
    public ulong LobbyId { get; init; }
    public Guid SessionEpoch { get; init; }
    public Guid ClientSessionId { get; init; }
    public Guid SyncRequestId { get; init; }
    public GameMessageKind Kind { get; init; }
    public string TableId { get; init; }
    public long Revision { get; init; }
    public long CommandId { get; init; }
    public TSnapshot State { get; init; }
    public bool Accepted { get; init; }
    public string Error { get; init; }
}