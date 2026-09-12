using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Approximately21.Networking;
using Xunit;

#nullable enable annotations

namespace Approximately21.Tests;

public sealed class GameStateServiceTests
{
    private const ulong LobbyId = 700;
    private const ulong HostId = 11;
    private const ulong ClientId = 22;
    private const string Alpha = "counter-a";
    private const string Beta = "counter-b";

    [Fact]
    public void CounterGameSynchronizesTwoTablesAndAttributesCommandsToTheirSenders()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.IsHost);
        Assert.True(harness.Host.IsReady);
        Assert.False(harness.Client.IsHost);
        Assert.True(harness.Client.IsReady);
        Assert.NotEqual(Guid.Empty, harness.Host.SessionEpoch);
        Assert.Equal(harness.Host.SessionEpoch, harness.Client.SessionEpoch);
        Assert.Equal(new[] { Alpha, Beta }, harness.Host.GetTableIds().OrderBy(id => id).ToArray());
        Assert.Equal(new[] { Alpha, Beta }, harness.Client.GetTableIds().OrderBy(id => id).ToArray());

        var changed = new List<string>();
        harness.Client.TableChanged += changed.Add;
        var alphaRevision = Table(harness.Client, Alpha).Revision;
        var betaRevision = Table(harness.Client, Beta).Revision;

        Assert.True(harness.Client.Add(Alpha, 3, out var firstCommandId));
        harness.Pump();
        Assert.True(harness.Client.Add(Beta, 7, out var secondCommandId));
        Assert.True(firstCommandId > 0);
        Assert.True(secondCommandId > firstCommandId);
        harness.Pump();

        AssertCounter(harness, Alpha, 3, ClientId, alphaRevision + 1);
        AssertCounter(harness, Beta, 7, ClientId, betaRevision + 1);
        Assert.Contains(Alpha, changed);
        Assert.Contains(Beta, changed);

        Assert.True(harness.Host.Add(Alpha, 2, out _));
        harness.Pump();
        AssertCounter(harness, Alpha, 5, HostId, alphaRevision + 2);
        AssertCounter(harness, Beta, 7, ClientId, betaRevision + 1);
    }

    [Fact]
    public void CounterProtocolRoundTripsPayloadAndRunsGameSpecificValidators()
    {
        using var harness = new Harness();
        Assert.True(harness.Client.Add(Alpha, 1, out _));
        harness.Pump();
        var protocol = harness.Protocol;
        Assert.True(protocol.TryDecode(harness.LastPacket(ClientId, GameMessageKind.Command).Payload,
            out var command));
        Assert.True(protocol.TryDecode(harness.LastPacket(HostId, GameMessageKind.Snapshot, Alpha).Payload,
            out var snapshot));
        Assert.Equal(CounterProtocol.ProtocolName, command.Protocol);
        Assert.Equal(1, command.ProtocolVersion);
        Assert.Equal(1, command.Delta);
        Assert.Equal(1, snapshot.State.Total);
        Assert.Equal(ClientId, snapshot.State.LastPlayerId);
        Assert.Equal(new[] { 1 }, snapshot.State.Deltas);

        var commandChecks = protocol.CommandValidationCalls;
        foreach (var delta in new[] { 1, 10 })
        {
            Assert.True(protocol.TryDecode(protocol.Encode(command with { Delta = delta }), out var decoded));
            Assert.Equal(delta, decoded.Delta);
        }
        foreach (var delta in new[] { 0, 11 })
        {
            Assert.False(protocol.TryDecode(ReplaceNumber(protocol.Encode(command), "Delta", delta), out _));
        }
        Assert.True(protocol.CommandValidationCalls > commandChecks);

        var snapshotChecks = protocol.SnapshotValidationCalls;
        foreach (var total in new[] { 0, 100 })
        {
            var valid = snapshot with { State = new CounterSnapshot { Total = total, Disconnects = 16 } };
            Assert.True(protocol.TryDecode(protocol.Encode(valid), out var decoded));
            Assert.Equal(total, decoded.State.Total);
        }
        foreach (var total in new[] { -1, 101 })
        {
            Assert.False(protocol.TryDecode(ReplaceNumber(protocol.Encode(snapshot), "Total", total), out _));
        }
        foreach (var disconnects in new[] { -1, 17 })
        {
            Assert.False(protocol.TryDecode(
                ReplaceNumber(protocol.Encode(snapshot), "Disconnects", disconnects), out _));
        }
        Assert.True(protocol.SnapshotValidationCalls > snapshotChecks);
    }

    [Fact]
    public void InvalidLocalDeltaDoesNotSendOrCreatePendingWork()
    {
        using var harness = new Harness();
        var completions = new List<CommandCompletion>();
        harness.Client.CommandCompleted += completions.Add;
        var checks = harness.Protocol.CommandValidationCalls;
        var revision = Table(harness.Client, Alpha).Revision;

        Assert.False(harness.Client.Add(Alpha, 0, out _));
        Assert.False(harness.Client.Add(Alpha, 11, out _));
        harness.Pump();
        harness.Now += TimeSpan.FromSeconds(3);
        harness.Pump();

        Assert.True(harness.Protocol.CommandValidationCalls > checks);
        Assert.Empty(harness.CommandPackets());
        Assert.Empty(completions);
        Assert.Equal(0, harness.Host.ApplyCalls);
        AssertCounter(harness, Alpha, 0, 0, revision);
    }

    [Fact]
    public void InvalidWireDeltaIsIgnoredWithoutPoisoningTheValidCommand()
    {
        using var harness = new Harness();
        harness.Bus.Drop = packet => harness.IsMessage(packet, ClientId, GameMessageKind.Command);
        Assert.True(harness.Client.Add(Alpha, 1, out _));
        harness.Pump();
        var request = Assert.Single(harness.CommandPackets());
        var revision = Table(harness.Host, Alpha).Revision;
        var checks = harness.Protocol.CommandValidationCalls;
        harness.Bus.Drop = null;

        harness.Bus.Inject(ClientId, HostId, ReplaceNumber(request.Payload, "Delta", 0));
        harness.Bus.Inject(ClientId, HostId, ReplaceNumber(request.Payload, "Delta", 11));
        harness.Pump();

        Assert.True(harness.Protocol.CommandValidationCalls > checks);
        Assert.Equal(0, harness.Host.ApplyCalls);
        AssertCounter(harness, Alpha, 0, 0, revision);

        harness.Bus.Inject(ClientId, HostId, request.Payload);
        harness.Pump();
        Assert.Equal(1, harness.Host.ApplyCalls);
        AssertCounter(harness, Alpha, 1, ClientId, revision + 1);
    }

    [Fact]
    public void DuplicateCounterCommandIsAppliedAndCompletedOnlyOnce()
    {
        using var harness = new Harness();
        var completions = new List<CommandCompletion>();
        harness.Client.CommandCompleted += completions.Add;
        var revision = Table(harness.Host, Alpha).Revision;
        Assert.True(harness.Client.Add(Alpha, 4, out _));
        harness.Pump();
        var request = Assert.Single(harness.CommandPackets());

        harness.Bus.Inject(ClientId, HostId, request.Payload);
        harness.Bus.Inject(ClientId, HostId, request.Payload);
        harness.Pump();

        Assert.Equal(1, harness.Host.ApplyCalls);
        Assert.Single(completions);
        AssertCounter(harness, Alpha, 4, ClientId, revision + 1);
    }

    [Fact]
    public void LostCounterCommandRetriesAfterTwoSecondsWithTheSameIdentity()
    {
        using var harness = new Harness();
        var completions = new List<CommandCompletion>();
        harness.Client.CommandCompleted += completions.Add;
        var revision = Table(harness.Host, Alpha).Revision;
        harness.Bus.Drop = packet => harness.IsMessage(packet, ClientId, GameMessageKind.Command);
        Assert.True(harness.Client.Add(Alpha, 4, out _));
        harness.Pump();
        var first = Assert.Single(harness.CommandPackets());
        Assert.Equal(0, harness.Host.ApplyCalls);
        Assert.Empty(completions);

        harness.Now += TimeSpan.FromMilliseconds(1900);
        harness.Pump();
        Assert.Single(harness.CommandPackets());

        harness.Bus.Drop = null;
        harness.Now += TimeSpan.FromMilliseconds(200);
        harness.Pump();
        var attempts = harness.CommandPackets();
        Assert.Equal(2, attempts.Length);
        Assert.True(harness.Protocol.TryDecode(first.Payload, out var original));
        Assert.True(harness.Protocol.TryDecode(attempts[1].Payload, out var retry));
        Assert.Equal(original.CommandId, retry.CommandId);
        Assert.Equal(original.ClientSessionId, retry.ClientSessionId);
        Assert.Equal(original.SessionEpoch, retry.SessionEpoch);
        Assert.Equal(original.TableId, retry.TableId);
        Assert.Equal(original.Delta, retry.Delta);
        Assert.Equal(1, harness.Host.ApplyCalls);
        Assert.Single(completions);
        AssertCounter(harness, Alpha, 4, ClientId, revision + 1);

        harness.Now += TimeSpan.FromSeconds(3);
        harness.Pump();
        Assert.Equal(2, harness.CommandPackets().Length);
    }

    [Fact]
    public void DepartedMembersUseCounterDisconnectHooksAndResetClearsClientTables()
    {
        using var harness = new Harness();
        Assert.True(harness.Client.Add(Alpha, 2, out _));
        harness.Pump();
        Assert.True(harness.Client.Add(Beta, 3, out _));
        harness.Pump();
        var alphaRevision = Table(harness.Host, Alpha).Revision;
        var betaRevision = Table(harness.Host, Beta).Revision;
        var remainingLobby = new LobbyContext(LobbyId, HostId, HostId, new[] { HostId });

        harness.Host.SetLobby(remainingLobby);
        harness.Host.Pump();
        Assert.Equal(new[] { ClientId, ClientId }, harness.Host.DisconnectedPlayers.ToArray());
        Assert.Equal(1, Table(harness.Host, Alpha).State.Disconnects);
        Assert.Equal(1, Table(harness.Host, Beta).State.Disconnects);
        Assert.Equal(2, Table(harness.Host, Alpha).State.Total);
        Assert.Equal(3, Table(harness.Host, Beta).State.Total);
        Assert.Equal(0UL, Table(harness.Host, Alpha).State.LastPlayerId);
        Assert.Equal(alphaRevision + 1, Table(harness.Host, Alpha).Revision);
        Assert.Equal(betaRevision + 1, Table(harness.Host, Beta).Revision);

        harness.Host.SetLobby(remainingLobby);
        Assert.Equal(2, harness.Host.DisconnectedPlayers.Count);

        var resets = 0;
        harness.Client.SessionReset += () => resets++;
        harness.Client.ResetSession();
        Assert.Equal(1, resets);
        Assert.False(harness.Client.IsReady);
        Assert.Empty(harness.Client.GetTableIds());
        Assert.False(harness.Client.Add(Alpha, 1, out _));
    }

    [Fact]
    public void RemovedCounterTableCannotBeResurrectedByADelayedSnapshot()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Add(Alpha, 1, out _));
        harness.Pump();
        var delayed = harness.LastPacket(HostId, GameMessageKind.Snapshot, Alpha);

        harness.Host.RemoveTable(Alpha);
        harness.Pump();
        Assert.False(harness.Host.TryGetTable(Alpha, out _));
        Assert.False(harness.Client.TryGetTable(Alpha, out _));
        Assert.True(harness.Client.TryGetTable(Beta, out _));

        harness.Bus.Inject(HostId, ClientId, delayed.Payload);
        harness.Pump();
        Assert.False(harness.Client.TryGetTable(Alpha, out _));
        Assert.False(harness.Client.Add(Alpha, 1, out _));
        Assert.Equal(new[] { Beta }, harness.Client.GetTableIds().ToArray());
    }

    [Fact]
    public void ForeignProtocolAndVersionAreRejectedEvenWhenTheSenderIsTheHost()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Add(Alpha, 1, out _));
        harness.Pump();
        var before = Table(harness.Client, Alpha);
        var packet = harness.LastPacket(HostId, GameMessageKind.Snapshot, Alpha);
        Assert.True(harness.Protocol.TryDecode(packet.Payload, out var snapshot));
        var foreign = new CounterProtocol("other-counter-game");
        var foreignMessage = snapshot with
        {
            Protocol = foreign.Name,
            Revision = snapshot.Revision + 50,
            State = new CounterSnapshot { Total = 99, LastPlayerId = HostId }
        };
        var foreignPayload = foreign.Encode(foreignMessage);
        Assert.True(foreign.TryDecode(foreignPayload, out _));
        Assert.False(harness.Protocol.TryDecode(foreignPayload, out _));
        Assert.False(foreign.TryDecode(packet.Payload, out _));
        var wrongVersion = ReplaceNumber(harness.Protocol.Encode(foreignMessage with
        {
            Protocol = harness.Protocol.Name
        }), "ProtocolVersion", 2);
        Assert.False(harness.Protocol.TryDecode(wrongVersion, out _));

        harness.Bus.Inject(HostId, ClientId, foreignPayload);
        harness.Bus.Inject(HostId, ClientId, wrongVersion);
        harness.Client.Pump();
        Assert.Equal(before.Revision, Table(harness.Client, Alpha).Revision);
        Assert.Equal(before.State.Total, Table(harness.Client, Alpha).State.Total);

        Assert.True(harness.Host.Add(Alpha, 2, out _));
        harness.Pump();
        AssertCounter(harness, Alpha, 3, HostId, before.Revision + 1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReturnedCounterSnapshotsAreDetachedIncludingNestedData(bool readHost)
    {
        using var harness = new Harness();
        Assert.True(harness.Client.Add(Alpha, 1, out _));
        harness.Pump();
        var service = readHost ? harness.Host : harness.Client;
        var first = Table(service, Alpha);
        var second = Table(service, Alpha);
        Assert.NotSame(first.State, second.State);
        Assert.NotSame(first.State.Deltas, second.State.Deltas);

        first.State.Deltas[0] = 99;
        Assert.Equal(new[] { 1 }, second.State.Deltas);
        Assert.Equal(new[] { 1 }, Table(harness.Host, Alpha).State.Deltas);
        Assert.Equal(new[] { 1 }, Table(harness.Client, Alpha).State.Deltas);

        Assert.True(harness.Host.Add(Alpha, 2, out _));
        harness.Pump();
        Assert.Equal(3, Table(harness.Host, Alpha).State.Total);
        Assert.Equal(new[] { 1, 2 }, Table(harness.Host, Alpha).State.Deltas);
        Assert.Equal(new[] { 1, 2 }, Table(harness.Client, Alpha).State.Deltas);
        Assert.Equal(new[] { 1 }, second.State.Deltas);
    }

    private static ReplicatedTable<CounterSnapshot> Table(CounterService service, string tableId)
    {
        Assert.True(service.TryGetTable(tableId, out var table));
        return table;
    }

    private static void AssertCounter(Harness harness, string tableId, int total, ulong playerId, long revision)
    {
        foreach (var service in new[] { harness.Host, harness.Client })
        {
            var table = Table(service, tableId);
            Assert.Equal(tableId, table.TableId);
            Assert.Equal(total, table.State.Total);
            Assert.Equal(playerId, table.State.LastPlayerId);
            Assert.Equal(revision, table.Revision);
        }
    }

    private static byte[] ReplaceNumber(byte[] payload, string propertyName, int value)
    {
        using var document = JsonDocument.Parse(payload);
        using var stream = new MemoryStream();
        var replacements = 0;
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteObject(document.RootElement, writer);
        }
        Assert.Equal(1, replacements);
        return stream.ToArray();

        void WriteObject(JsonElement element, Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteNumberValue(value);
                    replacements++;
                }
                else if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    WriteObject(property.Value, writer);
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
    }

    public sealed record CounterMessage : GameMessage<CounterSnapshot>
    {
        public CounterMessage()
        {
            Protocol = CounterProtocol.ProtocolName;
            ProtocolVersion = 1;
        }

        public int Delta { get; init; }
    }

    public sealed class CounterSnapshot
    {
        public int Total { get; init; }
        public ulong LastPlayerId { get; init; }
        public int Disconnects { get; init; }
        public int[] Deltas { get; init; } = Array.Empty<int>();
    }

    private sealed class CounterProtocol : GameProtocol<CounterMessage, CounterSnapshot>
    {
        public const string ProtocolName = "test-counter";
        public int CommandValidationCalls { get; private set; }
        public int SnapshotValidationCalls { get; private set; }

        public CounterProtocol(string name = ProtocolName) : base(name, 1)
        {
        }

        public override bool IsCommandValid(CounterMessage message)
        {
            CommandValidationCalls++;
            return message.Delta >= 1 && message.Delta <= 10;
        }

        protected override bool IsSnapshotValid(CounterSnapshot snapshot)
        {
            SnapshotValidationCalls++;
            return snapshot.Total >= 0 && snapshot.Total <= 100
                && snapshot.Disconnects >= 0 && snapshot.Disconnects <= 16
                && snapshot.Deltas != null && snapshot.Deltas.Length <= 100
                && snapshot.Deltas.All(delta => delta >= 1 && delta <= 10);
        }
    }

    private sealed class CounterGame
    {
        public int Total { get; set; }
        public ulong LastPlayerId { get; set; }
        public int Disconnects { get; set; }
        public List<int> Deltas { get; } = new();
        public HashSet<ulong> Players { get; } = new();
    }

    private sealed class CounterService : GameStateService<CounterGame, CounterMessage, CounterSnapshot>
    {
        public int ApplyCalls { get; private set; }
        public List<ulong> DisconnectedPlayers { get; } = new();

        public CounterService(IGameTransport transport, CounterProtocol protocol, Func<DateTimeOffset> clock)
            : base(transport, protocol, () => new CounterGame(), clock)
        {
        }

        public bool Add(string tableId, int delta, out long commandId)
        {
            return TrySubmit(tableId, new CounterMessage { Delta = delta }, out commandId);
        }

        protected override bool TryApply(CounterGame game, ulong playerId, CounterMessage command, out string error)
        {
            ApplyCalls++;
            if (command.Delta < 1 || command.Delta > 10 || game.Total + command.Delta > 100)
            {
                error = "Counter limit exceeded.";
                return false;
            }

            game.Total += command.Delta;
            game.LastPlayerId = playerId;
            game.Deltas.Add(command.Delta);
            game.Players.Add(playerId);
            error = string.Empty;
            return true;
        }

        protected override bool DisconnectPlayer(CounterGame game, ulong playerId)
        {
            DisconnectedPlayers.Add(playerId);
            if (!game.Players.Remove(playerId))
                return false;

            game.Disconnects++;
            if (game.LastPlayerId == playerId)
                game.LastPlayerId = 0;
            return true;
        }

        protected override CounterSnapshot CreateSnapshot(CounterGame game)
        {
            return new CounterSnapshot
            {
                Total = game.Total,
                LastPlayerId = game.LastPlayerId,
                Disconnects = game.Disconnects,
                Deltas = game.Deltas.ToArray()
            };
        }
    }

    private sealed class Harness : IDisposable
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public CounterProtocol Protocol { get; } = new();
        public MemoryBus Bus { get; } = new();
        public CounterService Host { get; }
        public CounterService Client { get; }

        public Harness()
        {
            Host = new CounterService(Bus.Connect(HostId), Protocol, () => Now);
            Client = new CounterService(Bus.Connect(ClientId), Protocol, () => Now);
            Host.SetLobby(new LobbyContext(LobbyId, HostId, HostId, new[] { HostId, ClientId }));
            Host.RegisterTable(Alpha);
            Host.RegisterTable(Beta);
            Client.SetLobby(new LobbyContext(LobbyId, ClientId, HostId, new[] { HostId, ClientId }));
            Pump();
            Assert.True(Client.IsReady);
            Assert.True(Client.TryGetTable(Alpha, out _));
            Assert.True(Client.TryGetTable(Beta, out _));
        }

        public void Pump()
        {
            for (var i = 0; i < 8; i++)
            {
                Host.Pump();
                Client.Pump();
            }
        }

        public bool IsMessage(Transmission packet, ulong senderId, GameMessageKind kind, string? tableId = null)
        {
            return packet.SenderId == senderId && Protocol.TryDecode(packet.Payload, out var message)
                && message.Kind == kind && (tableId == null || message.TableId == tableId);
        }

        public Transmission[] CommandPackets()
        {
            return Bus.Sent.Where(packet => IsMessage(packet, ClientId, GameMessageKind.Command)).ToArray();
        }

        public Transmission LastPacket(ulong senderId, GameMessageKind kind, string? tableId = null)
        {
            return Bus.Sent.Last(packet => IsMessage(packet, senderId, kind, tableId));
        }

        public void Dispose()
        {
            Client.Dispose();
            Host.Dispose();
        }
    }

    private sealed record Transmission(ulong SenderId, ulong RecipientId, byte[] Payload);

    private sealed class MemoryBus
    {
        private readonly Dictionary<ulong, MemoryTransport> transports = new();
        public List<Transmission> Sent { get; } = new();
        public Func<Transmission, bool>? Drop { get; set; }

        public IGameTransport Connect(ulong playerId)
        {
            var transport = new MemoryTransport(this, playerId);
            transports.Add(playerId, transport);
            return transport;
        }

        public void Inject(ulong senderId, ulong recipientId, byte[] payload)
        {
            transports[recipientId].Incoming.Enqueue(new ReceivedPacket(senderId, (byte[])payload.Clone()));
        }

        private bool Send(ulong senderId, ulong recipientId, byte[] payload)
        {
            if (!transports.TryGetValue(recipientId, out var recipient) || recipient.Disposed)
                return false;

            var packet = new Transmission(senderId, recipientId, (byte[])payload.Clone());
            Sent.Add(packet);
            if (Drop?.Invoke(packet) != true)
                Inject(senderId, recipientId, packet.Payload);
            return true;
        }

        private sealed class MemoryTransport : IGameTransport
        {
            private readonly MemoryBus bus;
            private readonly ulong playerId;
            private readonly HashSet<ulong> peers = new();
            public Queue<ReceivedPacket> Incoming { get; } = new();
            public bool Disposed { get; private set; }

            public MemoryTransport(MemoryBus bus, ulong playerId)
            {
                this.bus = bus;
                this.playerId = playerId;
            }

            public void SetPeers(IReadOnlyCollection<ulong> playerIds)
            {
                peers.Clear();
                peers.UnionWith(playerIds);
            }

            public bool TrySend(ulong recipientId, byte[] payload)
            {
                return !Disposed && peers.Contains(recipientId) && bus.Send(playerId, recipientId, payload);
            }

            public bool TryReceive(out ReceivedPacket packet)
            {
                if (!Disposed && Incoming.Count > 0)
                {
                    packet = Incoming.Dequeue();
                    return true;
                }

                packet = default!;
                return false;
            }

            public void Dispose()
            {
                Disposed = true;
                peers.Clear();
                Incoming.Clear();
            }
        }
    }
}
