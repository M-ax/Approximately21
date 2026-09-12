using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Approximately21.Blackjack;
using Approximately21.Networking;
using Xunit;

namespace Approximately21.Tests;

public sealed class NetworkingTests
{
    [Fact]
    public void EntireRoundRunsThroughCommandsWithoutLeakingDealerHoleCardOrDeck()
    {
        var firstCards = new[]
        {
            new Card { Rank = 10, Suit = Suit.Clubs },
            new Card { Rank = 9, Suit = Suit.Clubs },
            new Card { Rank = 7, Suit = Suit.Clubs },
            new Card { Rank = 8, Suit = Suit.Clubs },
            new Card { Rank = 2, Suit = Suit.Clubs }
        };
        var deck = firstCards.Concat(BlackjackDeck.CreateOrdered().Where(card => !firstCards.Contains(card))).ToArray();
        using var rig = new Rig(() => new BlackjackGameState(new BlackjackOptions(), () => deck));
        rig.Submit(rig.Client, "table", BlackjackAction.Join, 0);
        rig.Submit(rig.Client, "table", BlackjackAction.Bet, 0, 10);
        rig.Submit(rig.Client, "table", BlackjackAction.Deal, 0);
        var playing = rig.State(rig.Client);
        Assert.Equal(BlackjackPhase.PlayerTurns, playing.Phase);
        Assert.True(playing.DealerHoleCardHidden);
        Assert.Equal(9, Assert.Single(playing.DealerCards).Rank);
        var payload = rig.HostWire.Sent.Last(p => Decode(p.Payload).Kind == GameMessageKind.Snapshot).Payload;
        var json = Encoding.UTF8.GetString(payload);
        Assert.DoesNotContain("\"Rank\":8", json);
        Assert.DoesNotContain("\"Deck\"", json);
        Assert.DoesNotContain("\"Score\"", json);
        Assert.DoesNotContain("\"Value\"", json);
        rig.Submit(rig.Client, "table", BlackjackAction.Hit, 0);
        rig.Submit(rig.Client, "table", BlackjackAction.Stand, 0);
        var complete = rig.State(rig.Client);
        Assert.Equal(BlackjackPhase.RoundComplete, complete.Phase);
        Assert.False(complete.DealerHoleCardHidden);
        Assert.Equal(2, complete.DealerCards.Length);
        Assert.Equal(1010m, Assert.Single(complete.Players).Balance);
        Assert.Equal(HandOutcome.Win, Assert.Single(complete.Players[0].Hands).Outcome);
        Assert.Equal(rig.State(rig.Host).Players[0].Balance, complete.Players[0].Balance);
        Assert.All(rig.Completions, completion => Assert.True(completion.Accepted, completion.Error));
    }

    [Fact]
    public void HostAndClientsShareStateButTablesRemainIndependent()
    {
        using var rig = new Rig();
        Assert.True(rig.Host.RegisterTable("second"));
        rig.Flush();
        rig.Submit(rig.Client, "table", BlackjackAction.Join, 0);
        Assert.Equal(2UL, Assert.Single(rig.State(rig.Host).Players).PlayerId);
        Assert.Equal(2UL, Assert.Single(rig.State(rig.Client).Players).PlayerId);
        Assert.Empty(rig.State(rig.Client, "second").Players);
        rig.Submit(rig.Host, "second", BlackjackAction.Join, 1);
        Assert.Equal(1UL, Assert.Single(rig.State(rig.Client, "second").Players).PlayerId);
    }

    [Fact]
    public void DuplicateCommandsMutateExactlyOnceAndReturnSameResult()
    {
        using var rig = new Rig();
        Assert.True(rig.Client.TrySubmit("table", BlackjackAction.Join, 0, 0, out _));
        var command = rig.ClientWire.Sent.Last();
        rig.Bus.Inject(1, 2, command.Payload);
        rig.Flush();
        Assert.Single(rig.State(rig.Host).Players);
        Assert.True(rig.Host.TryGetTable("table", out var table));
        Assert.Equal(2, table.Revision);
        Assert.Single(rig.Completions);
        Assert.True(rig.Completions[0].Accepted);
    }

    [Fact]
    public void StaleCommandsCannotExecuteAfterReconnectOrSessionReplay()
    {
        using var rig = new Rig();
        var sync = rig.ClientWire.Sent.First().Payload;
        rig.Submit(rig.Client, "table", BlackjackAction.Join, 0);
        var command = rig.ClientWire.Sent.First(p => Decode(p.Payload).Kind == GameMessageKind.Command).Payload;
        rig.Submit(rig.Client, "table", BlackjackAction.Bet, 0, 10);
        rig.Now += TimeSpan.FromSeconds(3);
        rig.Bus.Inject(1, 2, sync);
        rig.Bus.Inject(1, 2, command);
        rig.Flush();
        Assert.True(rig.Host.TryGetTable("table", out var table));
        Assert.Equal(3, table.Revision);
        Assert.Equal(10m, Assert.Single(table.State.Players).Bet);
    }

    [Fact]
    public void HostValidatesSeatOwnershipAndConcurrentRevision()
    {
        using var rig = new Rig();
        Assert.True(rig.Client.TrySubmit("table", BlackjackAction.Join, 0, 0, out _));
        rig.Submit(rig.Host, "table", BlackjackAction.Join, 1);
        Assert.Single(rig.Completions);
        Assert.False(rig.Completions[0].Accepted);
        rig.Submit(rig.Client, "table", BlackjackAction.Join, 0);
        rig.Submit(rig.Client, "table", BlackjackAction.Bet, 1, 10);
        Assert.False(rig.Completions.Last().Accepted);
        Assert.All(rig.State(rig.Host).Players, player => Assert.Equal(0m, player.Bet));
    }

    [Fact]
    public void LateJoinAndReconnectReceiveSnapshotsWithoutAnyLocalTableEntities()
    {
        using var rig = new Rig();
        rig.Submit(rig.Host, "table", BlackjackAction.Join, 0);
        rig.Client.SetLobby(null);
        Assert.Empty(rig.Client.GetTableIds());
        rig.Now += TimeSpan.FromSeconds(3);
        rig.Client.SetLobby(rig.Context(2));
        rig.Flush();
        Assert.Equal(1UL, Assert.Single(rig.State(rig.Client).Players).PlayerId);
        Assert.Equal(rig.Host.SessionEpoch, rig.Client.SessionEpoch);
    }

    [Fact]
    public void RemovedTableCannotBeResurrectedByAnOldSnapshot()
    {
        using var rig = new Rig();
        var oldSnapshot = rig.HostWire.Sent.First(p => Decode(p.Payload).Kind == GameMessageKind.Snapshot).Payload;
        Assert.True(rig.Host.RemoveTable("table"));
        rig.Flush();
        rig.Bus.Inject(2, 1, oldSnapshot);
        rig.Flush();
        Assert.False(rig.Client.TryGetTable("table", out _));
        Assert.False(rig.Host.RegisterTable("table"));
    }

    [Fact]
    public void NonHostSnapshotsAndNonmemberCommandsAreIgnored()
    {
        using var rig = new Rig();
        var snapshot = new BlackjackMessage
        {
            LobbyId = 10, SessionEpoch = rig.Host.SessionEpoch, Kind = GameMessageKind.Snapshot,
            TableId = "forged", Revision = 100, State = new BlackjackGameState().CreateSnapshot()
        };
        rig.Bus.Inject(2, 3, BlackjackProtocol.Encode(snapshot));
        rig.Bus.Inject(2, 999, BlackjackProtocol.Encode(snapshot));
        Assert.True(rig.Client.TrySubmit("table", BlackjackAction.Join, 0, 0, out _));
        var command = rig.ClientWire.Sent.Last().Payload;
        rig.Bus.Queues[1].Clear();
        rig.Bus.Inject(1, 999, command);
        rig.Flush();
        Assert.False(rig.Client.TryGetTable("forged", out _));
        Assert.Empty(rig.State(rig.Host).Players);
    }

    [Fact]
    public void SessionResetRejectsOldCommandsAndOldEpochSnapshots()
    {
        using var rig = new Rig();
        var oldSnapshot = rig.HostWire.Sent.First(p => Decode(p.Payload).Kind == GameMessageKind.Snapshot).Payload;
        rig.Host.ResetSession();
        rig.Now += TimeSpan.FromSeconds(31);
        rig.Client.Pump();
        rig.Flush();
        Assert.Equal(rig.Host.SessionEpoch, rig.Client.SessionEpoch);
        Assert.Empty(rig.Client.GetTableIds());
        rig.Bus.Inject(2, 1, oldSnapshot);
        rig.Flush();
        Assert.Empty(rig.Client.GetTableIds());
    }

    [Fact]
    public void HostDepartureResetsInsteadOfMigratingPrivateGameState()
    {
        using var rig = new Rig();
        rig.Client.SetLobby(new LobbyContext(10, 2, 2, new ulong[] { 2, 3 }));
        Assert.True(rig.Client.IsHost);
        Assert.Empty(rig.Client.GetTableIds());
        Assert.NotEqual(rig.Host.SessionEpoch, rig.Client.SessionEpoch);
        Assert.True(rig.Client.RegisterTable("table"));
    }

    [Fact]
    public void DisconnectionIsPublishedAndRefundsUnplayedWagers()
    {
        using var rig = new Rig();
        rig.Submit(rig.Client, "table", BlackjackAction.Join, 0);
        rig.Submit(rig.Client, "table", BlackjackAction.Bet, 0, 10);
        rig.Host.SetLobby(new LobbyContext(10, 1, 1, new ulong[] { 1, 3 }));
        var player = rig.State(rig.Host).Players.SingleOrDefault();
        Assert.True(player == null || !player.IsConnected);
        if (player != null)
            Assert.Equal(0m, player.Bet);
    }

    [Fact]
    public void ReturningLobbyMemberCanReclaimTheirSeatAndBalance()
    {
        using var rig = new Rig();
        rig.Submit(rig.Client, "table", BlackjackAction.Join, 0);
        var balance = Assert.Single(rig.State(rig.Host).Players).Balance;
        rig.Submit(rig.Client, "table", BlackjackAction.Bet, 0, 10);
        rig.Host.SetLobby(new LobbyContext(10, 1, 1, new ulong[] { 1, 3 }));
        rig.Client.SetLobby(null);
        rig.Host.SetLobby(rig.Context(1));
        rig.Client.SetLobby(rig.Context(2));
        rig.Flush();
        rig.Submit(rig.Client, "table", BlackjackAction.Join, 0);
        var player = Assert.Single(rig.State(rig.Host).Players);
        Assert.True(player.IsConnected);
        Assert.Equal(balance, player.Balance);
        Assert.True(rig.Completions.Last().Accepted);
    }

    [Fact]
    public void ReturnedSnapshotsCannotMutateAuthoritativeStateOrCachedReplicas()
    {
        using var rig = new Rig();
        rig.Submit(rig.Client, "table", BlackjackAction.Join, 0);
        rig.State(rig.Client).Players[0] = null;
        Assert.NotNull(Assert.Single(rig.State(rig.Client).Players));
        Assert.NotNull(Assert.Single(rig.State(rig.Host).Players));
    }

    [Fact]
    public void FailedSendsAndLostResultsAreRetriedWithoutDuplicateMutation()
    {
        using var rig = new Rig();
        rig.ClientWire.FailSends = true;
        Assert.True(rig.Client.TrySubmit("table", BlackjackAction.Join, 0, 0, out _));
        Assert.False(rig.Client.TrySubmit("table", BlackjackAction.Join, 1, 0, out _));
        rig.ClientWire.FailSends = false;
        rig.Now += TimeSpan.FromSeconds(3);
        rig.Client.Pump();
        rig.Host.Pump();
        rig.Bus.Queues[2].Clear();
        rig.Now += TimeSpan.FromSeconds(3);
        rig.Client.Pump();
        rig.Flush();
        Assert.True(Assert.Single(rig.Completions).Accepted);
        Assert.Single(rig.State(rig.Host).Players);
        Assert.Single(rig.State(rig.Client).Players);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"Protocol\":\"Approximately21.Blackjack\",\"ProtocolVersion\":\"1\"}")]
    [InlineData("{\"Protocol\":\"Approximately21.Blackjack\",\"ProtocolVersion\":2}")]
    public void MalformedMessagesDoNotThrow(string json)
    {
        Assert.False(BlackjackProtocol.TryDecode(Encoding.UTF8.GetBytes(json), out _));
    }

    [Fact]
    public void VersionTwoCommandsKeepFlatWireContractAndRejectVersionOne()
    {
        var json = "{\"Protocol\":\"Approximately21.Blackjack\",\"ProtocolVersion\":1," +
                   "\"LobbyId\":10,\"SessionEpoch\":\"11111111-1111-1111-1111-111111111111\"," +
                   "\"ClientSessionId\":\"22222222-2222-2222-2222-222222222222\"," +
                   "\"Kind\":2,\"TableId\":\"table\",\"Revision\":3,\"CommandId\":4," +
                   "\"Action\":2,\"SeatIndex\":1,\"Amount\":12.5}";
        Assert.False(BlackjackProtocol.TryDecode(Encoding.UTF8.GetBytes(json), out _));
        json = json.Replace("\"ProtocolVersion\":1", "\"ProtocolVersion\":2");
        Assert.True(BlackjackProtocol.TryDecode(Encoding.UTF8.GetBytes(json), out var command));
        Assert.Equal(GameMessageKind.Command, command.Kind);
        Assert.Equal(BlackjackAction.Bet, command.Action);
        Assert.Equal(1, command.SeatIndex);
        Assert.Equal(12.5m, command.Amount);
        using var encoded = JsonDocument.Parse(BlackjackProtocol.Encode(command));
        var root = encoded.RootElement;
        Assert.Equal("Approximately21.Blackjack", root.GetProperty("Protocol").GetString());
        Assert.Equal(2, root.GetProperty("ProtocolVersion").GetInt32());
        Assert.Equal(2, root.GetProperty("Kind").GetInt32());
        Assert.Equal(2, root.GetProperty("Action").GetInt32());
        Assert.Equal(1, root.GetProperty("SeatIndex").GetInt32());
        Assert.Equal(12.5m, root.GetProperty("Amount").GetDecimal());
        Assert.False(root.TryGetProperty("Command", out _));
        Assert.False(root.TryGetProperty("State", out _));
    }

    [Fact]
    public void ProtocolBoundsPayloadAndRejectsInvalidStateBeforeApplying()
    {
        using var rig = new Rig();
        Assert.False(BlackjackProtocol.TryDecode(new byte[BlackjackProtocol.MaxMessageBytes + 1], out _));
        var valid = rig.HostWire.Sent.First(p => Decode(p.Payload).Kind == GameMessageKind.Snapshot).Payload;
        var json = Encoding.UTF8.GetString(valid).Replace("\"Players\":[]", "\"Players\":[null]");
        Assert.NotEqual(Encoding.UTF8.GetString(valid), json);
        Assert.False(BlackjackProtocol.TryDecode(Encoding.UTF8.GetBytes(json), out _));
        rig.Bus.Inject(2, 1, Encoding.UTF8.GetBytes(json));
        rig.Flush();
        Assert.Empty(rig.State(rig.Client).Players);
    }

    [Fact]
    public void TablesAndPeerListsAreBounded()
    {
        using var rig = new Rig();
        Assert.Throws<ArgumentException>(() => new LobbyContext(10, 1, 1, Enumerable.Range(1, 65).Select(i => (ulong)i)));
        for (var i = 1; i < BlackjackProtocol.MaxTablesPerSession; i++)
            Assert.True(rig.Host.RegisterTable("table" + i));
        Assert.False(rig.Host.RegisterTable("overflow"));
        Assert.False(rig.Client.RegisterTable("unauthorized"));
    }

    [Fact]
    public void AuthoritativeWagerLimitsRoundTripAndAllowLargeSupportedBets()
    {
        using var rig = new Rig(() => new BlackjackGameState(new BlackjackOptions
        {
            InitialBalance = 10000000m, MinimumBet = 0.01m, MaximumBet = 2000000.25m
        }));
        Assert.Equal(0.01m, rig.State(rig.Client).MinimumBet);
        Assert.Equal(2000000.25m, rig.State(rig.Client).MaximumBet);
        rig.Submit(rig.Client, "table", BlackjackAction.Join, 4);
        rig.Submit(rig.Client, "table", BlackjackAction.Bet, 4, 2000000.25m);
        Assert.True(rig.Completions.Last().Accepted);
        Assert.Equal(2000000.25m, rig.State(rig.Client).Players[0].Bet);
        rig.Submit(rig.Client, "table", BlackjackAction.Bet, 4, 2000000.26m);
        Assert.False(rig.Completions.Last().Accepted);
        Assert.Equal(2000000.25m, rig.State(rig.Client).Players[0].Bet);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    public void CommandSeatBoundsAreFive(int seat, bool accepted)
    {
        using var rig = new Rig();
        var template = Decode(rig.HostWire.Sent.First(p => Decode(p.Payload).Kind == GameMessageKind.Snapshot).Payload);
        var command = template with
        {
            Kind = GameMessageKind.Command, State = null, ClientSessionId = Guid.NewGuid(),
            CommandId = 1, Action = BlackjackAction.Join, SeatIndex = 0
        };
        var json = Encoding.UTF8.GetString(BlackjackProtocol.Encode(command))
            .Replace("\"SeatIndex\":0", "\"SeatIndex\":" + seat);
        Assert.Equal(accepted, BlackjackProtocol.TryDecode(Encoding.UTF8.GetBytes(json), out _));
        Assert.Equal(accepted, rig.Client.TrySubmit("table", BlackjackAction.Join, seat, 0, out _));
    }

    [Fact]
    public void CommandAmountsUseCentPrecisionAndSharedCurrencyBound()
    {
        foreach (var amount in new[] { 0.01m, 1000000.01m, BlackjackLimits.MaxCurrency })
            Assert.True(BlackjackProtocol.Instance.IsCommandValid(new BlackjackMessage { Action = BlackjackAction.Bet, Amount = amount }));
        foreach (var amount in new[] { -1m, 0m, 0.001m, BlackjackLimits.MaxCurrency + 0.01m, decimal.MaxValue })
            Assert.False(BlackjackProtocol.Instance.IsCommandValid(new BlackjackMessage { Action = BlackjackAction.Bet, Amount = amount }));
        foreach (var action in Enum.GetValues<BlackjackAction>().Where(action => action != BlackjackAction.Bet))
        {
            Assert.True(BlackjackProtocol.Instance.IsCommandValid(new BlackjackMessage { Action = action }));
            Assert.False(BlackjackProtocol.Instance.IsCommandValid(new BlackjackMessage { Action = action, Amount = 1m }));
        }
    }

    [Fact]
    public void SnapshotWireRejectsMissingMalformedLimitsAndVersionOne()
    {
        using var rig = new Rig();
        var payload = rig.HostWire.Sent.First(p => Decode(p.Payload).Kind == GameMessageKind.Snapshot).Payload;
        foreach (var field in new[] { "MinimumBet", "MaximumBet" })
        {
            foreach (var value in new[] { -1m, 0m, 0.001m, BlackjackLimits.MaxCurrency + 1, decimal.MaxValue })
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(payload);
                json["State"][field] = value;
                Assert.False(BlackjackProtocol.TryDecode(Encoding.UTF8.GetBytes(json.ToJsonString()), out _));
            }
            var missing = System.Text.Json.Nodes.JsonNode.Parse(payload);
            missing["State"].AsObject().Remove(field);
            Assert.False(BlackjackProtocol.TryDecode(Encoding.UTF8.GetBytes(missing.ToJsonString()), out _));
        }
        var reversed = System.Text.Json.Nodes.JsonNode.Parse(payload);
        reversed["State"]["MinimumBet"] = 501m;
        Assert.False(BlackjackProtocol.TryDecode(Encoding.UTF8.GetBytes(reversed.ToJsonString()), out _));
        var legacy = Encoding.UTF8.GetString(payload).Replace("\"ProtocolVersion\":2", "\"ProtocolVersion\":1");
        Assert.False(BlackjackProtocol.TryDecode(Encoding.UTF8.GetBytes(legacy), out _));
    }

    [Fact]
    public void FivePlayerSnapshotsRoundTripButSixPlayersAndOutOfRangeSeatsDoNot()
    {
        using var rig = new Rig();
        var game = new BlackjackGameState();
        for (var seat = 0; seat < 5; seat++)
            Assert.True(game.TryApply((ulong)seat + 1, BlackjackAction.Join, seat, 0, out _));
        Assert.Equal(5, BlackjackLimits.MaxSeats);
        var template = Decode(rig.HostWire.Sent.First(p => Decode(p.Payload).Kind == GameMessageKind.Snapshot).Payload);
        var payload = BlackjackProtocol.Encode(template with { State = game.CreateSnapshot() });
        Assert.Equal(5, Decode(payload).State.Players.Length);
        foreach (var seat in new[] { 5, 6 })
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(payload);
            json["State"]["Players"][4]["SeatIndex"] = seat;
            Assert.False(BlackjackProtocol.TryDecode(Encoding.UTF8.GetBytes(json.ToJsonString()), out _));
        }
        var six = System.Text.Json.Nodes.JsonNode.Parse(payload);
        six["State"]["Players"].AsArray().Add(JsonSerializer.SerializeToNode(new PlayerSnapshot
        {
            PlayerId = 6, SeatIndex = 5, Balance = 1000, IsConnected = true
        }));
        Assert.False(BlackjackProtocol.TryDecode(Encoding.UTF8.GetBytes(six.ToJsonString()), out _));
    }

    [Fact]
    public void TableBindingsCacheEarlySnapshotsAndKeepTablesIndependent()
    {
        using var rig = new Rig();
        using var host = new BlackjackTableBindings<string>(rig.Host);
        host.Observe("block-A", 17);
        host.Observe("block-B", 18);
        Assert.True(host.TryResolve("block-A", out var a));
        Assert.True(host.TryResolve("block-B", out var b));
        Assert.NotEqual(a, b);
        rig.Flush();
        using var client = new BlackjackTableBindings<string>(rig.Client);
        Assert.False(client.TryResolve("client-A", out _));
        client.Observe("client-A", 17);
        client.Observe("client-B", 18);
        client.Observe("unpublished", 19);
        Assert.True(client.TryResolve("client-A", out var received));
        Assert.Equal(a, received);
        Assert.False(client.TryResolve("unpublished", out _));
        rig.Submit(rig.Client, a, BlackjackAction.Join, 0);
        Assert.Single(rig.State(rig.Host, a).Players);
        Assert.Empty(rig.State(rig.Host, b).Players);

        // No observation (e.g. no renderer) is not a destruction notification.
        rig.Flush();
        Assert.True(host.TryResolve("block-A", out _));
        host.ConfirmDestroyed("block-A");
        rig.Flush();
        Assert.False(client.TryResolve("client-A", out _));
        Assert.True(client.TryResolve("client-B", out var retained));
        Assert.Equal(b, retained);
        host.Observe("replacement-A", 17);
        Assert.True(host.TryResolve("replacement-A", out var replacement));
        Assert.NotEqual(a, replacement);
        Assert.False(rig.Host.RegisterTable(a));
        rig.Flush();
        client.ConfirmDestroyed("client-A");
        client.Observe("replacement-client-A", 17);
        Assert.True(client.TryResolve("replacement-client-A", out received));
        Assert.Equal(replacement, received);
    }

    [Fact]
    public void TableBindingsReceiveSnapshotsAfterLocalDiscoveryAndSurvivePeerReconnect()
    {
        using var rig = new Rig();
        using var host = new BlackjackTableBindings<string>(rig.Host);
        using var client = new BlackjackTableBindings<string>(rig.Client);
        client.Observe("local", 23);
        Assert.False(client.TryResolve("local", out _));
        host.Observe("host", 23);
        rig.Flush();
        Assert.True(client.TryResolve("local", out var id));
        rig.Submit(rig.Client, id, BlackjackAction.Join, 0);
        rig.Host.SetLobby(new LobbyContext(10, 1, 1, new ulong[] { 1, 3 }));
        Assert.False(rig.State(rig.Host, id).Players[0].IsConnected);
        Assert.True(host.TryResolve("host", out var retained));
        Assert.Equal(id, retained);
        rig.Host.SetLobby(rig.Context(1));
        rig.Client.SetLobby(null);
        Assert.False(client.TryResolve("local", out _));
        rig.Now += TimeSpan.FromSeconds(3);
        rig.Client.SetLobby(rig.Context(2));
        rig.Flush();
        client.Observe("local", 23);
        Assert.True(client.TryResolve("local", out retained));
        Assert.Equal(id, retained);
        rig.Submit(rig.Client, id, BlackjackAction.Join, 0);
        Assert.True(rig.State(rig.Host, id).Players[0].IsConnected);
    }

    [Fact]
    public void TableBindingsResetOnHostChangeAndRejectAmbiguousLocalIdentity()
    {
        using var rig = new Rig();
        using var bindings = new BlackjackTableBindings<string>(rig.Host);
        bindings.Observe("one", 77);
        Assert.True(bindings.TryResolve("one", out var old));
        bindings.Observe("duplicate", 77);
        Assert.False(bindings.TryResolve("one", out _));
        bindings.ConfirmDestroyed("duplicate");
        Assert.True(bindings.TryResolve("one", out _));
        rig.Host.ResetSession();
        Assert.False(bindings.TryResolve("one", out _));
        bindings.Observe("one", 77);
        Assert.True(bindings.TryResolve("one", out var fresh));
        Assert.NotEqual(old, fresh);
        rig.Host.SetLobby(new LobbyContext(10, 1, 2, new ulong[] { 1, 2 }));
        bindings.Observe("one", 77);
        Assert.False(bindings.TryResolve("one", out _));
        Assert.Empty(rig.Host.GetTableIds());
        rig.Host.SetLobby(null);
        Assert.False(bindings.TryResolve("one", out _));
    }

    [Fact]
    public void TableBindingsReportEpochLimitWithoutResettingOtherTables()
    {
        using var rig = new Rig();
        using var bindings = new BlackjackTableBindings<int>(rig.Host);
        var failures = new List<string>();
        bindings.RegistrationFailed += failures.Add;
        var epoch = rig.Host.SessionEpoch;
        bindings.Observe(1, 1);
        Assert.True(bindings.TryResolve(1, out var retained));
        for (var i = 2; i < NetworkLimits.MaxTablesPerSession; i++)
        {
            bindings.Observe(i, i);
            bindings.ConfirmDestroyed(i);
        }
        bindings.Observe(999, 999);
        bindings.Observe(999, 999);
        Assert.Single(failures);
        Assert.False(bindings.TryResolve(999, out _));
        Assert.True(bindings.TryResolve(1, out var stillRetained));
        Assert.Equal(retained, stillRetained);
        Assert.Equal(epoch, rig.Host.SessionEpoch);
    }

    [Theory]
    [InlineData("blackjack:01:11111111111111111111111111111111")]
    [InlineData("blackjack:1:00000000000000000000000000000000")]
    [InlineData("blackjack:2147483648:11111111111111111111111111111111")]
    [InlineData("table:1:11111111111111111111111111111111")]
    [InlineData("blackjack:1:invalid")]
    public void TableBindingsRejectMalformedIdentities(string id)
    {
        Assert.False(BlackjackTableBindings<int>.TryParse(id, out _));
    }

    [Fact]
    public void PresentationHostCompletionIsSynchronousAndWagersAreTableLocal()
    {
        using var rig = new Rig();
        rig.Host.RegisterTable("second");
        using var view = Presentation(rig.Host, 1);
        Assert.True(view.RequestAction("table", 4, BlackjackAction.Join));
        Assert.False(view.IsPending);
        Assert.Contains("Accepted", view.GetView("table", 4).Status);
        Assert.True(view.RequestAction("second", 1, BlackjackAction.Join));
        view.AdjustWager("table", 4, 1);
        Assert.Equal(11m, view.GetView("table", 4).SelectedWager);
        Assert.Equal(10m, view.GetView("second", 1).SelectedWager);
        Assert.Equal(0m, rig.State(rig.Host).Players[0].Bet);
        Assert.True(view.RequestAction("table", 4, BlackjackAction.Bet));
        Assert.Equal(11m, rig.State(rig.Host).Players[0].Bet);
        Assert.True(view.RequestAction("table", 4, BlackjackAction.Leave));
        Assert.Empty(rig.State(rig.Host).Players);
        Assert.Single(rig.State(rig.Host, "second").Players);
    }

    [Fact]
    public void PresentationPendingBlocksEveryTableAndOtherViewsWithoutOptimisticMutation()
    {
        using var rig = new Rig();
        rig.Host.RegisterTable("second");
        rig.Flush();
        using var view = Presentation(rig.Client, 2);
        using var otherView = Presentation(rig.Client, 2);
        Assert.True(view.RequestAction("table", 3, BlackjackAction.Join));
        Assert.True(view.IsPending);
        Assert.True(otherView.IsPending);
        Assert.Contains("OPEN", view.GetView("table", 3).Header);
        Assert.False(otherView.RequestAction("second", 1, BlackjackAction.Join));
        Assert.False(view.GetView("second", 1).Can(BlackjackAction.Join));
        Assert.Empty(rig.State(rig.Host).Players);
        rig.Flush();
        Assert.False(view.IsPending);
        Assert.Contains("YOU", view.GetView("table", 3).Header);
        Assert.True(otherView.RequestAction("second", 1, BlackjackAction.Join));
        rig.Flush();
        Assert.Equal(3, rig.State(rig.Host).Players[0].SeatIndex);
        Assert.Equal(1, rig.State(rig.Host, "second").Players[0].SeatIndex);
        Assert.False(view.RequestAction("table", 0, BlackjackAction.Hit));
    }

    [Fact]
    public void PresentationFastClientCompletionDoesNotInstallStalePendingState()
    {
        using var rig = new Rig();
        using var view = Presentation(rig.Client, 2);
        rig.ClientWire.AfterSend = payload =>
        {
            if (Decode(payload).Kind == GameMessageKind.Command) rig.Flush();
        };
        Assert.True(view.RequestAction("table", 0, BlackjackAction.Join));
        Assert.False(view.IsPending);
        Assert.True(view.GetView("table", 0).Can(BlackjackAction.Bet));
        Assert.Contains("Accepted", view.GetView("table", 0).Status);
    }

    [Fact]
    public void PresentationStaleRevisionShowsRejectionWithoutAutomaticResubmission()
    {
        using var rig = new Rig();
        using var view = Presentation(rig.Client, 2);
        Assert.True(view.RequestAction("table", 0, BlackjackAction.Join));
        Assert.True(rig.Host.TrySubmit("table", BlackjackAction.Join, 1, 0, out _));
        rig.Flush();
        Assert.False(view.IsPending);
        Assert.Contains("Table changed", view.GetView("table", 0).Status);
        Assert.True(view.GetView("table", 0).Can(BlackjackAction.Join));
        rig.Now += TimeSpan.FromSeconds(3);
        rig.Flush();
        Assert.Single(rig.ClientWire.Sent, p => Decode(p.Payload).Kind == GameMessageKind.Command);
        Assert.Single(rig.State(rig.Host).Players);
        Assert.True(view.RequestAction("table", 0, BlackjackAction.Join));
        rig.Flush();
        Assert.Equal(2, rig.State(rig.Host).Players.Length);
    }

    [Fact]
    public void PresentationRemovalAndResetClearPendingAndSelectionsAndRebindingDetachesOldService()
    {
        using var rig = new Rig();
        rig.Host.RegisterTable("second");
        rig.Flush();
        using var view = Presentation(rig.Client, 2);
        rig.ClientWire.FailSends = true;
        Assert.True(view.RequestAction("table", 0, BlackjackAction.Join));
        rig.Host.RemoveTable("table");
        rig.Client.Pump();
        Assert.False(view.IsPending);
        Assert.False(view.GetView("table", 0).Can(BlackjackAction.Join));
        Assert.True(view.GetView("second", 0).Can(BlackjackAction.Join));
        rig.ClientWire.FailSends = false;
        Assert.True(view.RequestAction("second", 0, BlackjackAction.Join));
        rig.Flush();
        view.AdjustWager("second", 0, 1);
        Assert.Equal(11m, view.GetView("second", 0).SelectedWager);
        Assert.True(view.RequestAction("second", 0, BlackjackAction.Bet));
        rig.Client.SetLobby(null);
        Assert.False(view.IsPending);
        Assert.Equal(10m, view.GetView("second", 0).SelectedWager);
        view.Bind(rig.Host);
        view.Bind(rig.Host);
        view.SetContext(1, true);
        rig.Client.ResetSession();
        Assert.True(view.GetView("second", 1).Can(BlackjackAction.Join));
        Assert.True(view.RequestAction("second", 1, BlackjackAction.Join));
        Assert.False(view.IsPending);
    }

    [Fact]
    public void PresentationDisconnectReclaimAndReadinessFollowAuthoritativeSnapshots()
    {
        using var rig = new Rig();
        using var view = Presentation(rig.Client, 2);
        Assert.True(view.RequestAction("table", 0, BlackjackAction.Join));
        rig.Flush();
        rig.Host.SetLobby(new LobbyContext(10, 1, 1, new ulong[] { 1, 3 }));
        rig.Host.SetLobby(rig.Context(1));
        rig.Now += TimeSpan.FromSeconds(31);
        rig.Flush();
        Assert.Contains("OFFLINE", view.GetView("table", 0).Header);
        Assert.True(view.GetView("table", 0).Can(BlackjackAction.Join));
        Assert.False(view.GetView("table", 0).Can(BlackjackAction.Bet));
        Assert.True(view.RequestAction("table", 0, BlackjackAction.Join));
        rig.Flush();
        Assert.DoesNotContain("OFFLINE", view.GetView("table", 0).Header);
        view.SetContext(0, false, "Waiting for verified host.");
        Assert.False(view.RequestAction("table", 0, BlackjackAction.Bet));
        Assert.Contains("Waiting for verified host", view.GetView("table", 0).Status);
    }

    [Theory]
    [InlineData(BlackjackAction.Hit)]
    [InlineData(BlackjackAction.Stand)]
    [InlineData(BlackjackAction.DoubleDown)]
    [InlineData(BlackjackAction.Split)]
    public void PresentationRoutesTurnActionsWithZeroAmountAndPreservesSeat(BlackjackAction action)
    {
        var first = new[]
        {
            new Card { Rank = 8, Suit = Suit.Clubs }, new Card { Rank = 6, Suit = Suit.Clubs },
            new Card { Rank = 8, Suit = Suit.Diamonds }, new Card { Rank = 10, Suit = Suit.Clubs },
            new Card { Rank = 2, Suit = Suit.Clubs }, new Card { Rank = 3, Suit = Suit.Clubs }
        };
        var deck = first.Concat(BlackjackDeck.CreateOrdered().Where(c => !first.Contains(c))).ToArray();
        using var rig = new Rig(() => new BlackjackGameState(new BlackjackOptions(), () => deck));
        using var view = Presentation(rig.Client, 2);
        foreach (var step in new[] { BlackjackAction.Join, BlackjackAction.Bet, BlackjackAction.Deal, action })
        {
            Assert.True(view.RequestAction("table", 4, step));
            var command = Decode(rig.ClientWire.Sent.Last().Payload);
            Assert.Equal(step, command.Action);
            Assert.Equal("table", command.TableId);
            Assert.Equal(4, command.SeatIndex);
            Assert.Equal(step == BlackjackAction.Bet ? 10m : 0m, command.Amount);
            rig.Flush();
            Assert.False(view.IsPending);
            Assert.True(rig.Completions.Last().Accepted, rig.Completions.Last().Error);
        }
        Assert.Equal(rig.State(rig.Host).Players[0].Balance, rig.State(rig.Client).Players[0].Balance);
        if (action == BlackjackAction.Split)
        {
            Assert.Equal(2, view.GetView("table", 4).HandSummaries.Count);
            Assert.True(view.RequestAction("table", 4, BlackjackAction.Stand));
            rig.Flush();
            Assert.Contains("TURN H2", view.GetView("table", 4).Status);
        }
    }

    private static BlackjackTablePresentation Presentation(BlackjackStateService service, ulong playerId)
    {
        var view = new BlackjackTablePresentation();
        view.Bind(service);
        view.SetContext(playerId, true);
        return view;
    }

    [Fact]
    public void PresentationPreservesHalfCentPayoutAndSelectsOnlySpendableCents()
    {
        var first = new[]
        {
            new Card { Rank = 1, Suit = Suit.Clubs }, new Card { Rank = 6, Suit = Suit.Clubs },
            new Card { Rank = 10, Suit = Suit.Clubs }, new Card { Rank = 9, Suit = Suit.Clubs }
        };
        var deck = first.Concat(BlackjackDeck.CreateOrdered().Where(c => !first.Contains(c))).ToArray();
        using var rig = new Rig(() => new BlackjackGameState(
            new BlackjackOptions { InitialBalance = 10.01m, MinimumBet = 0.01m }, () => deck));
        using var view = Presentation(rig.Host, 1);
        Assert.True(view.RequestAction("table", 0, BlackjackAction.Join));
        view.AdjustWager("table", 0, 1);
        Assert.Equal(10.01m, view.GetView("table", 0).SelectedWager);
        Assert.True(view.RequestAction("table", 0, BlackjackAction.Bet));
        Assert.True(view.RequestAction("table", 0, BlackjackAction.Deal));
        Assert.Equal(25.025m, rig.State(rig.Host).Players[0].Balance);
        Assert.Contains("BAL 25.025", view.GetView("table", 0).Header);
        for (var i = 0; i < 20; i++) view.AdjustWager("table", 0, 1);
        Assert.Equal(25.02m, view.GetView("table", 0).SelectedWager);
        Assert.True(view.RequestAction("table", 0, BlackjackAction.Bet));
        Assert.Equal(0.005m, rig.State(rig.Host).Players[0].Balance);
        Assert.Contains("BAL 0.005", view.GetView("table", 0).Header);
    }

    private static BlackjackMessage Decode(byte[] payload)
    {
        Assert.True(BlackjackProtocol.TryDecode(payload, out var message));
        return message;
    }

    private sealed class Rig : IDisposable
    {
        public readonly Bus Bus = new();
        public readonly FakeTransport HostWire;
        public readonly FakeTransport ClientWire;
        public readonly BlackjackStateService Host;
        public readonly BlackjackStateService Client;
        public readonly List<CommandCompletion> Completions = new();
        public DateTimeOffset Now = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);

        public Rig() : this(null)
        {
        }

        public Rig(Func<BlackjackGameState> createGame)
        {
            HostWire = new FakeTransport(Bus, 1);
            ClientWire = new FakeTransport(Bus, 2);
            Host = new BlackjackStateService(HostWire, createGame, clock: () => Now);
            Client = new BlackjackStateService(ClientWire, clock: () => Now);
            Client.CommandCompleted += completion => Completions.Add(completion);
            Host.SetLobby(Context(1));
            Assert.True(Host.RegisterTable("table"));
            Client.SetLobby(Context(2));
            Flush();
        }

        public LobbyContext Context(ulong localId) => new(10, localId, 1, new ulong[] { 1, 2, 3 });
        public void Flush()
        {
            for (var i = 0; i < 4; i++)
            {
                Host.Pump();
                Client.Pump();
            }
        }

        public BlackjackSnapshot State(BlackjackStateService service, string id = "table")
        {
            Assert.True(service.TryGetTable(id, out var table));
            return table.State;
        }

        public void Submit(BlackjackStateService service, string tableId, BlackjackAction action, int seatIndex, decimal amount = 0)
        {
            Assert.True(service.TrySubmit(tableId, action, seatIndex, amount, out _));
            Flush();
        }

        public void Dispose()
        {
            Client.Dispose();
            Host.Dispose();
        }
    }

    private sealed class Bus
    {
        public readonly Dictionary<ulong, Queue<ReceivedPacket>> Queues = new();
        public void Inject(ulong targetId, ulong senderId, byte[] payload) =>
            Queues[targetId].Enqueue(new ReceivedPacket(senderId, (byte[])payload.Clone()));
    }

    private sealed class FakeTransport : IGameTransport
    {
        private readonly Bus _bus;
        private readonly ulong _id;
        private readonly HashSet<ulong> _peers = new();
        public readonly List<ReceivedPacket> Sent = new();
        public bool FailSends;
        public Action<byte[]> AfterSend;

        public FakeTransport(Bus bus, ulong id)
        {
            _bus = bus;
            _id = id;
            _bus.Queues.Add(id, new Queue<ReceivedPacket>());
        }

        public void SetPeers(IReadOnlyCollection<ulong> peers)
        {
            _peers.Clear();
            _peers.UnionWith(peers);
        }

        public bool TrySend(ulong recipientId, byte[] payload)
        {
            Sent.Add(new ReceivedPacket(recipientId, (byte[])payload.Clone()));
            if (FailSends || !_peers.Contains(recipientId) || !_bus.Queues.ContainsKey(recipientId))
                return false;
            _bus.Inject(recipientId, _id, payload);
            AfterSend?.Invoke(payload);
            return true;
        }

        public bool TryReceive(out ReceivedPacket packet) => _bus.Queues[_id].TryDequeue(out packet);
        public void Dispose() { }
    }
}