#nullable enable
using System;
using System.Collections.Generic;

namespace Approximately21.Blackjack;

public static class BlackjackSnapshotValidator
{
    public static bool IsValid(BlackjackSnapshot snapshot)
    {
        if (snapshot == null || !Enum.IsDefined(typeof(BlackjackPhase), snapshot.Phase) ||
            !BlackjackLimits.IsCurrency(snapshot.MinimumBet, 100) || snapshot.MinimumBet <= 0 ||
            !BlackjackLimits.IsCurrency(snapshot.MaximumBet, 100) || snapshot.MaximumBet < snapshot.MinimumBet ||
            snapshot.RoundNumber < 0 || snapshot.TestButtonClickCount < 0 ||
            snapshot.Players == null || snapshot.Players.Length > BlackjackLimits.MaxSeats ||
            snapshot.DealerCards == null || snapshot.DealerCards.Length > BlackjackLimits.MaxCardsPerHand)
            return false;

        var betting = snapshot.Phase == BlackjackPhase.Betting;
        var playing = snapshot.Phase == BlackjackPhase.PlayerTurns;
        if (snapshot.DealerHoleCardHidden != playing)
            return false;
        if (betting && snapshot.DealerCards.Length != 0 || playing && snapshot.DealerCards.Length != 1 ||
            !betting && !playing && snapshot.DealerCards.Length < 2)
            return false;
        if (!betting && snapshot.RoundNumber == 0)
            return false;
        if (playing)
        {
            if (snapshot.ActiveSeatIndex < 0 || snapshot.ActiveSeatIndex >= BlackjackLimits.MaxSeats ||
                snapshot.ActiveHandIndex < 0 || snapshot.ActiveHandIndex >= BlackjackLimits.MaxHands)
                return false;
        }
        else if (snapshot.ActiveSeatIndex != -1 || snapshot.ActiveHandIndex != -1)
            return false;

        var seenCards = new HashSet<(int, Suit)>();
        if (!AddCards(snapshot.DealerCards, seenCards))
            return false;
        var seenPlayers = new HashSet<ulong>();
        var seenSeats = new HashSet<int>();
        var firstActiveSeat = BlackjackLimits.MaxSeats;
        var firstActiveHand = -1;
        var handCount = 0;
        var needsDealerPlay = false;
        foreach (var player in snapshot.Players)
        {
            if (player == null || player.PlayerId == 0 || !seenPlayers.Add(player.PlayerId) ||
                player.SeatIndex < 0 || player.SeatIndex >= BlackjackLimits.MaxSeats || !seenSeats.Add(player.SeatIndex) ||
                !BlackjackLimits.IsCurrency(player.Balance, 200) || !BlackjackLimits.IsCurrency(player.Bet, 100) ||
                player.Hands == null || player.Hands.Length > BlackjackLimits.MaxHands)
                return false;
            if (playing && player.Balance + player.Bet > BlackjackLimits.MaxCurrency)
                return false;

            if (betting)
            {
                if (player.Hands.Length != 0 || !player.IsConnected && player.Bet != 0 ||
                    player.Balance + player.Bet > BlackjackLimits.MaxCurrency)
                    return false;
                continue;
            }

            decimal totalBet = 0;
            var splitRank = 0;
            for (var index = 0; index < player.Hands.Length; index++)
            {
                var hand = player.Hands[index];
                if (hand == null || hand.Cards == null || hand.Cards.Length < 2 ||
                    hand.Cards.Length > BlackjackLimits.MaxCardsPerHand || !AddCards(hand.Cards, seenCards) ||
                    !BlackjackLimits.IsCurrency(hand.Bet, 100) || hand.Bet <= 0 ||
                    !Enum.IsDefined(typeof(HandOutcome), hand.Outcome) ||
                    hand.IsSplit != (player.Hands.Length > 1))
                    return false;

                if (hand.IsSplit)
                {
                    if (index == 0)
                        splitRank = hand.Cards[0].Rank;
                    if (hand.Cards[0].Rank != splitRank)
                        return false;
                    if (splitRank == 1 && (player.Hands.Length != 2 || hand.Cards.Length != 2 || !hand.IsStanding))
                        return false;
                }

                var score = BlackjackScoring.Evaluate(hand.Cards).Total;
                if ((!player.IsConnected || score >= 21) && !hand.IsStanding)
                    return false;
                if (playing)
                {
                    if (hand.Outcome != HandOutcome.Pending)
                        return false;
                    if (!hand.IsStanding && player.SeatIndex < firstActiveSeat)
                    {
                        firstActiveSeat = player.SeatIndex;
                        firstActiveHand = index;
                    }
                }
                else
                {
                    if (!hand.IsStanding || hand.Outcome != BlackjackScoring.ResolveOutcome(hand.Cards, hand.IsSplit, snapshot.DealerCards))
                        return false;
                }

                if (score <= 21 && !BlackjackScoring.IsNatural(hand.Cards, hand.IsSplit))
                    needsDealerPlay = true;
                if (!HasLegalDraws(hand.Cards, 21))
                    return false;
                totalBet += hand.Bet;
                handCount++;
            }
            if (totalBet != player.Bet)
                return false;
        }

        if (seenCards.Count + (playing ? 1 : 0) > BlackjackLimits.DeckSize)
            return false;
        if (betting)
            return true;
        if (handCount == 0)
            return false;
        if (playing)
            return firstActiveSeat == snapshot.ActiveSeatIndex && firstActiveHand == snapshot.ActiveHandIndex;

        if (!HasLegalDraws(snapshot.DealerCards, 17))
            return false;
        return needsDealerPlay
            ? BlackjackScoring.Evaluate(snapshot.DealerCards).Total >= 17
            : snapshot.DealerCards.Length == 2;
    }

    private static bool AddCards(Card[] cards, HashSet<(int, Suit)> seenCards)
    {
        foreach (var card in cards)
        {
            if (!BlackjackLimits.IsCard(card) || !seenCards.Add((card.Rank, card.Suit)))
                return false;
        }
        return true;
    }

    private static bool HasLegalDraws(Card[] cards, int standAt)
    {
        if (cards.Length <= 2)
            return true;
        var prefix = new List<Card> { cards[0], cards[1] };
        for (var index = 2; index < cards.Length; index++)
        {
            if (BlackjackScoring.Evaluate(prefix).Total >= standAt)
                return false;
            prefix.Add(cards[index]);
        }
        return true;
    }
}