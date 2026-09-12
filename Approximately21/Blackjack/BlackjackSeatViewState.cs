using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Approximately21.Blackjack;

// Detached presentation only. The interaction owner caches one view per session/table/seat.
public sealed class BlackjackSeatViewState
{
    private readonly bool[] _actions;
    private BlackjackSeatViewState(string header, string status, decimal wager, bool[] actions, bool minus, bool plus)
    {
        Header = header;
        Status = status;
        SelectedWager = wager;
        _actions = actions;
        CanDecrease = minus;
        CanIncrease = plus;
    }

    public string Header { get; }
    public string Status { get; }
    public IReadOnlyList<string> HandSummaries { get; private init; }
    public string DealerSummary { get; private init; }
    public decimal SelectedWager { get; }
    public string WagerText => "BET " + Money(SelectedWager);
    public bool CanDecrease { get; }
    public bool CanIncrease { get; }
    public bool Can(BlackjackAction action) => (int)action >= 0 && (int)action < _actions.Length && _actions[(int)action];

    public static BlackjackSeatViewState Create(BlackjackSnapshot snapshot, ulong localPlayerId, int seatIndex,
        decimal selectedWager = 10m, bool ready = false, bool pending = false, string feedback = null)
    {
        if (seatIndex < 0 || seatIndex >= BlackjackLimits.MaxSeats) throw new ArgumentOutOfRangeException(nameof(seatIndex));
        var wager = BlackjackActionAvailability.ClampWager(snapshot, seatIndex, selectedWager);
        var actions = Enum.GetValues(typeof(BlackjackAction)).Cast<BlackjackAction>()
            .Select(a => BlackjackActionAvailability.Can(snapshot, localPlayerId, seatIndex, a, wager, ready, pending)).ToArray();
        var player = snapshot?.Players.FirstOrDefault(p => p.SeatIndex == seatIndex);
        var owner = player == null ? "OPEN" : player.PlayerId == localPlayerId ? "YOU" : player.PlayerId.ToString(CultureInfo.InvariantCulture);
        if (player != null && !player.IsConnected) owner += " OFFLINE";
        var header = $"SEAT {seatIndex + 1} {owner}";
        if (player != null) header += $"\nBAL {Money(player.Balance)} STAKE {Money(player.Bet)}";
        var status = snapshot == null ? "SYNCHRONIZING" : snapshot.Phase switch
        {
            BlackjackPhase.Betting => "BETTING",
            BlackjackPhase.RoundComplete => "ROUND COMPLETE",
            _ => snapshot.ActiveSeatIndex == seatIndex ? $"TURN H{snapshot.ActiveHandIndex + 1}" : "WAITING"
        };
        var hands = player == null ? Array.Empty<string>() : player.Hands.Select((h, i) =>
            $"H{i + 1} {Cards(h.Cards)}={BlackjackScoring.Evaluate(h.Cards).Total}" +
            (h.Outcome != HandOutcome.Pending ? " " + h.Outcome : h.IsStanding ? " STAND" : "")).ToArray();
        var dealer = string.Empty;
        if (snapshot != null && snapshot.DealerCards.Length > 0)
        {
            // Even an accidentally unredacted snapshot must not display the hole card or full score.
            var visible = snapshot.DealerHoleCardHidden ? snapshot.DealerCards.Take(1).ToArray() : snapshot.DealerCards;
            dealer = "D " + Cards(visible) + (snapshot.DealerHoleCardHidden ? " ?" : "=" + BlackjackScoring.Evaluate(visible).Total);
        }
        if (pending) status = "COMMAND PENDING\n" + status;
        else if (!ready && snapshot != null) status = "NOT CONNECTED\n" + status;
        if (!string.IsNullOrWhiteSpace(feedback)) status =
            (feedback.Length > 64 ? feedback.Substring(0, 61) + "..." : feedback) + "\n" + status;
        var adjust = ready && !pending && player != null && player.PlayerId == localPlayerId && player.IsConnected &&
            snapshot.Phase != BlackjackPhase.PlayerTurns;
        return new BlackjackSeatViewState(header, status, wager, actions,
            adjust && BlackjackActionAvailability.AdjustWager(snapshot, seatIndex, wager, -1) < wager,
            adjust && BlackjackActionAvailability.AdjustWager(snapshot, seatIndex, wager, 1) > wager)
        {
            HandSummaries = Array.AsReadOnly(hands),
            DealerSummary = dealer
        };
    }

    private static string Money(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Cards(Card[] cards) => string.Join(" ", cards.Select(c =>
        (c.Rank switch { 1 => "A", 11 => "J", 12 => "Q", 13 => "K", _ => c.Rank.ToString(CultureInfo.InvariantCulture) }) +
        c.Suit.ToString().Substring(0, 1).ToUpperInvariant()));
}