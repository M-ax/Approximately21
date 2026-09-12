using System;
using Approximately21.Blackjack;

namespace Approximately21.Networking;

public sealed class BlackjackStateService : GameStateService<BlackjackGameState, BlackjackMessage, BlackjackSnapshot>
{
    public BlackjackStateService(IGameTransport transport, Func<BlackjackGameState> createGame = null,
        Func<DateTimeOffset> clock = null)
        : base(transport, BlackjackProtocol.Instance, createGame ?? (() => new BlackjackGameState()), clock)
    {
    }

    public bool TryGetTable(string tableId, out ReplicatedTable table)
    {
        table = null;
        if (!base.TryGetTable(tableId, out var snapshot))
            return false;
        table = new ReplicatedTable(snapshot.TableId, snapshot.Revision, snapshot.State);
        return true;
    }

    public bool TrySubmit(string tableId, BlackjackAction action, int seatIndex, decimal amount, out long commandId) =>
        TrySubmit(tableId, new BlackjackMessage { Action = action, SeatIndex = seatIndex, Amount = amount }, out commandId);

    protected override bool TryApply(BlackjackGameState game, ulong playerId, BlackjackMessage command, out string error) =>
        game.TryApply(playerId, command.Action, command.SeatIndex, command.Amount, out error);

    protected override bool DisconnectPlayer(BlackjackGameState game, ulong playerId) => game.DisconnectPlayer(playerId);

    protected override BlackjackSnapshot CreateSnapshot(BlackjackGameState game) => game.CreateSnapshot();
}

public sealed record ReplicatedTable(string TableId, long Revision, BlackjackSnapshot State)
    : ReplicatedTable<BlackjackSnapshot>(TableId, Revision, State);
