#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Approximately21.Blackjack;

public sealed class BlackjackOptions
{
    public decimal InitialBalance { get; init; } = 1000m;
    public decimal MinimumBet { get; init; } = 1m;
    public decimal MaximumBet { get; init; } = 500m;

    internal void Validate()
    {
        if (!BlackjackLimits.IsCurrency(InitialBalance, 100) || InitialBalance <= 0)
            throw new ArgumentOutOfRangeException(nameof(InitialBalance));
        if (!BlackjackLimits.IsCurrency(MinimumBet, 100) || MinimumBet <= 0)
            throw new ArgumentOutOfRangeException(nameof(MinimumBet));
        if (!BlackjackLimits.IsCurrency(MaximumBet, 100) || MaximumBet < MinimumBet)
            throw new ArgumentOutOfRangeException(nameof(MaximumBet));
    }
}

public static class BlackjackLimits
{
    public const int MaxSeats = 5;
    public const int MaxLifetimePlayers = 64;
    public const int MaxHands = 4;
    public const int MaxCardsPerHand = 22;
    public const int DeckSize = 52;
    public const decimal MaxCurrency = 1_000_000_000_000m;

    internal static bool IsCurrency(decimal amount, int scale) =>
        amount >= 0 && amount <= MaxCurrency && decimal.Truncate(amount * scale) == amount * scale;

    internal static bool IsCard(Card? card) =>
        card != null && card.Rank >= 1 && card.Rank <= 13 && Enum.IsDefined(typeof(Suit), card.Suit);
}

public readonly record struct BlackjackHandValue(int Total, bool IsSoft);

public static class BlackjackScoring
{
    public static BlackjackHandValue Evaluate(IEnumerable<Card> cards)
    {
        if (cards == null)
            throw new ArgumentNullException(nameof(cards));

        var total = 0;
        var softAces = 0;
        foreach (var card in cards)
        {
            if (!BlackjackLimits.IsCard(card))
                throw new ArgumentException("Cards must have a rank from 1 to 13 and a valid suit.", nameof(cards));
            total += card.Value;
            if (card.Rank == 1)
                softAces++;
        }

        while (total > 21 && softAces > 0)
        {
            total -= 10;
            softAces--;
        }
        return new BlackjackHandValue(total, softAces > 0);
    }

    internal static bool IsNatural(IReadOnlyCollection<Card> cards, bool isSplit = false) =>
        !isSplit && cards.Count == 2 && Evaluate(cards).Total == 21;

    internal static HandOutcome ResolveOutcome(IReadOnlyCollection<Card> cards, bool isSplit, IReadOnlyCollection<Card> dealer)
    {
        var playerScore = Evaluate(cards).Total;
        var dealerScore = Evaluate(dealer).Total;
        var natural = IsNatural(cards, isSplit);
        if (playerScore > 21)
            return HandOutcome.Loss;
        if (IsNatural(dealer))
            return natural ? HandOutcome.Push : HandOutcome.Loss;
        if (natural)
            return HandOutcome.Blackjack;
        if (dealerScore > 21 || playerScore > dealerScore)
            return HandOutcome.Win;
        return playerScore == dealerScore ? HandOutcome.Push : HandOutcome.Loss;
    }
}

public static class BlackjackDeck
{
    public static Card[] CreateOrdered()
    {
        var cards = new Card[BlackjackLimits.DeckSize];
        var index = 0;
        for (var suit = 0; suit < 4; suit++)
        for (var rank = 1; rank <= 13; rank++)
            cards[index++] = new Card { Rank = rank, Suit = (Suit)suit };
        return cards;
    }

    public static Card[] CreateShuffled(Func<int, int>? nextIndex = null)
    {
        nextIndex ??= RandomNumberGenerator.GetInt32;
        var cards = CreateOrdered();
        for (var i = cards.Length - 1; i > 0; i--)
        {
            var index = nextIndex(i + 1);
            if (index < 0 || index > i)
                throw new ArgumentOutOfRangeException(nameof(nextIndex), "Shuffle index must be within the exclusive upper bound.");
            (cards[i], cards[index]) = (cards[index], cards[i]);
        }
        return cards;
    }
}