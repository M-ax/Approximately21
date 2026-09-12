using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Steamworks;

namespace Approximately21.Networking;

public sealed class SteamGameTransport : IGameTransport
{
    private const int ReliableSend = 8;
    private readonly int _channel;
    private readonly HashSet<ulong> _peers = new();
    private readonly Il2CppStructArray<IntPtr> _receiveBuffer = new(1);
    private bool _disposed;

    public SteamGameTransport(int channel)
    {
        if (channel < 0)
            throw new ArgumentOutOfRangeException(nameof(channel));
        _channel = channel;
    }

    public void SetPeers(IReadOnlyCollection<ulong> peers)
    {
        ThrowIfDisposed();
        if (peers == null || peers.Count > NetworkLimits.MaxPeers || peers.Contains(0UL))
            throw new ArgumentException("Invalid lobby peers.", nameof(peers));
        foreach (var peerId in _peers.Except(peers).ToArray())
        {
            var identity = Identity(peerId);
            SteamNetworkingMessages.CloseChannelWithUser(ref identity, _channel);
            _peers.Remove(peerId);
        }
        foreach (var peerId in peers)
            _peers.Add(peerId);
    }

    public bool AcceptSessionRequest(ulong senderId)
    {
        ThrowIfDisposed();
        if (!_peers.Contains(senderId))
            return false;
        var identity = Identity(senderId);
        return SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
    }

    public bool TrySend(ulong recipientId, byte[] payload)
    {
        ThrowIfDisposed();
        if (!_peers.Contains(recipientId) || payload == null || payload.Length == 0 ||
            payload.Length > NetworkLimits.MaxMessageBytes)
            return false;
        var identity = Identity(recipientId);
        var pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            return SteamNetworkingMessages.SendMessageToUser(ref identity, pin.AddrOfPinnedObject(),
                (uint)payload.Length, ReliableSend, _channel) == EResult.k_EResultOK;
        }
        finally
        {
            pin.Free();
        }
    }

    public bool TryReceive(out ReceivedPacket packet)
    {
        ThrowIfDisposed();
        packet = default;
        _receiveBuffer[0] = IntPtr.Zero;
        if (SteamNetworkingMessages.ReceiveMessagesOnChannel(_channel, _receiveBuffer, 1) <= 0)
            return false;
        var pointer = _receiveBuffer[0];
        if (pointer == IntPtr.Zero)
            return true;
        try
        {
            var message = SteamNetworkingMessage_t.FromIntPtr(pointer);
            var senderId = message.m_identityPeer.GetSteamID64();
            if (!_peers.Contains(senderId) || message.m_nChannel != _channel || message.m_pData == IntPtr.Zero ||
                message.m_cbSize <= 0 || message.m_cbSize > NetworkLimits.MaxMessageBytes)
                return true;
            var payload = new byte[message.m_cbSize];
            Marshal.Copy(message.m_pData, payload, 0, payload.Length);
            packet = new ReceivedPacket(senderId, payload);
            return true;
        }
        finally
        {
            SteamNetworkingMessage_t.Release(pointer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        SetPeers(Array.Empty<ulong>());
        _disposed = true;
    }

    private static SteamNetworkingIdentity Identity(ulong steamId)
    {
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID64(steamId);
        return identity;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SteamGameTransport));
    }
}