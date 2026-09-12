#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Approximately21.Blackjack;

public sealed class BlackjackGameState
{
    private readonly object sync = new();
    private readonly BlackjackOptions options;
    private readonly Func<IReadOnlyList<Card>> deckFactory;
    private RoundState state = new();

    public BlackjackGameState() : this(new BlackjackOptions())
    {
    }

    public BlackjackGameState(BlackjackOptions options, Func<IReadOnlyList<Card>>? deckFactory = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        this.deckFactory = deckFactory ?? (() => BlackjackDeck.CreateShuffled());
    }

    public int TestButtonClickCount { get; private set; }

    public BlackjackPhase Phase
    {
        get { lock (sync) return state.Phase; }
    }

    public int RoundNumber
    {
        get { lock (sync) return state.RoundNumber; }
    }

    public bool TryApply(ulong playerId, BlackjackAction action, int seatIndex, decimal amount, out string error)
    {
        lock (sync)
        {
            try
            {
                Require(playerId != 0, "A nonzero player ID is required.");
                Require(Enum.IsDefined(typeof(BlackjackAction), action), "Unknown blackjack action.");
                Require(seatIndex >= 0 && seatIndex < BlackjackLimits.MaxSeats, "Invalid seat index.");
                Require(action == BlackjackAction.Bet || amount == 0, "Only a bet action accepts an amount.");
                var next = state.Copy();
                ApplyAction(next, playerId, action, seatIndex, amount);
                if (next.Phase == BlackjackPhase.PlayerTurns)
                    EnsureDealerCanFinish(next);
                state = next;
                error = string.Empty;
                return true;
            }
            catch (RejectedActionException exception)
            {
                error = exception.Message;
                return false;
            }
        }
    }

    public bool DisconnectPlayer(ulong playerId)
    {
        lock (sync)
        {
            var next = state.Copy();
            var player = next.Players.Find(candidate => candidate.PlayerId == playerId);
            if (player == null || !player.IsConnected)
                return false;

            player.IsConnected = false;
            if (next.Phase == BlackjackPhase.Betting)
            {
                player.Balance += player.Bet;
                player.Bet = 0;
            }
            else if (next.Phase == BlackjackPhase.PlayerTurns)
            {
                foreach (var hand in player.Hands)
                    hand.IsStanding = true;
                AdvanceTurn(next);
            }
            state = next;
            return true;
        }
    }

    public BlackjackSnapshot CreateSnapshot()
    {
        lock (sync)
        {
            var hidden = state.Phase == BlackjackPhase.PlayerTurns;
            return new BlackjackSnapshot
            {
                MinimumBet = options.MinimumBet,
                MaximumBet = options.MaximumBet,
                Phase = state.Phase,
                RoundNumber = state.RoundNumber,
                ActiveSeatIndex = state.ActiveSeatIndex,
                ActiveHandIndex = state.ActiveHandIndex,
                DealerHoleCardHidden = hidden,
                DealerCards = CopyCards(hidden ? state.DealerCards.Take(1) : state.DealerCards),
                TestButtonClickCount = TestButtonClickCount,
                Players = state.Players.Select(player => new PlayerSnapshot
                {
                    SeatIndex = player.SeatIndex,
                    PlayerId = player.PlayerId,
                    Balance = player.Balance,
                    Bet = player.Bet,
                    IsConnected = player.IsConnected,
                    Hands = player.Hands.Select(hand => new HandSnapshot
                    {
                        Cards = CopyCards(hand.Cards),
                        Bet = hand.Bet,
                        IsStanding = hand.IsStanding,
                        IsSplit = hand.IsSplit,
                        Outcome = hand.Outcome
                    }).ToArray()
                }).ToArray()
            };
        }
    }

    internal void RecordTestButtonClick(int tableEntityIndex)
    {
        lock (sync)
        {
            if (TestButtonClickCount < int.MaxValue)
                TestButtonClickCount++;
        }
    }

    private void ApplyAction(RoundState next, ulong playerId, BlackjackAction action, int seatIndex, decimal amount)
    {
        if (action == BlackjackAction.Join)
        {
            var seated = next.Players.Find(player => player.PlayerId == playerId);
            if (seated != null)
            {
                Require(!seated.IsConnected, "Player is already seated.");
                Require(seated.SeatIndex == seatIndex, "Reconnect using the player's existing seat.");
                seated.IsConnected = true;
                return;
            }

            Require(next.Phase != BlackjackPhase.PlayerTurns, "Join between rounds.");
            var occupant = next.Players.Find(player => player.SeatIndex == seatIndex);
            Require(occupant == null || !occupant.IsConnected, "Seat is occupied.");
            var returning = next.RetainedBalances.TryGetValue(playerId, out var balance);
            Require(returning || next.RetainedBalances.Count < BlackjackLimits.MaxLifetimePlayers,
                "This table has reached its lifetime player limit; only returning players may join.");
            ResetCompletedRound(next);
            if (occupant != null)
            {
                next.RetainedBalances[occupant.PlayerId] = occupant.Balance;
                next.Players.Remove(occupant);
            }
            if (!returning)
            {
                balance = options.InitialBalance;
                next.RetainedBalances.Add(playerId, balance);
            }
            next.Players.Add(new SeatedPlayer { PlayerId = playerId, SeatIndex = seatIndex, Balance = balance });
            next.Players.Sort((left, right) => left.SeatIndex.CompareTo(right.SeatIndex));
            return;
        }

        var owner = next.Players.Find(player => player.SeatIndex == seatIndex && player.PlayerId == playerId);
        if (owner == null)
            throw new RejectedActionException("Player does not own this seat.");
        if (action == BlackjackAction.Leave)
        {
            Require(next.Phase != BlackjackPhase.PlayerTurns, "Leave between rounds.");
            ResetCompletedRound(next);
            owner.Balance += owner.Bet;
            next.RetainedBalances[owner.PlayerId] = owner.Balance;
            next.Players.Remove(owner);
            return;
        }

        Require(owner.IsConnected, "Player is disconnected.");
        if (action == BlackjackAction.Bet)
        {
            Require(next.Phase != BlackjackPhase.PlayerTurns, "Bet between rounds.");
            Require(BlackjackLimits.IsCurrency(amount, 100) && amount >= options.MinimumBet && amount <= options.MaximumBet,
                "Bet must be within the table limits and use whole cents.");
            ResetCompletedRound(next);
            var funds = owner.Balance + owner.Bet;
            Require(amount <= funds, "Insufficient chips.");
            Require(funds + amount * 8 <= BlackjackLimits.MaxCurrency, "Bet could exceed the bankroll limit.");
            owner.Balance = funds - amount;
            owner.Bet = amount;
            return;
        }

        if (action == BlackjackAction.Deal)
        {
            Require(next.Phase == BlackjackPhase.Betting, "Place fresh wagers before the next deal.");
            DealRound(next);
            return;
        }

        Require(next.Phase == BlackjackPhase.PlayerTurns && next.ActiveSeatIndex == seatIndex, "It is not this player's turn.");
        var hand = owner.Hands[next.ActiveHandIndex];
        Require(!hand.IsStanding && hand.Outcome == HandOutcome.Pending, "Hand is already finished.");
        switch (action)
        {
            case BlackjackAction.Hit:
                DrawInto(next, hand.Cards);
                hand.IsStanding = BlackjackScoring.Evaluate(hand.Cards).Total >= 21;
                break;
            case BlackjackAction.Stand:
                hand.IsStanding = true;
                break;
            case BlackjackAction.DoubleDown:
                Require(hand.Cards.Count == 2, "Double down requires the first two cards.");
                Require(owner.Balance >= hand.Bet, "Insufficient chips to double down.");
                owner.Balance -= hand.Bet;
                owner.Bet += hand.Bet;
                hand.Bet *= 2;
                DrawInto(next, hand.Cards);
                hand.IsStanding = true;
                break;
            case BlackjackAction.Split:
                SplitHand(next, owner, hand);
                break;
            default:
                throw new RejectedActionException("Action is not available during a turn.");
        }
        AdvanceTurn(next);
    }

    private void DealRound(RoundState next)
    {
        var participants = next.Players.Where(player => player.IsConnected && player.Bet > 0).ToArray();
        Require(participants.Length > 0, "At least one connected player must wager.");
        Require(next.RoundNumber < int.MaxValue, "Round limit reached.");
        next.Deck = LoadDeck();
        next.DeckIndex = 0;
        next.RoundNumber++;
        next.Phase = BlackjackPhase.PlayerTurns;
        foreach (var player in participants)
            player.Hands.Add(new RoundHand { Bet = player.Bet });
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var player in participants)
                DrawInto(next, player.Hands[0].Cards);
            DrawInto(next, next.DealerCards);
        }
        foreach (var player in participants)
            player.Hands[0].IsStanding = BlackjackScoring.IsNatural(player.Hands[0].Cards);
        if (BlackjackScoring.IsNatural(next.DealerCards))
            CompleteRound(next);
        else
            AdvanceTurn(next);
    }

    private Card[] LoadDeck()
    {
        IReadOnlyList<Card> supplied;
        try
        {
            supplied = deckFactory();
        }
        catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException)
        {
            throw new RejectedActionException("The deck provider could not supply a valid deck.");
        }
        Require(supplied != null && supplied.Count == BlackjackLimits.DeckSize, "A fresh, complete 52-card deck is required.");
        var cards = new Card[BlackjackLimits.DeckSize];
        var unique = new HashSet<(int, Suit)>();
        for (var index = 0; index < cards.Length; index++)
        {
            var card = supplied![index];
            Require(BlackjackLimits.IsCard(card), "Deck contains an invalid card.");
            Require(unique.Add((card.Rank, card.Suit)), "Deck contains a duplicate card.");
            cards[index] = new Card { Rank = card.Rank, Suit = card.Suit };
        }
        return cards;
    }

    private static void SplitHand(RoundState next, SeatedPlayer player, RoundHand hand)
    {
        Require(hand.Cards.Count == 2 && hand.Cards[0].Rank == hand.Cards[1].Rank, "Split requires two cards of the same rank.");
        Require(player.Hands.Count < BlackjackLimits.MaxHands, "At most four hands are allowed.");
        Require(!(hand.IsSplit && hand.Cards[0].Rank == 1), "Split aces cannot be split again.");
        Require(player.Balance >= hand.Bet, "Insufficient chips to split.");
        player.Balance -= hand.Bet;
        player.Bet += hand.Bet;
        var split = new RoundHand { Bet = hand.Bet, IsSplit = true };
        split.Cards.Add(hand.Cards[1]);
        hand.Cards.RemoveAt(1);
        hand.IsSplit = true;
        player.Hands.Insert(next.ActiveHandIndex + 1, split);
        DrawInto(next, hand.Cards);
        DrawInto(next, split.Cards);
        var aces = hand.Cards[0].Rank == 1;
        hand.IsStanding = aces || BlackjackScoring.Evaluate(hand.Cards).Total >= 21;
        split.IsStanding = aces || BlackjackScoring.Evaluate(split.Cards).Total >= 21;
    }

    private static void AdvanceTurn(RoundState next)
    {
        foreach (var player in next.Players)
        for (var index = 0; index < player.Hands.Count; index++)
        {
            var hand = player.Hands[index];
            if (!player.IsConnected)
                hand.IsStanding = true;
            if (hand.IsStanding)
                continue;
            next.ActiveSeatIndex = player.SeatIndex;
            next.ActiveHandIndex = index;
            return;
        }
        CompleteRound(next);
    }

    private static bool NeedsDealerPlay(RoundState next) => next.Players.SelectMany(player => player.Hands)
        .Any(hand => BlackjackScoring.Evaluate(hand.Cards).Total <= 21 && !BlackjackScoring.IsNatural(hand.Cards, hand.IsSplit));

    private static void EnsureDealerCanFinish(RoundState next)
    {
        var cards = new List<Card>(next.DealerCards);
        var index = next.DeckIndex;
        while (BlackjackScoring.Evaluate(cards).Total < 17)
        {
            Require(index < next.Deck.Length && cards.Count < BlackjackLimits.MaxCardsPerHand,
                "Not enough cards remain to finish the dealer hand; stand instead.");
            cards.Add(next.Deck[index++]);
        }
    }

    private static void CompleteRound(RoundState next)
    {
        if (NeedsDealerPlay(next))
        {
            while (BlackjackScoring.Evaluate(next.DealerCards).Total < 17)
                DrawInto(next, next.DealerCards);
        }
        foreach (var player in next.Players)
        foreach (var hand in player.Hands)
        {
            hand.IsStanding = true;
            hand.Outcome = BlackjackScoring.ResolveOutcome(hand.Cards, hand.IsSplit, next.DealerCards);
            player.Balance += hand.Outcome switch
            {
                HandOutcome.Blackjack => hand.Bet * 2.5m,
                HandOutcome.Win => hand.Bet * 2,
                HandOutcome.Push => hand.Bet,
                _ => 0
            };
        }
        next.Phase = BlackjackPhase.RoundComplete;
        next.ActiveSeatIndex = -1;
        next.ActiveHandIndex = -1;
    }

    private static void ResetCompletedRound(RoundState next)
    {
        if (next.Phase != BlackjackPhase.RoundComplete)
            return;
        next.Phase = BlackjackPhase.Betting;
        next.DealerCards.Clear();
        next.Deck = Array.Empty<Card>();
        next.DeckIndex = 0;
        foreach (var player in next.Players)
        {
            player.Bet = 0;
            player.Hands.Clear();
        }
    }

    private static void DrawInto(RoundState next, List<Card> cards)
    {
        Require(next.DeckIndex < next.Deck.Length && cards.Count < BlackjackLimits.MaxCardsPerHand,
            "Not enough cards remain for this action; stand instead.");
        cards.Add(next.Deck[next.DeckIndex++]);
    }

    private static Card[] CopyCards(IEnumerable<Card> cards) =>
        cards.Select(card => new Card { Rank = card.Rank, Suit = card.Suit }).ToArray();

    private static void Require(bool condition, string error)
    {
        if (!condition)
            throw new RejectedActionException(error);
    }

    private sealed class RejectedActionException : Exception
    {
        public RejectedActionException(string message) : base(message)
        {
        }
    }

    private sealed class RoundState
    {
        public BlackjackPhase Phase;
        public int RoundNumber;
        public int ActiveSeatIndex = -1;
        public int ActiveHandIndex = -1;
        public Card[] Deck = Array.Empty<Card>();
        public int DeckIndex;
        public List<Card> DealerCards = new();
        public List<SeatedPlayer> Players = new();
        public Dictionary<ulong, decimal> RetainedBalances = new();

        public RoundState Copy() => new()
        {
            Phase = Phase,
            RoundNumber = RoundNumber,
            ActiveSeatIndex = ActiveSeatIndex,
            ActiveHandIndex = ActiveHandIndex,
            Deck = Deck,
            DeckIndex = DeckIndex,
            DealerCards = new List<Card>(DealerCards),
            Players = Players.Select(player => player.Copy()).ToList(),
            RetainedBalances = new Dictionary<ulong, decimal>(RetainedBalances)
        };
    }

    private sealed class SeatedPlayer
    {
        public int SeatIndex;
        public ulong PlayerId;
        public decimal Balance;
        public decimal Bet;
        public bool IsConnected = true;
        public List<RoundHand> Hands = new();

        public SeatedPlayer Copy() => new()
        {
            SeatIndex = SeatIndex,
            PlayerId = PlayerId,
            Balance = Balance,
            Bet = Bet,
            IsConnected = IsConnected,
            Hands = Hands.Select(hand => hand.Copy()).ToList()
        };
    }

    private sealed class RoundHand
    {
        public List<Card> Cards = new();
        public decimal Bet;
        public bool IsStanding;
        public bool IsSplit;
        public HandOutcome Outcome;

        public RoundHand Copy() => new()
        {
            Cards = new List<Card>(Cards),
            Bet = Bet,
            IsStanding = IsStanding,
            IsSplit = IsSplit,
            Outcome = Outcome
        };
    }
}

public sealed record Card
{
    public int Rank { get; init; }
    public Suit Suit { get; init; }
    public int Value => Rank == 1 ? 11 : Math.Min(Rank, 10);
}

public enum Suit
{
    Clubs,
    Diamonds,
    Hearts,
    Spades
}