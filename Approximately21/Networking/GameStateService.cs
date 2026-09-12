using System;
using System.Collections.Generic;
using System.Linq;

namespace Approximately21.Networking;

public abstract class GameStateService<TGame, TMessage, TSnapshot> : IDisposable
    where TGame : class
    where TMessage : GameMessage<TSnapshot>, new()
    where TSnapshot : class
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private readonly IGameTransport _transport;
    private readonly GameProtocol<TMessage, TSnapshot> _protocol;
    private readonly Func<TGame> _createGame;
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<string, AuthoritativeTable> _tables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _replicas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _revisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _removed = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, PeerSession> _peers = new();
    private LobbyContext _lobby;
    private Guid _clientSessionId;
    private Guid _syncRequestId;
    private long _nextCommandId;
    private TMessage _pending;
    private DateTimeOffset _lastSync;
    private DateTimeOffset _lastCommand;
    private bool _disposed;

    protected GameStateService(IGameTransport transport, GameProtocol<TMessage, TSnapshot> protocol,
        Func<TGame> createGame, Func<DateTimeOffset> clock = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        _createGame = createGame ?? throw new ArgumentNullException(nameof(createGame));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public Guid SessionEpoch { get; private set; }
    public bool IsHost => _lobby?.IsHost == true;
    public bool IsReady => _lobby != null && SessionEpoch != Guid.Empty;
    public bool HasPendingCommand => _pending != null;
    public event Action<string> TableChanged;
    public event Action SessionReset;
    public event Action<CommandCompletion> CommandCompleted;

    protected abstract bool TryApply(TGame game, ulong playerId, TMessage command, out string error);
    protected abstract bool DisconnectPlayer(TGame game, ulong playerId);
    protected abstract TSnapshot CreateSnapshot(TGame game);

    public void SetLobby(LobbyContext lobby)
    {
        CheckThread();
        if (lobby == null || _lobby == null || lobby.LobbyId != _lobby.LobbyId ||
            lobby.LocalPlayerId != _lobby.LocalPlayerId || lobby.HostPlayerId != _lobby.HostPlayerId)
        {
            Reset(lobby);
            return;
        }

        var departed = _lobby.Members.Except(lobby.Members).ToArray();
        _lobby = lobby;
        _transport.SetPeers(lobby.Members.Where(id => id != lobby.LocalPlayerId).ToArray());
        if (!IsHost)
            return;
        foreach (var playerId in departed)
        {
            _peers.Remove(playerId);
            foreach (var pair in _tables)
                if (DisconnectPlayer(pair.Value.Game, playerId))
                    Publish(pair.Key, pair.Value);
        }
    }

    public void ResetSession()
    {
        CheckThread();
        Reset(_lobby);
    }

    public bool RegisterTable(string tableId)
    {
        CheckThread();
        if (!IsHost || !GameProtocol<TMessage, TSnapshot>.IsTableIdValid(tableId) || _tables.ContainsKey(tableId) ||
            _removed.ContainsKey(tableId) || _tables.Count + _removed.Count >= NetworkLimits.MaxTablesPerSession)
            return false;
        var table = new AuthoritativeTable(_createGame());
        _tables.Add(tableId, table);
        Publish(tableId, table);
        return true;
    }

    public bool RemoveTable(string tableId)
    {
        CheckThread();
        if (!IsHost || tableId == null || !_tables.Remove(tableId, out var table))
            return false;
        var revision = checked(table.Revision + 1);
        _removed.Add(tableId, revision);
        var message = Removal(tableId, revision);
        ApplyReplica(message);
        Broadcast(message);
        return true;
    }

    public IReadOnlyList<string> GetTableIds()
    {
        CheckThread();
        return _replicas.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    public bool TryGetTable(string tableId, out ReplicatedTable<TSnapshot> table)
    {
        CheckThread();
        table = null;
        if (tableId == null || !_replicas.TryGetValue(tableId, out var payload) ||
            !_protocol.TryDecode(payload, out var message))
            return false;
        table = new ReplicatedTable<TSnapshot>(tableId, message.Revision, message.State);
        return true;
    }

    protected bool TrySubmit(string tableId, TMessage command, out long commandId)
    {
        CheckThread();
        commandId = 0;
        if (!IsReady || _pending != null || !GameProtocol<TMessage, TSnapshot>.IsTableIdValid(tableId) ||
            !_replicas.ContainsKey(tableId) || command == null || !_protocol.IsCommandValid(command) ||
            _nextCommandId == long.MaxValue)
            return false;
        commandId = ++_nextCommandId;
        command = command with
        {
            Protocol = _protocol.Name, ProtocolVersion = _protocol.Version,
            Kind = GameMessageKind.Command, LobbyId = _lobby.LobbyId, SessionEpoch = SessionEpoch,
            ClientSessionId = _clientSessionId, SyncRequestId = Guid.Empty,
            TableId = tableId, Revision = _revisions[tableId], CommandId = commandId,
            State = null, Accepted = false, Error = null
        };
        if (IsHost)
            HandleCommand(_lobby.LocalPlayerId, command);
        else
        {
            _pending = command;
            _lastCommand = _clock();
            Send(_lobby.HostPlayerId, command);
        }
        return true;
    }

    public void Pump()
    {
        CheckThread();
        for (var i = 0; i < NetworkLimits.MaxMessagesPerPump && _transport.TryReceive(out var packet); i++)
        {
            if (_lobby == null || packet.SenderId == _lobby.LocalPlayerId || !_lobby.Members.Contains(packet.SenderId) ||
                !_protocol.TryDecode(packet.Payload, out var message) || message.LobbyId != _lobby.LobbyId)
                continue;
            if (IsHost)
            {
                if (message.Kind == GameMessageKind.SyncRequest)
                    HandleSync(packet.SenderId, message);
                else if (message.Kind == GameMessageKind.Command && message.SessionEpoch == SessionEpoch)
                    HandleCommand(packet.SenderId, message);
            }
            else if (packet.SenderId == _lobby.HostPlayerId)
                HandleHostMessage(message);
        }

        if (_lobby == null || IsHost)
            return;
        var now = _clock();
        if (now - _lastSync >= (IsReady && _pending == null ? RefreshInterval : RetryInterval))
            RequestSync();
        if (_pending != null && now - _lastCommand >= RetryInterval)
        {
            _lastCommand = now;
            Send(_lobby.HostPlayerId, _pending);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        CheckThread();
        Reset(null);
        _transport.Dispose();
        _disposed = true;
    }

    private void Reset(LobbyContext lobby)
    {
        _lobby = lobby;
        _tables.Clear();
        _replicas.Clear();
        _revisions.Clear();
        _removed.Clear();
        _peers.Clear();
        _pending = null;
        _nextCommandId = 0;
        _clientSessionId = Guid.NewGuid();
        _syncRequestId = Guid.Empty;
        SessionEpoch = lobby?.IsHost == true ? Guid.NewGuid() : Guid.Empty;
        _transport.SetPeers(lobby == null ? Array.Empty<ulong>() :
            lobby.Members.Where(id => id != lobby.LocalPlayerId).ToArray());
        if (IsHost)
            _peers.Add(lobby.LocalPlayerId, new PeerSession(_clientSessionId));
        SessionReset?.Invoke();
        if (lobby != null && !IsHost)
            RequestSync();
    }

    private void RequestSync()
    {
        _lastSync = _clock();
        if (_syncRequestId == Guid.Empty)
            _syncRequestId = Guid.NewGuid();
        Send(_lobby.HostPlayerId, new TMessage
        {
            Protocol = _protocol.Name, ProtocolVersion = _protocol.Version,
            Kind = GameMessageKind.SyncRequest, LobbyId = _lobby.LobbyId, SessionEpoch = SessionEpoch,
            ClientSessionId = _clientSessionId, SyncRequestId = _syncRequestId
        });
    }

    private void HandleSync(ulong senderId, TMessage request)
    {
        var now = _clock();
        if (_peers.TryGetValue(senderId, out var peer) && now - peer.LastSync < RetryInterval)
            return;
        if (peer == null || peer.ClientSessionId != request.ClientSessionId)
            _peers[senderId] = peer = new PeerSession(request.ClientSessionId);
        peer.LastSync = now;
        Send(senderId, new TMessage
        {
            Protocol = _protocol.Name, ProtocolVersion = _protocol.Version,
            Kind = GameMessageKind.Welcome, LobbyId = _lobby.LobbyId, SessionEpoch = SessionEpoch,
            ClientSessionId = request.ClientSessionId, SyncRequestId = request.SyncRequestId
        });
        foreach (var payload in _replicas.Values)
            _transport.TrySend(senderId, payload);
        foreach (var pair in _removed)
            Send(senderId, Removal(pair.Key, pair.Value));
    }

    private void HandleHostMessage(TMessage message)
    {
        if (message.Kind == GameMessageKind.Welcome)
        {
            if (_syncRequestId == Guid.Empty || message.SyncRequestId != _syncRequestId ||
                message.ClientSessionId != _clientSessionId)
                return;
            _syncRequestId = Guid.Empty;
            if (SessionEpoch != message.SessionEpoch)
            {
                _replicas.Clear();
                _revisions.Clear();
                _pending = null;
                SessionEpoch = message.SessionEpoch;
                SessionReset?.Invoke();
            }
            return;
        }
        if (!IsReady || message.SessionEpoch != SessionEpoch)
            return;
        if (message.Kind is GameMessageKind.Snapshot or GameMessageKind.TableRemoved)
            ApplyReplica(message);
        else if (message.Kind == GameMessageKind.CommandResult && _pending != null &&
                 message.ClientSessionId == _clientSessionId && message.CommandId == _pending.CommandId &&
                 message.TableId == _pending.TableId)
        {
            _pending = null;
            CommandCompleted?.Invoke(new CommandCompletion(message.CommandId, message.TableId, message.Accepted, message.Error));
        }
    }

    private void HandleCommand(ulong senderId, TMessage command)
    {
        if (!_peers.TryGetValue(senderId, out var peer) || peer.ClientSessionId != command.ClientSessionId)
            return;
        if (senderId != _lobby.LocalPlayerId)
        {
            var now = _clock();
            if (now - peer.CommandWindow >= TimeSpan.FromSeconds(1))
            {
                peer.CommandWindow = now;
                peer.CommandCount = 0;
            }
            if (++peer.CommandCount > 32)
                return;
        }
        if (command.CommandId <= peer.LastCommandId)
        {
            if (command.CommandId == peer.LastCommandId && peer.LastResult != null)
                Send(senderId, peer.LastResult);
            return;
        }
        peer.LastCommandId = command.CommandId;
        var accepted = false;
        string error;
        if (!_tables.TryGetValue(command.TableId, out var table))
            error = "Table does not exist.";
        else if (command.Revision != table.Revision)
            error = "Table changed; retry against the latest snapshot.";
        else
        {
            accepted = TryApply(table.Game, senderId, command, out error);
            if (accepted)
                Publish(command.TableId, table);
        }

        var result = new TMessage
        {
            Protocol = _protocol.Name, ProtocolVersion = _protocol.Version,
            Kind = GameMessageKind.CommandResult, LobbyId = _lobby.LobbyId, SessionEpoch = SessionEpoch,
            ClientSessionId = command.ClientSessionId, CommandId = command.CommandId, TableId = command.TableId,
            Accepted = accepted, Error = error
        };
        peer.LastResult = result;
        if (senderId == _lobby.LocalPlayerId)
            CommandCompleted?.Invoke(new CommandCompletion(command.CommandId, command.TableId, accepted, error));
        else
        {
            if (_replicas.TryGetValue(command.TableId, out var payload))
                _transport.TrySend(senderId, payload);
            Send(senderId, result);
        }
    }

    private void Publish(string tableId, AuthoritativeTable table)
    {
        var message = new TMessage
        {
            Protocol = _protocol.Name, ProtocolVersion = _protocol.Version,
            Kind = GameMessageKind.Snapshot, LobbyId = _lobby.LobbyId, SessionEpoch = SessionEpoch,
            TableId = tableId, Revision = checked(++table.Revision), State = CreateSnapshot(table.Game)
        };
        ApplyReplica(message);
        Broadcast(message);
    }

    private void ApplyReplica(TMessage message)
    {
        if (_revisions.TryGetValue(message.TableId, out var revision) && message.Revision <= revision)
            return;
        if (!_revisions.ContainsKey(message.TableId) && _revisions.Count >= NetworkLimits.MaxTablesPerSession)
            return;
        var payload = _protocol.Encode(message);
        _revisions[message.TableId] = message.Revision;
        if (message.Kind == GameMessageKind.TableRemoved)
            _replicas.Remove(message.TableId);
        else
            _replicas[message.TableId] = payload;
        TableChanged?.Invoke(message.TableId);
        if (message.Kind == GameMessageKind.TableRemoved && _pending?.TableId == message.TableId)
        {
            var command = _pending;
            _pending = null;
            CommandCompleted?.Invoke(new CommandCompletion(command.CommandId, command.TableId, false, "Table removed."));
        }
    }

    private TMessage Removal(string tableId, long revision) => new()
    {
        Protocol = _protocol.Name, ProtocolVersion = _protocol.Version,
        Kind = GameMessageKind.TableRemoved, LobbyId = _lobby.LobbyId, SessionEpoch = SessionEpoch,
        TableId = tableId, Revision = revision
    };

    private void Broadcast(TMessage message)
    {
        var payload = _protocol.Encode(message);
        foreach (var peerId in _peers.Keys.ToArray())
            if (peerId != _lobby.LocalPlayerId)
                _transport.TrySend(peerId, payload);
    }

    private void Send(ulong recipientId, TMessage message) =>
        _transport.TrySend(recipientId, _protocol.Encode(message));

    private void CheckThread()
    {
        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);
        if (_ownerThread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Pump and access game state only on its owning thread.");
    }

    private sealed class AuthoritativeTable
    {
        public AuthoritativeTable(TGame game) => Game = game ?? throw new ArgumentNullException(nameof(game));
        public TGame Game { get; }
        public long Revision { get; set; }
    }

    private sealed class PeerSession
    {
        public PeerSession(Guid clientSessionId) => ClientSessionId = clientSessionId;
        public Guid ClientSessionId { get; }
        public long LastCommandId { get; set; }
        public TMessage LastResult { get; set; }
        public DateTimeOffset LastSync { get; set; }
        public DateTimeOffset CommandWindow { get; set; }
        public int CommandCount { get; set; }
    }
}