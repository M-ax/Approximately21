using System;
using System.Collections.Generic;
using System.Linq;

namespace Approximately21.Networking;

public sealed class LobbyContext
{
    public LobbyContext(ulong lobbyId, ulong localPlayerId, ulong hostPlayerId, IEnumerable<ulong> members)
    {
        var ids = members?.Distinct().ToArray() ?? throw new ArgumentNullException(nameof(members));
        if (lobbyId == 0 || localPlayerId == 0 || hostPlayerId == 0 || ids.Length > NetworkLimits.MaxPeers ||
            ids.Contains(0UL) || !ids.Contains(localPlayerId) || !ids.Contains(hostPlayerId))
            throw new ArgumentException("A lobby needs a verified host, local player, and nonzero member identities.");

        LobbyId = lobbyId;
        LocalPlayerId = localPlayerId;
        HostPlayerId = hostPlayerId;
        Members = Array.AsReadOnly(ids);
    }

    public ulong LobbyId { get; }
    public ulong LocalPlayerId { get; }
    public ulong HostPlayerId { get; }
    public IReadOnlyList<ulong> Members { get; }
    public bool IsHost => LocalPlayerId == HostPlayerId;
}