using System;
using Approximately21.Blackjack;

namespace Approximately21.Networking;

public sealed record BlackjackMessage : GameMessage<BlackjackSnapshot>
{
    public BlackjackMessage()
    {
        Protocol = BlackjackProtocol.Name;
        ProtocolVersion = BlackjackProtocol.Version;
    }

    public BlackjackAction Action { get; init; }
    public int SeatIndex { get; init; }
    public decimal Amount { get; init; }
}

public static class BlackjackProtocol
{
    public const string Name = "Approximately21.Blackjack";
    public const int Version = 2;
    public const int MaxMessageBytes = NetworkLimits.MaxMessageBytes;
    public const int MaxPeers = NetworkLimits.MaxPeers;
    public const int MaxTablesPerSession = NetworkLimits.MaxTablesPerSession;
    public const int MaxMessagesPerPump = NetworkLimits.MaxMessagesPerPump;

    public static GameProtocol<BlackjackMessage, BlackjackSnapshot> Instance { get; } = new Codec();

    public static byte[] Encode(BlackjackMessage message) => Instance.Encode(message);

    public static bool TryDecode(byte[] payload, out BlackjackMessage message) => Instance.TryDecode(payload, out message);

    public static bool IsTableIdValid(string tableId) => GameProtocol<BlackjackMessage, BlackjackSnapshot>.IsTableIdValid(tableId);

    private sealed class Codec : GameProtocol<BlackjackMessage, BlackjackSnapshot>
    {
        public Codec() : base(BlackjackProtocol.Name, BlackjackProtocol.Version)
        {
        }

        public override bool IsCommandValid(BlackjackMessage message) => message != null &&
            Enum.IsDefined(typeof(BlackjackAction), message.Action) &&
            message.SeatIndex >= 0 && message.SeatIndex < BlackjackLimits.MaxSeats &&
            (message.Action == BlackjackAction.Bet
                ? BlackjackLimits.IsCurrency(message.Amount, 100) && message.Amount > 0
                : message.Amount == 0);

        protected override bool IsSnapshotValid(BlackjackSnapshot snapshot) => BlackjackSnapshotValidator.IsValid(snapshot);
    }
}
