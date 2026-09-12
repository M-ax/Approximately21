using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Approximately21.Networking;

public sealed class BlackjackTableBindings<THandle> : IDisposable
{
    private readonly BlackjackStateService _service;
    private readonly Dictionary<THandle, int> _blocks = new();
    private readonly Dictionary<int, string> _tables = new();
    private readonly HashSet<int> _failed = new();

    public BlackjackTableBindings(BlackjackStateService service)
    {
        _service = service;
        service.TableChanged += OnTableChanged;
        service.SessionReset += Clear;
        foreach (var id in service.GetTableIds())
            OnTableChanged(id);
    }

    public event Action<string> RegistrationFailed;

    public void Observe(THandle handle, int nativeId)
    {
        if (_blocks.TryGetValue(handle, out var previous) && previous != nativeId)
            ConfirmDestroyed(handle);
        _blocks[handle] = nativeId;
        if (!_service.IsHost || _tables.ContainsKey(nativeId) || _failed.Contains(nativeId))
            return;
        var id = $"blackjack:{nativeId.ToString(CultureInfo.InvariantCulture)}:{Guid.NewGuid():N}";
        if (!_service.RegisterTable(id))
        {
            _failed.Add(nativeId);
            RegistrationFailed?.Invoke($"Could not register blackjack block {nativeId}; the session identity limit may be exhausted.");
        }
    }

    public bool TryResolve(THandle handle, out string tableId)
    {
        tableId = null;
        return _service.IsReady && _blocks.TryGetValue(handle, out var nativeId) &&
               _blocks.Values.Count(id => id == nativeId) == 1 &&
               _tables.TryGetValue(nativeId, out tableId);
    }

    public void ConfirmDestroyed(THandle handle)
    {
        if (!_blocks.Remove(handle, out var nativeId) || _blocks.ContainsValue(nativeId))
            return;
        _failed.Remove(nativeId);
        if (_service.IsHost && _tables.TryGetValue(nativeId, out var id))
            _service.RemoveTable(id);
    }

    private void OnTableChanged(string tableId)
    {
        if (!TryParse(tableId, out var nativeId))
            return;
        if (_service.TryGetTable(tableId, out _))
        {
            // Never choose arbitrarily between conflicting authoritative identities.
            var matches = _service.GetTableIds().Where(id => TryParse(id, out var n) && n == nativeId).ToArray();
            if (matches.Length == 1)
                _tables[nativeId] = tableId;
            else
                _tables.Remove(nativeId);
        }
        else
        {
            _tables.Remove(nativeId);
            var remaining = _service.GetTableIds().Where(id => TryParse(id, out var n) && n == nativeId).ToArray();
            if (remaining.Length == 1)
                _tables[nativeId] = remaining[0];
        }
    }

    public static bool TryParse(string tableId, out int nativeId)
    {
        nativeId = 0;
        var parts = tableId?.Split(':');
        return parts?.Length == 3 && parts[0] == "blackjack" &&
               int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out nativeId) &&
               parts[1] == nativeId.ToString(CultureInfo.InvariantCulture) &&
               Guid.TryParseExact(parts[2], "N", out var incarnation) && incarnation != Guid.Empty;
    }

    private void Clear()
    {
        _blocks.Clear();
        _tables.Clear();
        _failed.Clear();
    }

    public void Dispose()
    {
        _service.TableChanged -= OnTableChanged;
        _service.SessionReset -= Clear;
        Clear();
    }
}