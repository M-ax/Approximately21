using System;
using System.Linq;

namespace Approximately21.Blackjack;

public static class BlackjackActionAvailability
{
    public static decimal AvailableFunds(BlackjackSnapshot snapshot, PlayerSnapshot player) =>
        player == null ? 0 : player.Balance + (snapshot.Phase == BlackjackPhase.Betting ? player.Bet : 0);

    public static decimal ClampWager(BlackjackSnapshot snapshot, int seatIndex, decimal selected = 10m)
    {
        if (snapshot == null) return 10m;
        var player = snapshot.Players.FirstOrDefault(p => p.SeatIndex == seatIndex);
        if (player == null) return Math.Max(snapshot.MinimumBet, Math.Min(snapshot.MaximumBet, selected));
        var maximum = decimal.Floor(Math.Max(0, Math.Min(snapshot.MaximumBet, AvailableFunds(snapshot, player))) * 100m) / 100m;
        if (maximum < snapshot.MinimumBet) return maximum;
        return Math.Max(snapshot.MinimumBet, Math.Min(maximum, selected));
    }

    public static decimal AdjustWager(BlackjackSnapshot snapshot, int seatIndex, decimal selected, int direction) =>
        ClampWager(snapshot, seatIndex, selected + Math.Sign(direction));

    public static bool Can(BlackjackSnapshot snapshot, ulong localPlayerId, int seatIndex,
        BlackjackAction action, decimal selectedWager, bool ready, bool pending)
    {
        if (!ready || pending || snapshot == null || localPlayerId == 0 || seatIndex < 0 || seatIndex >= BlackjackLimits.MaxSeats)
            return false;
        var occupant = snapshot.Players.FirstOrDefault(p => p.SeatIndex == seatIndex);
        var local = snapshot.Players.FirstOrDefault(p => p.PlayerId == localPlayerId);
        var betweenRounds = snapshot.Phase != BlackjackPhase.PlayerTurns;
        if (action == BlackjackAction.Join)
            return local != null ? local.SeatIndex == seatIndex && !local.IsConnected :
                betweenRounds && (occupant == null || !occupant.IsConnected);
        if (occupant == null || occupant.PlayerId != localPlayerId || !occupant.IsConnected) return false;
        switch (action)
        {
            case BlackjackAction.Leave: return betweenRounds;
            case BlackjackAction.Bet:
                return betweenRounds && BlackjackLimits.IsCurrency(selectedWager, 100) &&
                    selectedWager >= snapshot.MinimumBet && selectedWager <= snapshot.MaximumBet &&
                    selectedWager <= AvailableFunds(snapshot, occupant) &&
                    AvailableFunds(snapshot, occupant) + selectedWager * 8 <= BlackjackLimits.MaxCurrency;
            case BlackjackAction.Deal:
                return snapshot.Phase == BlackjackPhase.Betting && snapshot.Players.Any(p => p.IsConnected && p.Bet > 0);
        }
        if (snapshot.Phase != BlackjackPhase.PlayerTurns || snapshot.ActiveSeatIndex != seatIndex ||
            snapshot.ActiveHandIndex < 0 || snapshot.ActiveHandIndex >= occupant.Hands.Length) return false;
        var hand = occupant.Hands[snapshot.ActiveHandIndex];
        if (hand.IsStanding || hand.Outcome != HandOutcome.Pending || hand.Cards.Length == 0 ||
            BlackjackScoring.Evaluate(hand.Cards).Total >= 21) return false;
        switch (action)
        {
            case BlackjackAction.Hit: return hand.Cards.Length < BlackjackLimits.MaxCardsPerHand;
            case BlackjackAction.Stand: return true;
            case BlackjackAction.DoubleDown: return hand.Cards.Length == 2 && occupant.Balance >= hand.Bet;
            case BlackjackAction.Split:
                return hand.Cards.Length == 2 && hand.Cards[0].Rank == hand.Cards[1].Rank &&
                    !(hand.IsSplit && hand.Cards[0].Rank == 1) && occupant.Hands.Length < BlackjackLimits.MaxHands &&
                    occupant.Balance >= hand.Bet;
            default: return false;
        }
    }
}