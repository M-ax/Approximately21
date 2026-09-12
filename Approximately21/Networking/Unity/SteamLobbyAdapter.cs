using System;
using System.Collections.Generic;
using System.Linq;
using Steamworks;

namespace Approximately21.Networking.Unity;

public sealed class SteamLobbyAdapter : IDisposable
{
    private readonly SteamManager _steam;
    private readonly SteamGameTransport _transport;
    private readonly Callback<SteamNetworkingMessagesSessionRequest_t>.DispatchDelegate _dispatch;
    private readonly Callback<SteamNetworkingMessagesSessionRequest_t> _requests;
    private bool _disposed;

    public SteamLobbyAdapter(SteamManager steam, SteamGameTransport transport)
    {
        _steam = steam;
        _transport = transport;
        _dispatch = new Action<SteamNetworkingMessagesSessionRequest_t>(OnSessionRequest);
        _requests = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(_dispatch);
    }

    public LobbyContext ReadLobby()
    {
        if (_disposed || !SteamUser.BLoggedOn())
            return null;
        var lobby = _steam.GetLobby();
        var local = SteamUser.GetSteamID().m_SteamID;
        if (lobby.m_SteamID == 0 || local == 0)
            return null;
        ulong host;
        if (_steam.IsHost())
        {
            if (_steam._lobbyState != SteamManager.LobbyState.Created &&
                _steam._lobbyState != SteamManager.LobbyState.Connected)
                return null;
            host = local;
        }
        else
        {
            if (_steam._lobbyState != SteamManager.LobbyState.Connected ||
                !SteamNetworkingSockets.GetConnectionInfo(_steam._clientConnection, out var info) ||
                info.m_eState != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
                return null;
            host = info.m_identityRemote.GetSteamID64();
            if (host == local)
                return null;
        }
        var count = SteamMatchmaking.GetNumLobbyMembers(lobby);
        if (count <= 0 || count > NetworkLimits.MaxPeers)
            return null;
        var members = new List<ulong>(count);
        for (var i = 0; i < count; i++)
        {
            var member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
            if (_steam.IsHost() && member.m_SteamID != local)
            {
                var connections = _steam._steamToConn;
                if (!connections.IsCreated || !connections.TryGetValue(member, out var connection) ||
                    !SteamNetworkingSockets.GetConnectionInfo(connection, out var peer) ||
                    peer.m_eState != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected ||
                    peer.m_identityRemote.GetSteamID64() != member.m_SteamID)
                    continue;
            }
            members.Add(member.m_SteamID);
        }
        return BlackjackLobbyResolver.Resolve(lobby.m_SteamID, local, _steam.IsHost(), true, host, members);
    }

    private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t request)
    {
        if (_disposed)
            return;
        try
        {
            var sender = request.m_identityRemote.GetSteamID64();
            var lobby = ReadLobby();
            if (lobby != null && sender != lobby.LocalPlayerId && lobby.Members.Contains(sender))
                _transport.AcceptSessionRequest(sender);
        }
        catch (Exception exception)
        {
            Plugin.LogWarning($"Blackjack session request rejected: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _requests.Dispose();
    }
}