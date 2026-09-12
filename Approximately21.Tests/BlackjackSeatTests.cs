using System;
using System.IO;
using System.Linq;
using Approximately21.Blackjack;
using Xunit;

namespace Approximately21.Tests;

public class BlackjackSeatTests
{
    [Theory]
    [InlineData(1.03636, 0.61818)]
    [InlineData(1.04, 0.58)]
    [InlineData(2.07272, 1.23636)]
    public void LayoutIsSymmetricEquallySpacedContainedAndSeparated(double width, double depth)
    {
        var poses = BlackjackSeatLayout.Create(width, depth);
        var boundary = BlackjackSeatLayout.Boundary(width, depth);
        Assert.Equal(5, poses.Length);
        Assert.Equal(0, poses[2].X, 10);
        Assert.Equal(0, poses[2].RotationRadians, 10);
        var step = poses[1].ArcDistance - poses[0].ArcDistance;
        for (var i = 0; i < poses.Length; i++)
        {
            Assert.Equal(i, poses[i].SeatIndex);
            Assert.Equal(-poses[4 - i].X, poses[i].X, 10);
            Assert.Equal(poses[4 - i].Z, poses[i].Z, 10);
            Assert.Equal(-poses[4 - i].RotationRadians, poses[i].RotationRadians, 10);
            if (i > 0) Assert.Equal(step, poses[i].ArcDistance - poses[i - 1].ArcDistance, 10);
            Assert.True(Math.Cos(poses[i].RotationRadians) > 0);
            foreach (var corner in poses[i].Corners)
                for (var edge = 0; edge < boundary.Length; edge++)
                {
                    var a = boundary[edge];
                    var b = boundary[(edge + 1) % boundary.Length];
                    Assert.True((b.X - a.X) * (corner.Z - a.Z) - (b.Z - a.Z) * (corner.X - a.X) >= -1e-8,
                        $"Seat {i} corner {corner} outside edge {edge}");
                }
            for (var other = i + 1; other < poses.Length; other++) Assert.True(Separated(poses[i], poses[other]));
        }
        // The profile is not an equal-X distribution.
        Assert.NotEqual(poses[1].X - poses[0].X, poses[2].X - poses[1].X);
    }

    private static bool Separated(BlackjackSeatPose a, BlackjackSeatPose b)
    {
        foreach (var angle in new[] { a.RotationRadians, b.RotationRadians })
        foreach (var axis in new[] { new SeatPoint(Math.Cos(angle), Math.Sin(angle)), new SeatPoint(-Math.Sin(angle), Math.Cos(angle)) })
        {
            var x = a.Corners.Select(p => p.X * axis.X + p.Z * axis.Z).ToArray();
            var y = b.Corners.Select(p => p.X * axis.X + p.Z * axis.Z).ToArray();
            if (x.Max() < y.Min() || y.Max() < x.Min()) return true;
        }
        return false;
    }

    [Fact]
    public void PanelsFitTheExportedFeltTrianglesNotJustTheCalibratedPolygon()
    {
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "BlackjackTableMesh.bin")));
        Assert.Equal("A21P", System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4)));
        var count = reader.ReadInt32();
        var indexCount = reader.ReadInt32();
        var points = new SeatPoint[count];
        for (var i = 0; i < count; i++)
        {
            var x = reader.ReadSingle() * 0.1;
            reader.ReadSingle();
            points[i] = new SeatPoint(x, reader.ReadSingle() * 0.1);
        }
        reader.BaseStream.Position = 12 + count * 32;
        var groups = Enumerable.Range(0, count).Select(_ => reader.ReadUInt32()).ToArray();
        var indices = Enumerable.Range(0, indexCount).Select(_ => reader.ReadInt32()).ToArray();
        var felt = points.Where((_, i) => groups[i] == 1).ToArray();
        var width = felt.Max(p => p.X) - felt.Min(p => p.X);
        var depth = felt.Max(p => p.Z) - felt.Min(p => p.Z);
        var center = new SeatPoint((felt.Max(p => p.X) + felt.Min(p => p.X)) / 2,
            (felt.Max(p => p.Z) + felt.Min(p => p.Z)) / 2);
        Assert.Equal(BlackjackSeatLayout.ModelWidth, width, 5);
        Assert.Equal(BlackjackSeatLayout.ModelDepth, depth, 5);
        foreach (var vertex in BlackjackSeatLayout.Boundary(width, depth))
            Assert.Contains(felt, p => Math.Abs(p.X - center.X - vertex.X) < 0.000002 &&
                Math.Abs(p.Z - center.Z - vertex.Z) < 0.000002);
        foreach (var pose in BlackjackSeatLayout.Create(width, depth))
        foreach (var corner in pose.Corners)
        {
            var p = new SeatPoint(corner.X + center.X, corner.Z + center.Z);
            Assert.Contains(Enumerable.Range(0, indexCount / 3), triangle =>
            {
                var a = indices[triangle * 3];
                var b = indices[triangle * 3 + 1];
                var c = indices[triangle * 3 + 2];
                if (groups[a] != 1) return false;
                var signs = new[] { Cross(points[a], points[b], p), Cross(points[b], points[c], p), Cross(points[c], points[a], p) };
                return Math.Abs(Cross(points[a], points[b], points[c])) > 1e-10 &&
                    (signs.All(s => s >= -1e-9) || signs.All(s => s <= 1e-9));
            });
        }
    }

    private static double Cross(SeatPoint a, SeatPoint b, SeatPoint p) =>
        (b.X - a.X) * (p.Z - a.Z) - (b.Z - a.Z) * (p.X - a.X);

    [Fact]
    public void ChildRectanglesFitAndDoNotOverlap()
    {
        var rectangles = Enumerable.Range(0, 8).Select(BlackjackSeatLayout.ActionRectangle)
            .Concat(new[] { BlackjackSeatLayout.Header, BlackjackSeatLayout.Wager, BlackjackSeatLayout.Minus,
                BlackjackSeatLayout.Plus }).Concat(Enumerable.Range(0, 6).Select(BlackjackSeatLayout.SummaryRectangle)).ToArray();
        for (var i = 0; i < rectangles.Length; i++)
        {
            var a = rectangles[i];
            Assert.True(Math.Abs(a.X) + a.Width / 2 <= 0.5);
            Assert.True(Math.Abs(a.Z) + a.Depth / 2 <= 0.5);
            for (var j = i + 1; j < rectangles.Length; j++)
            {
                var b = rectangles[j];
                Assert.True(Math.Abs(a.X - b.X) >= (a.Width + b.Width) / 2 ||
                    Math.Abs(a.Z - b.Z) >= (a.Depth + b.Depth) / 2);
            }
        }
    }

    private static PlayerSnapshot Player(bool connected = true, decimal balance = 100, decimal bet = 10,
        params HandSnapshot[] hands) => new() { PlayerId = 7, SeatIndex = 0, IsConnected = connected,
            Balance = balance, Bet = bet, Hands = hands };
    private static BlackjackSnapshot Snapshot(BlackjackPhase phase = BlackjackPhase.Betting, PlayerSnapshot player = null,
        int hand = 0) => new() { MinimumBet = 1, MaximumBet = 500, Phase = phase, ActiveSeatIndex = 0,
            ActiveHandIndex = hand, Players = player == null ? Array.Empty<PlayerSnapshot>() : new[] { player } };
    private static bool Can(BlackjackSnapshot snapshot, BlackjackAction action, ulong id = 7, int seat = 0,
        decimal wager = 10, bool ready = true, bool pending = false) =>
        BlackjackActionAvailability.Can(snapshot, id, seat, action, wager, ready, pending);
    private static HandSnapshot Hand(int a = 8, int b = 8, bool split = false, bool standing = false) => new()
        { Cards = new[] { new Card { Rank = a }, new Card { Rank = b } }, Bet = 10, IsSplit = split, IsStanding = standing };

    [Fact]
    public void JoinUsesReconnectAndReclaimRules()
    {
        Assert.True(Can(Snapshot(), BlackjackAction.Join));
        Assert.False(Can(Snapshot(player: Player()), BlackjackAction.Join, id: 9));
        Assert.True(Can(Snapshot(player: Player(false)), BlackjackAction.Join, id: 9));
        var turn = Snapshot(BlackjackPhase.PlayerTurns, Player(false));
        Assert.True(Can(turn, BlackjackAction.Join));
        Assert.False(Can(turn, BlackjackAction.Join, seat: 1));
        Assert.False(Can(turn, BlackjackAction.Join, id: 9));
        Assert.False(Can(Snapshot(player: Player()), BlackjackAction.Join, seat: 1));
    }

    [Theory]
    [InlineData(BlackjackPhase.Betting, true, true)]
    [InlineData(BlackjackPhase.RoundComplete, true, false)]
    [InlineData(BlackjackPhase.PlayerTurns, false, false)]
    public void BetweenRoundActions(BlackjackPhase phase, bool between, bool deal)
    {
        var snapshot = Snapshot(phase, Player());
        Assert.Equal(between, Can(snapshot, BlackjackAction.Leave));
        Assert.Equal(between, Can(snapshot, BlackjackAction.Bet));
        Assert.Equal(deal, Can(snapshot, BlackjackAction.Deal));
        Assert.False(Can(Snapshot(phase, Player(bet: 0)), BlackjackAction.Deal));
    }

    [Fact]
    public void TurnActionsUseActiveHandCardsFundsAndLimits()
    {
        var snapshot = Snapshot(BlackjackPhase.PlayerTurns, Player(hands: new[] { Hand(standing: true), Hand() }), 1);
        foreach (var action in new[] { BlackjackAction.Hit, BlackjackAction.Stand, BlackjackAction.DoubleDown, BlackjackAction.Split })
            Assert.True(Can(snapshot, action));
        Assert.False(Can(Snapshot(BlackjackPhase.PlayerTurns, Player(hands: new[] { Hand(standing: true) })), BlackjackAction.Hit));
        Assert.False(Can(Snapshot(BlackjackPhase.PlayerTurns, Player(balance: 9.99m, hands: new[] { Hand() })), BlackjackAction.DoubleDown));
        Assert.False(Can(Snapshot(BlackjackPhase.PlayerTurns, Player(balance: 9.99m, hands: new[] { Hand() })), BlackjackAction.Split));
        Assert.False(Can(Snapshot(BlackjackPhase.PlayerTurns, Player(hands: new[] { Hand(10, 13) })), BlackjackAction.Split));
        Assert.False(Can(Snapshot(BlackjackPhase.PlayerTurns, Player(hands: new[] { Hand(1, 1, true) })), BlackjackAction.Split));
        Assert.False(Can(Snapshot(BlackjackPhase.PlayerTurns, Player(hands: Enumerable.Repeat(Hand(), 4).ToArray())), BlackjackAction.Split));
        Assert.False(Can(snapshot, BlackjackAction.Hit, seat: 1));
    }

    [Fact]
    public void ReadOnlyAndPendingStatesDisableEveryAction()
    {
        var snapshot = Snapshot(BlackjackPhase.PlayerTurns, Player(hands: new[] { Hand() }));
        foreach (BlackjackAction action in Enum.GetValues(typeof(BlackjackAction)))
        {
            Assert.False(Can(snapshot, action, pending: true));
            Assert.False(Can(snapshot, action, ready: false));
            Assert.False(Can(snapshot, action, id: 9));
            Assert.False(Can(snapshot, action, id: 0));
            Assert.False(Can(snapshot, action, seat: 5));
            Assert.False(Can(null, action));
        }
    }

    [Fact]
    public void WagersKeepDecimalsAndOnlyRefundUnplayedStake()
    {
        Assert.Equal(10m, BlackjackActionAvailability.ClampWager(Snapshot(), 0));
        var betting = Snapshot(player: Player(balance: 5.5m, bet: 10));
        Assert.Equal(10m, BlackjackActionAvailability.ClampWager(betting, 0));
        Assert.Equal(11m, BlackjackActionAvailability.AdjustWager(betting, 0, 10, 1));
        Assert.Equal(9m, BlackjackActionAvailability.AdjustWager(betting, 0, 10, -1));
        Assert.Equal(15.5m, BlackjackActionAvailability.ClampWager(betting, 0, 100));
        Assert.True(Can(betting, BlackjackAction.Bet, wager: 15.5m));
        var completed = Snapshot(BlackjackPhase.RoundComplete, Player(balance: 5.5m, bet: 10));
        Assert.Equal(5.5m, BlackjackActionAvailability.ClampWager(completed, 0, 100));
        Assert.False(Can(completed, BlackjackAction.Bet, wager: 15.5m));
        Assert.False(Can(betting, BlackjackAction.Bet, wager: 1.001m));
        Assert.False(Can(betting, BlackjackAction.Bet, wager: 0));
        Assert.Equal(0.5m, BlackjackActionAvailability.ClampWager(Snapshot(player: Player(balance: 0.5m, bet: 0)), 0));
    }

    [Fact]
    public void HostLimitsAndInsufficientFundsConstrainSelection()
    {
        var snapshot = new BlackjackSnapshot { MinimumBet = 12.5m, MaximumBet = 20.5m, Players = new[] { Player() } };
        Assert.Equal(12.5m, BlackjackActionAvailability.ClampWager(snapshot, 0));
        Assert.Equal(20.5m, BlackjackActionAvailability.ClampWager(snapshot, 0, 500));
        Assert.False(Can(snapshot, BlackjackAction.Bet, wager: 21));
        Assert.False(BlackjackSeatViewState.Create(snapshot, 7, 0, ready: true).CanDecrease);
        Assert.False(BlackjackSeatViewState.Create(snapshot, 7, 0, selectedWager: 21, ready: true).CanIncrease);
        Assert.False(Can(Snapshot(player: Player(balance: 0.5m, bet: 0)), BlackjackAction.Bet, wager: 1));
    }

    [Fact]
    public void FinishedOrNonInitialHandsDisableTheRelevantTurnActions()
    {
        var threeCards = new HandSnapshot { Bet = 10, Cards = new[] { new Card { Rank = 2 }, new Card { Rank = 3 }, new Card { Rank = 4 } } };
        var snapshot = Snapshot(BlackjackPhase.PlayerTurns, Player(hands: new[] { threeCards }));
        Assert.True(Can(snapshot, BlackjackAction.Hit));
        Assert.True(Can(snapshot, BlackjackAction.Stand));
        Assert.False(Can(snapshot, BlackjackAction.DoubleDown));
        Assert.False(Can(snapshot, BlackjackAction.Split));
        foreach (var hand in new[] { Hand(1, 13), new HandSnapshot { Cards = Hand().Cards, Outcome = HandOutcome.Loss }, Hand(standing: true) })
        foreach (var action in new[] { BlackjackAction.Hit, BlackjackAction.Stand, BlackjackAction.DoubleDown, BlackjackAction.Split })
            Assert.False(Can(Snapshot(BlackjackPhase.PlayerTurns, Player(hands: new[] { hand })), action));
    }

    [Fact]
    public void DisconnectedAndOtherPlayersHaveNoOwnedControls()
    {
        foreach (var phase in new[] { BlackjackPhase.Betting, BlackjackPhase.PlayerTurns, BlackjackPhase.RoundComplete })
        foreach (var id in new ulong[] { 7, 9 })
        {
            var snapshot = Snapshot(phase, Player(connected: false, hands: new[] { Hand() }));
            var view = BlackjackSeatViewState.Create(snapshot, id, 0, ready: true);
            foreach (BlackjackAction action in Enum.GetValues(typeof(BlackjackAction)))
                if (action != BlackjackAction.Join) Assert.False(view.Can(action));
            Assert.False(view.CanIncrease);
            Assert.False(view.CanDecrease);
        }
    }

    [Fact]
    public void SplitSummariesAreDetachedAndFitIndividualGlyphLabels()
    {
        var cards = Enumerable.Repeat(new Card { Rank = 2 }, 22).ToArray();
        var player = Player(hands: Enumerable.Range(0, 4).Select(_ => new HandSnapshot
            { Cards = cards, Bet = 10, Outcome = HandOutcome.Loss }).ToArray());
        var snapshot = new BlackjackSnapshot { MinimumBet = 1, MaximumBet = 500, Phase = BlackjackPhase.RoundComplete,
            Players = new[] { player }, DealerCards = new[] { new Card { Rank = 13 }, new Card { Rank = 7 } } };
        var view = BlackjackSeatViewState.Create(snapshot, 7, 0, ready: true, feedback: new string('X', 200));
        Assert.Equal(4, view.HandSummaries.Count);
        for (var i = 0; i < 4; i++)
        {
            Assert.StartsWith($"H{i + 1} ", view.HandSummaries[i]);
            Assert.Contains("=44", view.HandSummaries[i]);
            Assert.True(view.HandSummaries[i].Length <= 128);
        }
        Assert.True(view.Status.Length <= 128);
        Assert.Contains("=17", view.DealerSummary);
        Assert.DoesNotContain("?", view.DealerSummary);
        cards[0] = new Card { Rank = 1 };
        Assert.Contains("=44", view.HandSummaries[0]);
        Assert.Equal(100m, player.Balance);
        Assert.Equal(10m, player.Bet);
    }

    [Fact]
    public void ViewConcealsHoleCardPreservesMoneyAndDisablesAdjustments()
    {
        var snapshot = new BlackjackSnapshot { MinimumBet = 1, MaximumBet = 50,
            Players = new[] { Player(balance: 12.5m) }, DealerHoleCardHidden = true,
            DealerCards = new[] { new Card { Rank = 6 }, new Card { Rank = 13 } } };
        var view = BlackjackSeatViewState.Create(snapshot, 7, 0, ready: true);
        Assert.Contains("SEAT 1 YOU", view.Header);
        Assert.Contains("12.5", view.Header);
        Assert.Contains("?", view.DealerSummary);
        Assert.DoesNotContain("K", view.DealerSummary);
        Assert.True(view.CanIncrease);
        Assert.True(view.CanDecrease);
        var pending = BlackjackSeatViewState.Create(snapshot, 7, 0, ready: true, pending: true);
        Assert.False(pending.CanIncrease);
        Assert.False(pending.CanDecrease);
        Assert.False(pending.Can(BlackjackAction.Bet));
        Assert.Contains("COMMAND PENDING", pending.Status);
    }
}