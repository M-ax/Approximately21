using System.Collections.Generic;
using System.Linq;

namespace Approximately21.Networking;

public static class BlackjackLobbyResolver
{
    public static LobbyContext Resolve(ulong lobbyId, ulong localId, bool isHost, bool sessionReady,
        ulong authenticatedRemoteHost, IEnumerable<ulong> members)
    {
        if (!sessionReady || lobbyId == 0 || localId == 0 || members == null)
            return null;
        var host = isHost ? localId : authenticatedRemoteHost;
        var ids = members.ToArray();
        if (host == 0 || (!isHost && host == localId) || ids.Length > NetworkLimits.MaxPeers ||
            ids.Contains(0UL) || !ids.Contains(localId) || !ids.Contains(host))
            return null;
        return new LobbyContext(lobbyId, localId, host, ids);
    }
}