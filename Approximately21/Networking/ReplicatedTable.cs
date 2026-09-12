namespace Approximately21.Networking;

public record ReplicatedTable<TSnapshot>(string TableId, long Revision, TSnapshot State) where TSnapshot : class;

public sealed record CommandCompletion(long CommandId, string TableId, bool Accepted, string Error);