using System;

namespace Approximately21.Blackjack;

public enum BlackjackAction
{
    Join,
    Leave,
    Bet,
    Deal,
    Hit,
    Stand,
    DoubleDown,
    Split
}

public enum BlackjackPhase
{
    Betting,
    PlayerTurns,
    RoundComplete
}

public enum HandOutcome
{
    Pending,
    Win,
    Loss,
    Push,
    Blackjack
}

public sealed class BlackjackSnapshot
{
    public decimal MinimumBet { get; init; }
    public decimal MaximumBet { get; init; }
    public BlackjackPhase Phase { get; init; }
    public int RoundNumber { get; init; }
    public int ActiveSeatIndex { get; init; } = -1;
    public int ActiveHandIndex { get; init; } = -1;
    public bool DealerHoleCardHidden { get; init; }
    public Card[] DealerCards { get; init; } = Array.Empty<Card>();
    public PlayerSnapshot[] Players { get; init; } = Array.Empty<PlayerSnapshot>();
    public int TestButtonClickCount { get; init; }
}

public sealed class PlayerSnapshot
{
    public int SeatIndex { get; init; }
    public ulong PlayerId { get; init; }
    public decimal Balance { get; init; }
    public decimal Bet { get; init; }
    public bool IsConnected { get; init; }
    public HandSnapshot[] Hands { get; init; } = Array.Empty<HandSnapshot>();
}

public sealed class HandSnapshot
{
    public Card[] Cards { get; init; } = Array.Empty<Card>();
    public decimal Bet { get; init; }
    public bool IsStanding { get; init; }
    public bool IsSplit { get; init; }
    public HandOutcome Outcome { get; init; }
}