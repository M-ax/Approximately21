using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Approximately21.Networking;

public abstract class GameProtocol<TMessage, TSnapshot>
    where TMessage : GameMessage<TSnapshot>, new()
    where TSnapshot : class
{
    private static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = 16,
        IgnoreReadOnlyProperties = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    protected GameProtocol(string name, int version)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            throw new ArgumentException("A bounded, unique game protocol name is required.", nameof(name));
        if (version <= 0)
            throw new ArgumentOutOfRangeException(nameof(version));
        Name = name;
        Version = version;
    }

    public string Name { get; }
    public int Version { get; }

    public abstract bool IsCommandValid(TMessage message);
    protected abstract bool IsSnapshotValid(TSnapshot snapshot);

    public byte[] Encode(TMessage message)
    {
        if (!IsValid(message))
            throw new ArgumentException("Invalid game protocol message.", nameof(message));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        if (bytes.Length > NetworkLimits.MaxMessageBytes)
            throw new ArgumentException("Game message exceeds the wire limit.", nameof(message));
        return bytes;
    }

    public bool TryDecode(byte[] payload, out TMessage message)
    {
        message = null;
        if (payload == null || payload.Length == 0 || payload.Length > NetworkLimits.MaxMessageBytes)
            return false;
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(nameof(GameMessage<TSnapshot>.Protocol), out var protocol) ||
                protocol.ValueKind != JsonValueKind.String || protocol.GetString() != Name ||
                !document.RootElement.TryGetProperty(nameof(GameMessage<TSnapshot>.ProtocolVersion), out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != Version)
                return false;
            var decoded = JsonSerializer.Deserialize<TMessage>(payload, Options);
            if (!IsValid(decoded))
                return false;
            message = decoded;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsTableIdValid(string tableId)
    {
        if (string.IsNullOrWhiteSpace(tableId) || tableId.Length > 128)
            return false;
        foreach (var character in tableId)
            if (char.IsControl(character))
                return false;
        return true;
    }

    private bool IsValid(TMessage message)
    {
        if (message == null || message.Protocol != Name || message.ProtocolVersion != Version ||
            message.LobbyId == 0 || !Enum.IsDefined(typeof(GameMessageKind), message.Kind) ||
            message.Revision < 0 || message.CommandId < 0 || message.Error?.Length > 256)
            return false;

        if (message.Kind == GameMessageKind.SyncRequest)
            return message.ClientSessionId != Guid.Empty && message.SyncRequestId != Guid.Empty && message.State == null;
        if (message.SessionEpoch == Guid.Empty)
            return false;
        if (message.Kind == GameMessageKind.Welcome)
            return message.ClientSessionId != Guid.Empty && message.SyncRequestId != Guid.Empty && message.State == null;
        if (!IsTableIdValid(message.TableId))
            return false;

        return message.Kind switch
        {
            GameMessageKind.Command => message.ClientSessionId != Guid.Empty && message.CommandId > 0 &&
                message.Revision > 0 && message.State == null && IsCommandValid(message),
            GameMessageKind.Snapshot => message.Revision > 0 && message.State != null && IsSnapshotValid(message.State),
            GameMessageKind.TableRemoved => message.Revision > 0 && message.State == null,
            GameMessageKind.CommandResult => message.ClientSessionId != Guid.Empty && message.CommandId > 0 &&
                message.State == null,
            _ => false
        };
    }
}