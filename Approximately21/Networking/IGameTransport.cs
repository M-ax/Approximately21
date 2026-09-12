using System;
using System.Collections.Generic;

namespace Approximately21.Networking;

public readonly record struct ReceivedPacket(ulong SenderId, byte[] Payload);

public interface IGameTransport : IDisposable
{
    void SetPeers(IReadOnlyCollection<ulong> peers);
    bool TrySend(ulong recipientId, byte[] payload);
    bool TryReceive(out ReceivedPacket packet);
}