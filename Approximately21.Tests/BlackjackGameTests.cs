using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Approximately21.Blackjack;
using Xunit;

namespace Approximately21.Tests;

public class BlackjackGameTests
{
    [Theory]
    [InlineData(new[] { 1, 1, 9 }, 21, true)]
    [InlineData(new[] { 1, 6, 10 }, 17, false)]
    [InlineData(new[] { 1, 6 }, 17, true)]
    [InlineData(new[] { 13, 12, 2 }, 22, false)]
    public void ScoresSoftAces(int[] ranks, int total, bool soft)
    {
        var score = BlackjackScoring.Evaluate(ranks.Select(rank => new Card { Rank = rank }));
        Assert.Equal(total, score.Total);
        Assert.Equal(soft, score.IsSoft);
    }

    [Fact]
    public void DeckHas52UniqueCardsAndShuffleUsesExclusiveBounds()
    {
        var bounds = new List<int>();
        var deck = BlackjackDeck.CreateShuffled(upper =>
        {
            bounds.Add(upper);
            return upper - 1;
        });
        Assert.Equal(52, deck.Length);
        Assert.Equal(52, deck.Distinct().Count());
        Assert.Equal(Enumerable.Range(2, 51).Reverse(), bounds);
        Assert.Equal(BlackjackDeck.CreateOrdered(), deck);
        Assert.All(deck, card => Assert.InRange(card.Value, 2, 11));
        Assert.Equal(11, new Card { Rank = 1 }.Value);
        Assert.Equal(10, new Card { Rank = 13 }.Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => BlackjackDeck.CreateShuffled(_ => -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BlackjackDeck.CreateShuffled(upper => upper));
        Assert.Equal(52, BlackjackDeck.CreateShuffled().Distinct().Count());
    }

    [Fact]
    public void DealsInSeatOrderAndRejectsWrongOwnerAndTurnAtomically()
    {
        var game = Game(10, 9, 6, 7, 8, 10, 2);
        Apply(game, 2, BlackjackAction.Join, 2);
        Apply(game, 1, BlackjackAction.Join, 0);
        Apply(game, 2, BlackjackAction.Bet, 2, 10);
        Apply(game, 1, BlackjackAction.Bet, 0, 10);
        Apply(game, 2, BlackjackAction.Deal, 2);
        var snapshot = game.CreateSnapshot();
        Assert.Equal(new[] { 10, 7 }, snapshot.Players[0].Hands[0].Cards.Select(card => card.Rank));
        Assert.Equal(new[] { 9, 8 }, snapshot.Players[1].Hands[0].Cards.Select(card => card.Rank));
        Assert.Equal(6, Assert.Single(snapshot.DealerCards).Rank);
        Assert.True(snapshot.DealerHoleCardHidden);
        Assert.Equal(0, snapshot.ActiveSeatIndex);
        Reject(game, 2, BlackjackAction.Hit, 2);
        Reject(game, 2, BlackjackAction.Hit, 0);
        Reject(game, 1, BlackjackAction.Leave, 0);
        Reject(game, 3, BlackjackAction.Join, 1);
        Apply(game, 1, BlackjackAction.Stand, 0);
        Assert.Equal(2, game.CreateSnapshot().ActiveSeatIndex);
        Apply(game, 2, BlackjackAction.Stand, 2);
        Assert.Equal(BlackjackPhase.RoundComplete, game.Phase);
        Assert.Equal(new[] { 6, 10, 2 }, game.CreateSnapshot().DealerCards.Select(card => card.Rank));
        Reject(game, 2, BlackjackAction.Hit, 2);
        Reject(game, 2, BlackjackAction.Deal, 2);
    }

    [Theory]
    [InlineData(10, 7, HandOutcome.Push, 1000)]
    [InlineData(10, 8, HandOutcome.Win, 1010)]
    [InlineData(10, 6, HandOutcome.Loss, 990)]
    public void DealerStandsOnSoft17(int first, int second, HandOutcome outcome, int balance)
    {
        var game = Start(Game(first, 1, second, 6));
        Apply(game, 1, BlackjackAction.Stand, 0);
        var snapshot = game.CreateSnapshot();
        Assert.Equal(2, snapshot.DealerCards.Length);
        Assert.Equal(outcome, snapshot.Players[0].Hands[0].Outcome);
        Assert.Equal((decimal)balance, snapshot.Players[0].Balance);
    }

    [Fact]
    public void BustEndsHandWithoutUnnecessaryDealerDraws()
    {
        var game = Start(Game(10, 6, 9, 10, 5));
        Apply(game, 1, BlackjackAction.Hit, 0);
        var snapshot = game.CreateSnapshot();
        Assert.Equal(BlackjackPhase.RoundComplete, snapshot.Phase);
        Assert.Equal(HandOutcome.Loss, snapshot.Players[0].Hands[0].Outcome);
        Assert.Equal(990m, snapshot.Players[0].Balance);
        Assert.Equal(2, snapshot.DealerCards.Length);
        Assert.True(snapshot.Players[0].Hands[0].IsStanding);
    }

    [Fact]
    public void Hit21AutomaticallyStandsAndDealerBustPaysEvenMoney()
    {
        var game = Start(Game(8, 6, 5, 10, 8, 10));
        Apply(game, 1, BlackjackAction.Hit, 0);
        var snapshot = game.CreateSnapshot();
        Assert.Equal(HandOutcome.Win, snapshot.Players[0].Hands[0].Outcome);
        Assert.Equal(1010m, snapshot.Players[0].Balance);
        Assert.Equal(3, snapshot.DealerCards.Length);
    }

    [Fact]
    public void NaturalPaysExactThreeToTwoIncludingHalfCents()
    {
        var game = Game(1, 9, 13, 7);
        Start(game, 1.01m);
        var snapshot = game.CreateSnapshot();
        Assert.Equal(BlackjackPhase.RoundComplete, snapshot.Phase);
        Assert.Equal(HandOutcome.Blackjack, snapshot.Players[0].Hands[0].Outcome);
        Assert.Equal(1001.515m, snapshot.Players[0].Balance);
    }

    [Fact]
    public void NaturalBeatsDealerDrawn21ButOrdinaryAndSplit21Push()
    {
        var game = Game(1, 10, 6, 13, 9, 5, 10);
        Apply(game, 1, BlackjackAction.Join, 0);
        Apply(game, 2, BlackjackAction.Join, 1);
        Apply(game, 1, BlackjackAction.Bet, 0, 10);
        Apply(game, 2, BlackjackAction.Bet, 1, 10);
        Apply(game, 1, BlackjackAction.Deal, 0);
        Apply(game, 2, BlackjackAction.Stand, 1);
        Assert.Equal(3, game.CreateSnapshot().DealerCards.Length);
        Assert.Equal(HandOutcome.Blackjack, game.CreateSnapshot().Players[0].Hands[0].Outcome);
        Assert.Equal(1015m, game.CreateSnapshot().Players[0].Balance);
        var ordinary = Start(Game(8, 6, 5, 5, 8, 10));
        Apply(ordinary, 1, BlackjackAction.Hit, 0);
        Assert.Equal(HandOutcome.Push, ordinary.CreateSnapshot().Players[0].Hands[0].Outcome);
        Assert.Equal(1000m, ordinary.CreateSnapshot().Players[0].Balance);
        var split = Start(Game(10, 6, 10, 5, 1, 9, 10));
        Apply(split, 1, BlackjackAction.Split, 0);
        Apply(split, 1, BlackjackAction.Stand, 0);
        Assert.Equal(HandOutcome.Push, split.CreateSnapshot().Players[0].Hands[0].Outcome);
        Assert.Equal(990m, split.CreateSnapshot().Players[0].Balance);
    }

    [Theory]
    [InlineData(1, 13, HandOutcome.Push, 1000)]
    [InlineData(10, 9, HandOutcome.Loss, 990)]
    public void DealerPeekSettlesImmediately(int first, int second, HandOutcome outcome, int balance)
    {
        var game = Start(Game(first, 1, second, 12));
        var snapshot = game.CreateSnapshot();
        Assert.Equal(BlackjackPhase.RoundComplete, snapshot.Phase);
        Assert.False(snapshot.DealerHoleCardHidden);
        Assert.Equal(2, snapshot.DealerCards.Length);
        Assert.Equal(outcome, snapshot.Players[0].Hands[0].Outcome);
        Assert.Equal((decimal)balance, snapshot.Players[0].Balance);
        Assert.Equal(-1, snapshot.ActiveSeatIndex);
        Assert.Equal(-1, snapshot.ActiveHandIndex);
    }

    [Fact]
    public void DoubleDrawsExactlyOneCardAndDoublesStake()
    {
        var game = Start(Game(5, 6, 6, 10, 10, 10));
        Apply(game, 1, BlackjackAction.DoubleDown, 0);
        var player = game.CreateSnapshot().Players[0];
        Assert.Equal(20m, player.Bet);
        Assert.Equal(20m, player.Hands[0].Bet);
        Assert.Equal(3, player.Hands[0].Cards.Length);
        Assert.Equal(1020m, player.Balance);
        Assert.Equal(HandOutcome.Win, player.Hands[0].Outcome);
    }

    [Fact]
    public void DoubleAfterHitAndWithoutFundsIsRejected()
    {
        var game = Start(Game(2, 10, 3, 7, 4));
        Apply(game, 1, BlackjackAction.Hit, 0);
        Reject(game, 1, BlackjackAction.DoubleDown, 0);
        var poor = Start(Game(new BlackjackOptions { InitialBalance = 10 }, 5, 10, 6, 7));
        Reject(poor, 1, BlackjackAction.DoubleDown, 0);
    }

    [Fact]
    public void Split21IsNotNaturalAndMovesThroughEachHand()
    {
        var game = Start(Game(10, 9, 10, 8, 1, 9));
        Apply(game, 1, BlackjackAction.Split, 0);
        var snapshot = game.CreateSnapshot();
        Assert.Equal(1, snapshot.ActiveHandIndex);
        Assert.True(snapshot.Players[0].Hands[0].IsStanding);
        Apply(game, 1, BlackjackAction.Stand, 0);
        var player = game.CreateSnapshot().Players[0];
        Assert.Equal(1020m, player.Balance);
        Assert.All(player.Hands, hand => Assert.Equal(HandOutcome.Win, hand.Outcome));
        Assert.All(player.Hands, hand => Assert.True(hand.IsSplit));
    }

    [Fact]
    public void SplitAcesGetOneCardAndCannotResplit()
    {
        var game = Start(Game(1, 9, 1, 8, 1, 13));
        Apply(game, 1, BlackjackAction.Split, 0);
        var player = game.CreateSnapshot().Players[0];
        Assert.Equal(BlackjackPhase.RoundComplete, game.Phase);
        Assert.Equal(1000m, player.Balance);
        Assert.Equal(HandOutcome.Loss, player.Hands[0].Outcome);
        Assert.Equal(HandOutcome.Win, player.Hands[1].Outcome);
        Assert.All(player.Hands, hand => Assert.Equal(2, hand.Cards.Length));
        Assert.All(player.Hands, hand => Assert.True(hand.IsStanding));
        Reject(game, 1, BlackjackAction.Split, 0);
        Reject(game, 1, BlackjackAction.Hit, 0);
    }

    [Fact]
    public void SplittingRequiresSameRankAndFunds()
    {
        Reject(Start(Game(10, 9, 13, 8)), 1, BlackjackAction.Split, 0);
        var poor = Start(Game(new BlackjackOptions { InitialBalance = 10 }, 8, 9, 8, 7));
        Reject(poor, 1, BlackjackAction.Split, 0);
    }

    [Fact]
    public void ResplitStopsAtFourHands()
    {
        var game = Start(Game(8, 10, 8, 7, 8, 8, 2, 2, 2, 2));
        Apply(game, 1, BlackjackAction.Split, 0);
        Apply(game, 1, BlackjackAction.Split, 0);
        Apply(game, 1, BlackjackAction.Stand, 0);
        Apply(game, 1, BlackjackAction.Stand, 0);
        Apply(game, 1, BlackjackAction.Split, 0);
        Assert.Equal(4, game.CreateSnapshot().Players[0].Hands.Length);
        Reject(game, 1, BlackjackAction.Split, 0);
        Apply(game, 1, BlackjackAction.Stand, 0);
        Apply(game, 1, BlackjackAction.Stand, 0);
        Assert.Equal(40m, game.CreateSnapshot().Players[0].Bet);
    }

    [Fact]
    public void DoubleAfterSplitIsAllowedButSplitAfterHitIsNot()
    {
        var game = Start(Game(5, 10, 5, 7, 6, 7, 10, 2));
        Apply(game, 1, BlackjackAction.Split, 0);
        Apply(game, 1, BlackjackAction.DoubleDown, 0);
        Assert.Equal(1, game.CreateSnapshot().ActiveHandIndex);
        Assert.Equal(20m, game.CreateSnapshot().Players[0].Hands[0].Bet);
        Apply(game, 1, BlackjackAction.Hit, 0);
        Reject(game, 1, BlackjackAction.Split, 0);
        Apply(game, 1, BlackjackAction.Stand, 0);
        Assert.Equal(1010m, game.CreateSnapshot().Players[0].Balance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExhaustionIsAtomicAndDealerCanAlwaysFinishOnDisconnect(bool reserveDealer)
    {
        var ranks = new[]
        {
            2, 3, 4, 5, 6, 9, 2, 3, 4, 5, 6, reserveDealer ? 6 : 8,
            2, 2, 10, 10, 8, 10, 8, 10, 11, 11, 11, 11,
            3, 3, 12, 12, reserveDealer ? 1 : 6, 12, 12, 13, 13, 13, 13,
            4, 4, 7, 8, 7, 9,
            5, 5, 7, 7,
            1, 9, 6, 9, 1, 1, reserveDealer ? 8 : 1
        };
        Assert.Equal(52, ranks.Length);
        var game = Game(ranks);
        for (var seat = 0; seat < BlackjackLimits.MaxSeats; seat++)
        {
            Apply(game, (ulong)seat + 1, BlackjackAction.Join, seat);
            Apply(game, (ulong)seat + 1, BlackjackAction.Bet, seat, 10);
        }
        Apply(game, 1, BlackjackAction.Deal, 0);
        for (var seat = 0; seat < 2; seat++)
        {
            var playerId = (ulong)seat + 1;
            Apply(game, playerId, BlackjackAction.Split, seat);
            Apply(game, playerId, BlackjackAction.Split, seat);
            Apply(game, playerId, BlackjackAction.Hit, seat);
            Apply(game, playerId, BlackjackAction.Hit, seat);
            if (seat == 0)
                Apply(game, playerId, BlackjackAction.Hit, seat);
            Apply(game, playerId, BlackjackAction.Hit, seat);
            Apply(game, playerId, BlackjackAction.Split, seat);
            Apply(game, playerId, BlackjackAction.Hit, seat);
            Apply(game, playerId, BlackjackAction.Hit, seat);
        }
        Apply(game, 3, BlackjackAction.Split, 2);
        for (var hit = 0; hit < 4; hit++)
            Apply(game, 3, BlackjackAction.Hit, 2);
        Apply(game, 4, BlackjackAction.Split, 3);
        for (var hit = 0; hit < 2; hit++)
        {
            Apply(game, 4, BlackjackAction.Hit, 3);
            Apply(game, 4, BlackjackAction.Stand, 3);
        }
        Apply(game, 5, BlackjackAction.Split, 4);
        for (var hit = 0; hit < (reserveDealer ? 4 : 5); hit++)
            Apply(game, 5, BlackjackAction.Hit, 4);
        Assert.Equal(4, game.CreateSnapshot().ActiveSeatIndex);
        Assert.Equal(1, game.CreateSnapshot().ActiveHandIndex);
        var pending = game.CreateSnapshot();
        Assert.Equal(reserveDealer ? 50 : 51, pending.DealerCards.Length + pending.Players.Sum(player => player.Hands.Sum(hand => hand.Cards.Length)));
        Reject(game, 5, BlackjackAction.Hit, 4);
        Assert.True(game.DisconnectPlayer(5));
        var complete = game.CreateSnapshot();
        Assert.Equal(BlackjackPhase.RoundComplete, complete.Phase);
        Assert.Equal(reserveDealer ? 3 : 2, complete.DealerCards.Length);
        Assert.Equal(52, complete.DealerCards.Length + complete.Players.Sum(player => player.Hands.Sum(hand => hand.Cards.Length)));
        AssertValid(game);
    }

    [Fact]
    public void NewRoundRequiresFreshWagersAndPreservesBalances()
    {
        var calls = 0;
        var game = new BlackjackGameState(new BlackjackOptions(), () =>
        {
            calls++;
            return Deck(10, 9, 8, 8);
        });
        Start(game);
        Apply(game, 1, BlackjackAction.Stand, 0);
        Assert.Equal(1010m, game.CreateSnapshot().Players[0].Balance);
        Reject(game, 1, BlackjackAction.Deal, 0);
        Apply(game, 1, BlackjackAction.Bet, 0, 20);
        var betting = game.CreateSnapshot();
        Assert.Equal(BlackjackPhase.Betting, betting.Phase);
        Assert.Empty(betting.DealerCards);
        Assert.Empty(betting.Players[0].Hands);
        Assert.Equal(990m, betting.Players[0].Balance);
        Apply(game, 1, BlackjackAction.Deal, 0);
        Apply(game, 1, BlackjackAction.Stand, 0);
        Assert.Equal(1030m, game.CreateSnapshot().Players[0].Balance);
        Assert.Equal(2, game.RoundNumber);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void WagerReplacementAndLeaveRefundOnlyUnplayedChips()
    {
        var game = Game();
        Apply(game, 1, BlackjackAction.Join, 0);
        Apply(game, 1, BlackjackAction.Bet, 0, 10);
        Apply(game, 1, BlackjackAction.Bet, 0, 20);
        Assert.Equal(980m, game.CreateSnapshot().Players[0].Balance);
        Apply(game, 1, BlackjackAction.Leave, 0);
        Assert.Empty(game.CreateSnapshot().Players);
    }

    [Fact]
    public void DisconnectRefundsBetAndDoesNotBlockOtherPlayers()
    {
        var game = Game(10, 9, 8, 8);
        Apply(game, 1, BlackjackAction.Join, 0);
        Apply(game, 2, BlackjackAction.Join, 1);
        Apply(game, 1, BlackjackAction.Bet, 0, 10);
        Apply(game, 2, BlackjackAction.Bet, 1, 10);
        Assert.True(game.DisconnectPlayer(1));
        Assert.False(game.DisconnectPlayer(1));
        Assert.False(game.DisconnectPlayer(99));
        var disconnected = game.CreateSnapshot().Players[0];
        Assert.False(disconnected.IsConnected);
        Assert.Equal(1000m, disconnected.Balance);
        Assert.Equal(0m, disconnected.Bet);
        AssertValid(game);
        Apply(game, 2, BlackjackAction.Deal, 1);
        Assert.Empty(game.CreateSnapshot().Players[0].Hands);
        Assert.Equal(1, game.CreateSnapshot().ActiveSeatIndex);
        Apply(game, 2, BlackjackAction.Stand, 1);
        Apply(game, 1, BlackjackAction.Leave, 0);
        Assert.Single(game.CreateSnapshot().Players);
    }

    [Fact]
    public void VacantUnwageredSeatsAndDisconnectedNaturalDoNotBlockTurns()
    {
        var game = Game(1, 8, 10, 13, 9, 7);
        Apply(game, 1, BlackjackAction.Join, 0);
        Apply(game, 2, BlackjackAction.Join, 3);
        Apply(game, 3, BlackjackAction.Join, 4);
        Apply(game, 1, BlackjackAction.Bet, 0, 10);
        Apply(game, 2, BlackjackAction.Bet, 3, 10);
        Apply(game, 3, BlackjackAction.Deal, 4);
        Assert.Equal(3, game.CreateSnapshot().ActiveSeatIndex);
        Assert.Empty(game.CreateSnapshot().Players[2].Hands);
        Assert.True(game.DisconnectPlayer(1));
        Assert.Equal(3, game.CreateSnapshot().ActiveSeatIndex);
        Apply(game, 2, BlackjackAction.Stand, 3);
        Assert.Equal(1015m, game.CreateSnapshot().Players[0].Balance);
        Assert.True(game.DisconnectPlayer(2));
        AssertValid(game);
    }

    [Fact]
    public void DisconnectStandsEverySplitHandAndAdvancesRound()
    {
        var game = Start(Game(8, 10, 8, 7, 2, 3));
        Apply(game, 1, BlackjackAction.Split, 0);
        Assert.True(game.DisconnectPlayer(1));
        Assert.Equal(BlackjackPhase.RoundComplete, game.Phase);
        var player = game.CreateSnapshot().Players[0];
        Assert.False(player.IsConnected);
        Assert.All(player.Hands, hand => Assert.True(hand.IsStanding));
        Assert.Equal(980m, player.Balance);
        AssertValid(game);
    }

    [Fact]
    public void DisconnectOutOfTurnDoesNotSkipConnectedPlayer()
    {
        var game = Game(10, 9, 10, 8, 7, 7);
        Apply(game, 1, BlackjackAction.Join, 0);
        Apply(game, 2, BlackjackAction.Join, 1);
        Apply(game, 1, BlackjackAction.Bet, 0, 10);
        Apply(game, 2, BlackjackAction.Bet, 1, 10);
        Apply(game, 1, BlackjackAction.Deal, 0);
        Assert.True(game.DisconnectPlayer(2));
        Assert.Equal(0, game.CreateSnapshot().ActiveSeatIndex);
        Apply(game, 1, BlackjackAction.Stand, 0);
        Assert.Equal(BlackjackPhase.RoundComplete, game.Phase);
    }

    [Fact]
    public void ReconnectDuringRoundPreservesAllAutoStoodSplitHands()
    {
        var game = Game(8, 9, 10, 8, 7, 7, 2, 3);
        Apply(game, 1, BlackjackAction.Join, 0);
        Apply(game, 2, BlackjackAction.Join, 1);
        Apply(game, 1, BlackjackAction.Bet, 0, 10);
        Apply(game, 2, BlackjackAction.Bet, 1, 10);
        Apply(game, 1, BlackjackAction.Deal, 0);
        Apply(game, 1, BlackjackAction.Split, 0);
        Assert.True(game.DisconnectPlayer(1));
        var before = game.CreateSnapshot();
        Assert.Equal(1, before.ActiveSeatIndex);
        Assert.Equal(2, before.Players[0].Hands.Length);
        Assert.Equal(980m, before.Players[0].Balance);

        Apply(game, 1, BlackjackAction.Join, 0);
        var after = game.CreateSnapshot();
        Assert.True(after.Players[0].IsConnected);
        Assert.Equal(before.Phase, after.Phase);
        Assert.Equal(before.ActiveSeatIndex, after.ActiveSeatIndex);
        Assert.Equal(before.ActiveHandIndex, after.ActiveHandIndex);
        Assert.Equal(before.Players[0].Balance, after.Players[0].Balance);
        Assert.Equal(before.Players[0].Bet, after.Players[0].Bet);
        Assert.Equal(JsonSerializer.Serialize(before.Players[0].Hands), JsonSerializer.Serialize(after.Players[0].Hands));
        Assert.All(after.Players[0].Hands, hand => Assert.True(hand.IsStanding));
        Reject(game, 1, BlackjackAction.Hit, 0);
        Reject(game, 1, BlackjackAction.DoubleDown, 0);
        Reject(game, 1, BlackjackAction.Split, 0);
        Reject(game, 1, BlackjackAction.Stand, 0);
        Reject(game, 1, BlackjackAction.Join, 0);
        Apply(game, 2, BlackjackAction.Stand, 1);
        Assert.Equal(BlackjackPhase.RoundComplete, game.Phase);
        Assert.Equal(980m, game.CreateSnapshot().Players[0].Balance);
    }

    [Fact]
    public void ReconnectOutOfTurnDoesNotRestoreAFutureTurn()
    {
        var game = Game(10, 9, 10, 8, 7, 7);
        Apply(game, 1, BlackjackAction.Join, 0);
        Apply(game, 2, BlackjackAction.Join, 1);
        Apply(game, 1, BlackjackAction.Bet, 0, 10);
        Apply(game, 2, BlackjackAction.Bet, 1, 10);
        Apply(game, 1, BlackjackAction.Deal, 0);
        Assert.True(game.DisconnectPlayer(2));
        Apply(game, 2, BlackjackAction.Join, 1);
        Assert.Equal(0, game.CreateSnapshot().ActiveSeatIndex);
        Assert.True(game.CreateSnapshot().Players[1].Hands[0].IsStanding);
        Apply(game, 1, BlackjackAction.Stand, 0);
        Assert.Equal(BlackjackPhase.RoundComplete, game.Phase);
        Reject(game, 2, BlackjackAction.Hit, 1);
        Reject(game, 2, BlackjackAction.DoubleDown, 1);
        Assert.Equal(990m, game.CreateSnapshot().Players[1].Balance);
    }

    [Fact]
    public void ReconnectBetweenRoundsRetainsRefundWithoutRestoringWager()
    {
        var game = Game();
        Apply(game, 1, BlackjackAction.Join, 0);
        Apply(game, 1, BlackjackAction.Bet, 0, 20);
        Assert.True(game.DisconnectPlayer(1));
        Apply(game, 1, BlackjackAction.Join, 0);
        var player = game.CreateSnapshot().Players[0];
        Assert.True(player.IsConnected);
        Assert.Equal(1000m, player.Balance);
        Assert.Equal(0m, player.Bet);
        Assert.Empty(player.Hands);
        Assert.Equal(BlackjackPhase.Betting, game.Phase);
        Reject(game, 1, BlackjackAction.Deal, 0);
        Apply(game, 1, BlackjackAction.Bet, 0, 10);
        Assert.Equal(990m, game.CreateSnapshot().Players[0].Balance);
    }

    [Fact]
    public void ReconnectAfterSettlementPreservesResultAndBankrollForNextRound()
    {
        var game = Start(Game(10, 10, 6, 7));
        Assert.True(game.DisconnectPlayer(1));
        var settled = game.CreateSnapshot();
        Assert.Equal(BlackjackPhase.RoundComplete, settled.Phase);
        Assert.Equal(990m, settled.Players[0].Balance);
        Apply(game, 1, BlackjackAction.Join, 0);
        var reconnected = game.CreateSnapshot();
        Assert.True(reconnected.Players[0].IsConnected);
        Assert.Equal(settled.Phase, reconnected.Phase);
        Assert.Equal(settled.RoundNumber, reconnected.RoundNumber);
        Assert.Equal(settled.Players[0].Balance, reconnected.Players[0].Balance);
        Assert.Equal(JsonSerializer.Serialize(settled.Players[0].Hands), JsonSerializer.Serialize(reconnected.Players[0].Hands));
        Reject(game, 1, BlackjackAction.Hit, 0);
        Reject(game, 1, BlackjackAction.DoubleDown, 0);
        Apply(game, 1, BlackjackAction.Bet, 0, 10);
        Assert.Equal(980m, game.CreateSnapshot().Players[0].Balance);
        Apply(game, 1, BlackjackAction.Deal, 0);
        Apply(game, 1, BlackjackAction.Stand, 0);
        Assert.Equal(2, game.RoundNumber);
        Assert.Equal(980m, game.CreateSnapshot().Players[0].Balance);
        Apply(game, 1, BlackjackAction.Leave, 0);
        Apply(game, 1, BlackjackAction.Join, 3);
        Assert.Equal(980m, game.CreateSnapshot().Players[0].Balance);
    }

    [Fact]
    public void JoinCannotStealDisconnectedSeatDuringRoundOrReconnectToAnotherSeat()
    {
        var game = Game(10, 9, 10, 8, 7, 7);
        Apply(game, 1, BlackjackAction.Join, 0);
        Apply(game, 2, BlackjackAction.Join, 1);
        Apply(game, 1, BlackjackAction.Bet, 0, 10);
        Apply(game, 2, BlackjackAction.Bet, 1, 10);
        Apply(game, 1, BlackjackAction.Deal, 0);
        Assert.True(game.DisconnectPlayer(1));
        Reject(game, 3, BlackjackAction.Join, 0);
        Reject(game, 3, BlackjackAction.Join, 2);
        Reject(game, 1, BlackjackAction.Join, 1);
        Reject(game, 1, BlackjackAction.Join, 2);
        Apply(game, 2, BlackjackAction.Stand, 1);
        Reject(game, 1, BlackjackAction.Join, 1);
        Reject(game, 1, BlackjackAction.Join, 2);
        Apply(game, 1, BlackjackAction.Join, 0);
        Assert.True(game.CreateSnapshot().Players[0].IsConnected);
    }

    [Theory]
    [InlineData(10, 8, 10, 1010)]
    [InlineData(10, 6, 10, 990)]
    [InlineData(10, 7, 10, 1000)]
    [InlineData(1, 13, 1, 1001.5)]
    [InlineData(10, 6, 1000, 0)]
    public void LeaveAndRejoinRetainSettledBankroll(int first, int second, int bet, double expectedBalance)
    {
        var game = Start(Game(new BlackjackOptions { MaximumBet = 1000 }, first, 10, second, 7), bet);
        if (game.Phase == BlackjackPhase.PlayerTurns)
            Apply(game, 1, BlackjackAction.Stand, 0);
        Assert.Equal((decimal)expectedBalance, game.CreateSnapshot().Players[0].Balance);
        Apply(game, 1, BlackjackAction.Leave, 0);
        Assert.Empty(game.CreateSnapshot().Players);
        Apply(game, 1, BlackjackAction.Join, 3);
        var player = game.CreateSnapshot().Players[0];
        Assert.Equal(3, player.SeatIndex);
        Assert.Equal((decimal)expectedBalance, player.Balance);
        Assert.Equal(0m, player.Bet);
        Assert.Empty(player.Hands);
        Apply(game, 1, BlackjackAction.Leave, 3);
        Apply(game, 1, BlackjackAction.Join, 0);
        Assert.Equal((decimal)expectedBalance, game.CreateSnapshot().Players[0].Balance);
        if (expectedBalance == 0)
            Reject(game, 1, BlackjackAction.Bet, 0, 1);
    }

    [Fact]
    public void LeaveRefundsOnlyFreshWagerAndPreservesHalfCentPayout()
    {
        var game = Start(Game(new BlackjackOptions { MinimumBet = 0.01m }, 1, 10, 13, 7), 0.01m);
        Assert.Equal(1000.015m, game.CreateSnapshot().Players[0].Balance);
        Apply(game, 1, BlackjackAction.Bet, 0, 20);
        Apply(game, 1, BlackjackAction.Leave, 0);
        Apply(game, 1, BlackjackAction.Join, 0);
        Assert.Equal(1000.015m, game.CreateSnapshot().Players[0].Balance);
    }

    [Fact]
    public void ReclaimAfterCompletedRoundRetainsOfflinePayoutWithoutRefundingSettledBet()
    {
        var game = Start(Game(10, 10, 8, 7));
        Assert.True(game.DisconnectPlayer(1));
        Assert.Equal(1010m, game.CreateSnapshot().Players[0].Balance);
        Apply(game, 2, BlackjackAction.Join, 0);
        var replacement = Assert.Single(game.CreateSnapshot().Players);
        Assert.Equal(2UL, replacement.PlayerId);
        Assert.Equal(1000m, replacement.Balance);
        Assert.Equal(BlackjackPhase.Betting, game.Phase);
        Reject(game, 1, BlackjackAction.Join, 0);
        Reject(game, 1, BlackjackAction.Leave, 0);
        Assert.False(game.DisconnectPlayer(1));
        Apply(game, 1, BlackjackAction.Join, 1);
        Assert.Equal(1010m, game.CreateSnapshot().Players[1].Balance);
        Assert.Empty(game.CreateSnapshot().Players[1].Hands);
    }

    [Fact]
    public void FullyDisconnectedTableCanReclaimEverySeatAndRetainsOldBankrolls()
    {
        var game = Game(10, 10, 6, 7);
        for (var seat = 0; seat < BlackjackLimits.MaxSeats; seat++)
            Apply(game, (ulong)seat + 1, BlackjackAction.Join, seat);
        Apply(game, 1, BlackjackAction.Bet, 0, 10);
        Apply(game, 1, BlackjackAction.Deal, 0);
        for (ulong id = 1; id <= BlackjackLimits.MaxSeats; id++)
            Assert.True(game.DisconnectPlayer(id));
        Assert.Equal(BlackjackPhase.RoundComplete, game.Phase);
        Assert.All(game.CreateSnapshot().Players, player => Assert.False(player.IsConnected));
        for (var seat = 0; seat < BlackjackLimits.MaxSeats; seat++)
            Apply(game, (ulong)seat + 8, BlackjackAction.Join, seat);
        Assert.Equal(5, game.CreateSnapshot().Players.Length);
        Assert.All(game.CreateSnapshot().Players, player => Assert.True(player.IsConnected));
        Assert.True(game.DisconnectPlayer(8));
        Apply(game, 1, BlackjackAction.Join, 0);
        Assert.Equal(990m, game.CreateSnapshot().Players[0].Balance);
        Assert.True(game.DisconnectPlayer(1));
        Apply(game, 8, BlackjackAction.Join, 0);
        Assert.Equal(1000m, game.CreateSnapshot().Players[0].Balance);
    }

    [Fact]
    public void ReclaimBettingSeatRetainsRefundedBalanceExactlyOnce()
    {
        var game = Start(Game(10, 10, 6, 7));
        Apply(game, 1, BlackjackAction.Stand, 0);
        Apply(game, 1, BlackjackAction.Bet, 0, 20);
        Assert.True(game.DisconnectPlayer(1));
        Apply(game, 2, BlackjackAction.Join, 0);
        Apply(game, 1, BlackjackAction.Join, 1);
        Assert.Equal(990m, game.CreateSnapshot().Players[1].Balance);
        Assert.Equal(0m, game.CreateSnapshot().Players[1].Bet);
    }

    [Fact]
    public void LifetimeIdentityLimitRetainsKnownPlayersAndRejectsNewPlayersAtomically()
    {
        var game = Start(Game(10, 10, 6, 7));
        Apply(game, 1, BlackjackAction.Stand, 0);
        Apply(game, 1, BlackjackAction.Leave, 0);
        for (ulong id = 2; id <= 64; id++)
        {
            Apply(game, id, BlackjackAction.Join, 0);
            Apply(game, id, BlackjackAction.Leave, 0);
        }
        Reject(game, 65, BlackjackAction.Join, 0);
        Apply(game, 1, BlackjackAction.Join, 0);
        Assert.Equal(990m, game.CreateSnapshot().Players[0].Balance);
        Assert.True(game.DisconnectPlayer(1));
        Reject(game, 65, BlackjackAction.Join, 0);
        Apply(game, 1, BlackjackAction.Join, 0);
        Assert.Equal(990m, game.CreateSnapshot().Players[0].Balance);
        Assert.True(game.DisconnectPlayer(1));
        Apply(game, 2, BlackjackAction.Join, 0);
        Apply(game, 1, BlackjackAction.Join, 1);
        Assert.Equal(990m, game.CreateSnapshot().Players[1].Balance);
        var freshTable = Game();
        Apply(freshTable, 1, BlackjackAction.Join, 0);
        Assert.Equal(1000m, freshTable.CreateSnapshot().Players[0].Balance);
    }

    [Fact]
    public void RejectedJoinsDoNotConsumeLifetimeIdentitySlots()
    {
        var game = Game();
        Apply(game, 1, BlackjackAction.Join, 0);
        for (ulong id = 2; id <= 65; id++)
            Reject(game, id, BlackjackAction.Join, 0);
        Apply(game, 1, BlackjackAction.Leave, 0);
        for (ulong id = 2; id <= 64; id++)
        {
            Apply(game, id, BlackjackAction.Join, 0);
            Apply(game, id, BlackjackAction.Leave, 0);
        }
        Reject(game, 65, BlackjackAction.Join, 0);
    }

    [Fact]
    public void RejectsMalformedActionsSeatsAmountsAndDuplicateJoins()
    {
        var game = Game();
        Reject(game, 0, BlackjackAction.Join, 0);
        Reject(game, 1, BlackjackAction.Join, -1);
        Reject(game, 1, BlackjackAction.Join, 5);
        Reject(game, 1, BlackjackAction.Join, 6);
        Reject(game, 1, BlackjackAction.Join, 7);
        Reject(game, 1, (BlackjackAction)99, 0);
        Reject(game, 1, BlackjackAction.Join, 0, 1);
        Apply(game, 1, BlackjackAction.Join, 0);
        Reject(game, 1, BlackjackAction.Join, 1);
        Reject(game, 2, BlackjackAction.Join, 0);
        Reject(game, 1, BlackjackAction.Deal, 0);
        foreach (var amount in new[] { -1m, 0m, 0.001m, 501m, decimal.MaxValue })
            Reject(game, 1, BlackjackAction.Bet, 0, amount);
        Apply(game, 1, BlackjackAction.Bet, 0, 10);
        Reject(game, 1, BlackjackAction.Deal, 0, 1);
        var poor = Game(new BlackjackOptions { InitialBalance = 5 });
        Apply(poor, 1, BlackjackAction.Join, 0);
        Reject(poor, 1, BlackjackAction.Bet, 0, 10);
        var capped = Game(new BlackjackOptions { InitialBalance = BlackjackLimits.MaxCurrency - 1 });
        Apply(capped, 1, BlackjackAction.Join, 0);
        Reject(capped, 1, BlackjackAction.Bet, 0, 1);
    }

    [Fact]
    public void OptionsAndInjectedDeckAreValidated()
    {
        Assert.Throws<ArgumentNullException>(() => new BlackjackGameState(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlackjackGameState(new BlackjackOptions { InitialBalance = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlackjackGameState(new BlackjackOptions { InitialBalance = 0.001m }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlackjackGameState(new BlackjackOptions { MinimumBet = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlackjackGameState(new BlackjackOptions { MinimumBet = 0.001m }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlackjackGameState(new BlackjackOptions { MaximumBet = 0.5m }));
        var game = new BlackjackGameState(new BlackjackOptions(), () => new[] { new Card { Rank = 1 } });
        Apply(game, 1, BlackjackAction.Join, 0);
        Apply(game, 1, BlackjackAction.Bet, 0, 10);
        Reject(game, 1, BlackjackAction.Deal, 0);
        var invalid = BlackjackDeck.CreateOrdered();
        invalid[1] = invalid[0];
        var duplicateDeck = new BlackjackGameState(new BlackjackOptions(), () => invalid);
        Apply(duplicateDeck, 1, BlackjackAction.Join, 0);
        Apply(duplicateDeck, 1, BlackjackAction.Bet, 0, 10);
        Reject(duplicateDeck, 1, BlackjackAction.Deal, 0);
        foreach (var card in new Card[] { null!, new Card { Rank = 0 }, new Card { Rank = 3, Suit = (Suit)99 } })
        {
            invalid[0] = card;
            Reject(duplicateDeck, 1, BlackjackAction.Deal, 0);
        }
    }

    [Fact]
    public void DeckProviderArrayCannotMutateAnActiveRound()
    {
        var deck = Deck(10, 9, 8, 8, 2);
        var game = Start(new BlackjackGameState(new BlackjackOptions(), () => deck));
        Array.Fill(deck, new Card { Rank = 13 });
        Apply(game, 1, BlackjackAction.Hit, 0);
        Assert.Equal(2, game.CreateSnapshot().Players[0].Hands[0].Cards[2].Rank);
        Apply(game, 1, BlackjackAction.Stand, 0);
        Assert.Equal(1010m, game.CreateSnapshot().Players[0].Balance);
    }

    [Fact]
    public void LegacyButtonCounterHasNoGameSideEffects()
    {
        var game = new BlackjackGameState();
        game.RecordTestButtonClick(123);
        game.RecordTestButtonClick(456);
        var snapshot = game.CreateSnapshot();
        Assert.Equal(2, snapshot.TestButtonClickCount);
        Assert.Equal(BlackjackPhase.Betting, snapshot.Phase);
        Assert.Empty(snapshot.Players);
        AssertValid(game);
    }

    [Fact]
    public void SnapshotsAreDetachedAndJsonContainsNoDeckBackrefsOrScore()
    {
        var game = Start(Game(10, 9, 8, 8));
        var snapshot = game.CreateSnapshot();
        var json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("Deck", json);
        Assert.DoesNotContain("GameState", json);
        Assert.DoesNotContain("Score", json);
        Assert.True(BlackjackSnapshotValidator.IsValid(JsonSerializer.Deserialize<BlackjackSnapshot>(json)!));
        snapshot.DealerCards[0] = new Card { Rank = 1 };
        snapshot.Players[0].Hands[0].Cards[0] = new Card { Rank = 2 };
        snapshot.Players[0] = new PlayerSnapshot();
        Assert.Equal(9, game.CreateSnapshot().DealerCards[0].Rank);
        Assert.Equal(10, game.CreateSnapshot().Players[0].Hands[0].Cards[0].Rank);
        Apply(game, 1, BlackjackAction.Stand, 0);
        Assert.True(snapshot.DealerHoleCardHidden);
        AssertValid(game);
    }

    [Fact]
    public void RejectsHostileSnapshotNullsEnumsDuplicatesAndBounds()
    {
        var valid = Start(Game(10, 9, 8, 8)).CreateSnapshot();
        Assert.True(BlackjackSnapshotValidator.IsValid(valid));
        Assert.False(BlackjackSnapshotValidator.IsValid(null!));
        Assert.False(BlackjackSnapshotValidator.IsValid(new BlackjackSnapshot { MinimumBet = 1, MaximumBet = 500, Phase = (BlackjackPhase)99 }));
        Assert.False(BlackjackSnapshotValidator.IsValid(new BlackjackSnapshot { MinimumBet = 1, MaximumBet = 500, Players = null! }));
        Assert.False(BlackjackSnapshotValidator.IsValid(new BlackjackSnapshot { MinimumBet = 1, MaximumBet = 500, DealerCards = null! }));
        Assert.False(BlackjackSnapshotValidator.IsValid(new BlackjackSnapshot { MinimumBet = 1, MaximumBet = 500, RoundNumber = -1 }));
        Assert.False(BlackjackSnapshotValidator.IsValid(new BlackjackSnapshot { MinimumBet = 1, MaximumBet = 500, TestButtonClickCount = -1 }));
        Assert.False(BlackjackSnapshotValidator.IsValid(new BlackjackSnapshot { MinimumBet = 1, MaximumBet = 500, Players = new PlayerSnapshot[6] }));
        Assert.False(BlackjackSnapshotValidator.IsValid(new BlackjackSnapshot { MinimumBet = 1, MaximumBet = 500, Players = new[] { new PlayerSnapshot { PlayerId = 1, SeatIndex = 5 } } }));
        Assert.False(BlackjackSnapshotValidator.IsValid(new BlackjackSnapshot { MinimumBet = 1, MaximumBet = 500, Players = new[] { new PlayerSnapshot { PlayerId = 1, Balance = -1 } } }));
        var duplicatePlayers = new BlackjackSnapshot
        {
            MinimumBet = 1, MaximumBet = 500,
            Players = new[] { new PlayerSnapshot { PlayerId = 1, SeatIndex = 0 }, new PlayerSnapshot { PlayerId = 1, SeatIndex = 1 } }
        };
        Assert.False(BlackjackSnapshotValidator.IsValid(duplicatePlayers));
        duplicatePlayers.Players[1] = new PlayerSnapshot { PlayerId = 2, SeatIndex = 0 };
        Assert.False(BlackjackSnapshotValidator.IsValid(duplicatePlayers));
        foreach (var card in new Card[] { null!, new Card { Rank = 0 }, new Card { Rank = 14 }, new Card { Rank = 5, Suit = (Suit)99 } })
        {
            valid.DealerCards[0] = card;
            Assert.False(BlackjackSnapshotValidator.IsValid(valid));
        }
        valid.DealerCards[0] = valid.Players[0].Hands[0].Cards[0];
        Assert.False(BlackjackSnapshotValidator.IsValid(valid));
        valid.DealerCards[0] = new Card { Rank = 9 };
        valid.Players[0].Hands[0] = new HandSnapshot { Cards = new Card[23], Bet = 10 };
        Assert.False(BlackjackSnapshotValidator.IsValid(valid));
        valid.Players[0].Hands[0] = new HandSnapshot { Cards = null!, Bet = 10 };
        Assert.False(BlackjackSnapshotValidator.IsValid(valid));
        valid.Players[0].Hands[0] = new HandSnapshot { Cards = new[] { new Card { Rank = 2 }, new Card { Rank = 3 } }, Bet = 10, Outcome = (HandOutcome)99 };
        Assert.False(BlackjackSnapshotValidator.IsValid(valid));
        valid.Players[0] = new PlayerSnapshot { PlayerId = 1, Hands = null! };
        Assert.False(BlackjackSnapshotValidator.IsValid(valid));
        valid.Players[0] = null!;
        Assert.False(BlackjackSnapshotValidator.IsValid(valid));
    }

    [Fact]
    public void RejectsIllegalSnapshotTurnsCurrencyAndHandStatesWithoutMutation()
    {
        var source = JsonSerializer.Serialize(Start(Game(10, 9, 8, 8)).CreateSnapshot());
        var corruptions = new Action<JsonNode>[]
        {
            json => json["ActiveSeatIndex"] = -1,
            json => json["ActiveSeatIndex"] = 5,
            json => json["ActiveSeatIndex"] = 6,
            json => json["ActiveSeatIndex"] = 1,
            json => json["ActiveHandIndex"] = -1,
            json => json["ActiveHandIndex"] = 1,
            json => json["ActiveHandIndex"] = 4,
            json => json["RoundNumber"] = 0,
            json => json["DealerHoleCardHidden"] = false,
            json => json["Players"]![0]!["Balance"] = decimal.MaxValue,
            json => json["Players"]![0]!["Balance"] = 0.001m,
            json => json["Players"]![0]!["Bet"] = 11m,
            json => json["Players"]![0]!["IsConnected"] = false,
            json => json["Players"]![0]!["Hands"] = new JsonArray(null, null, null, null, null),
            json => json["Players"]![0]!["Hands"]![0] = null,
            json => json["Players"]![0]!["Hands"]![0]!["Bet"] = 0,
            json => json["Players"]![0]!["Hands"]![0]!["Bet"] = 0.001m,
            json => json["Players"]![0]!["Hands"]![0]!["IsStanding"] = true,
            json => json["Players"]![0]!["Hands"]![0]!["IsSplit"] = true,
            json => json["Players"]![0]!["Hands"]![0]!["Outcome"] = (int)HandOutcome.Win
        };
        foreach (var corrupt in corruptions)
        {
            var json = JsonNode.Parse(source)!;
            corrupt(json);
            var snapshot = JsonSerializer.Deserialize<BlackjackSnapshot>(json.ToJsonString())!;
            var before = JsonSerializer.Serialize(snapshot);
            Assert.False(BlackjackSnapshotValidator.IsValid(snapshot));
            Assert.Equal(before, JsonSerializer.Serialize(snapshot));
        }
        var game = Start(Game(10, 9, 8, 8));
        Apply(game, 1, BlackjackAction.Stand, 0);
        var settled = JsonNode.Parse(JsonSerializer.Serialize(game.CreateSnapshot()))!;
        settled["Players"]![0]!["Hands"]![0]!["Outcome"] = (int)HandOutcome.Blackjack;
        Assert.False(BlackjackSnapshotValidator.IsValid(JsonSerializer.Deserialize<BlackjackSnapshot>(settled.ToJsonString())!));
    }

    [Theory]
    [InlineData(17)]
    [InlineData(42)]
    [InlineData(613)]
    public void SeededFullTableRoundsAlwaysProduceValidSnapshots(int seed)
    {
        var random = new Random(seed);
        var game = new BlackjackGameState(new BlackjackOptions { InitialBalance = 1000000 }, () => BlackjackDeck.CreateShuffled(random.Next));
        for (var seat = 0; seat < BlackjackLimits.MaxSeats; seat++)
            Apply(game, (ulong)seat + 1, BlackjackAction.Join, seat);
        for (var round = 1; round <= 25; round++)
        {
            for (var seat = 0; seat < BlackjackLimits.MaxSeats; seat++)
                Apply(game, (ulong)seat + 1, BlackjackAction.Bet, seat, 25);
            Apply(game, 1, BlackjackAction.Deal, 0);
            var turns = 0;
            while (game.Phase == BlackjackPhase.PlayerTurns)
            {
                Assert.True(turns++ < 100);
                var before = game.CreateSnapshot();
                var action = new[] { BlackjackAction.Hit, BlackjackAction.Stand, BlackjackAction.DoubleDown, BlackjackAction.Split }[random.Next(4)];
                if (!game.TryApply((ulong)before.ActiveSeatIndex + 1, action, before.ActiveSeatIndex, 0, out _))
                {
                    Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(game.CreateSnapshot()));
                    Apply(game, (ulong)before.ActiveSeatIndex + 1, BlackjackAction.Stand, before.ActiveSeatIndex);
                }
                AssertValid(game);
            }
            Assert.Equal(round, game.RoundNumber);
            Assert.All(game.CreateSnapshot().Players, player => Assert.All(player.Hands, hand => Assert.NotEqual(HandOutcome.Pending, hand.Outcome)));
        }
    }

    [Fact]
    public void SnapshotLimitsMatchValidatedOptionsIncludingBoundaryValues()
    {
        foreach (var limits in new[] { (1m, 500m), (0.01m, 0.01m), (12.34m, 567.89m), (BlackjackLimits.MaxCurrency, BlackjackLimits.MaxCurrency) })
        {
            var snapshot = new BlackjackGameState(new BlackjackOptions
            {
                MinimumBet = limits.Item1, MaximumBet = limits.Item2
            }).CreateSnapshot();
            Assert.Equal(limits.Item1, snapshot.MinimumBet);
            Assert.Equal(limits.Item2, snapshot.MaximumBet);
            Assert.True(BlackjackSnapshotValidator.IsValid(snapshot));
        }
        foreach (var amount in new[] { -1m, 0m, 0.001m, BlackjackLimits.MaxCurrency + 0.01m, decimal.MaxValue })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BlackjackGameState(new BlackjackOptions { MinimumBet = amount }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BlackjackGameState(new BlackjackOptions { MaximumBet = amount }));
        }
    }

    private static BlackjackGameState Game(params int[] ranks) => Game(new BlackjackOptions(), ranks);

    private static BlackjackGameState Game(BlackjackOptions options, params int[] ranks) => new(options, () => Deck(ranks));

    private static Card[] Deck(params int[] ranks)
    {
        var remaining = BlackjackDeck.CreateOrdered().ToList();
        var cards = new List<Card>();
        foreach (var rank in ranks)
        {
            var card = remaining.First(candidate => candidate.Rank == rank);
            cards.Add(card);
            remaining.Remove(card);
        }
        cards.AddRange(remaining);
        return cards.ToArray();
    }

    private static BlackjackGameState Start(BlackjackGameState game, decimal bet = 10)
    {
        Apply(game, 1, BlackjackAction.Join, 0);
        Apply(game, 1, BlackjackAction.Bet, 0, bet);
        Apply(game, 1, BlackjackAction.Deal, 0);
        return game;
    }

    private static void Apply(BlackjackGameState game, ulong player, BlackjackAction action, int seat, decimal amount = 0)
    {
        Assert.True(game.TryApply(player, action, seat, amount, out var error), error);
        Assert.Equal(string.Empty, error);
        AssertValid(game);
    }

    private static void Reject(BlackjackGameState game, ulong player, BlackjackAction action, int seat, decimal amount = 0)
    {
        var before = JsonSerializer.Serialize(game.CreateSnapshot());
        Assert.False(game.TryApply(player, action, seat, amount, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(before, JsonSerializer.Serialize(game.CreateSnapshot()));
        AssertValid(game);
    }

    private static void AssertValid(BlackjackGameState game) => Assert.True(BlackjackSnapshotValidator.IsValid(game.CreateSnapshot()));
}