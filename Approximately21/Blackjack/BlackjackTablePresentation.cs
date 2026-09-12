using System;
using System.Collections.Generic;
using Approximately21.Networking;

namespace Approximately21.Blackjack;

// Owned by BlackjackInteraction; snapshots are decoded on service events, not while rendering seats.
public sealed class BlackjackTablePresentation : IDisposable
{
    private readonly Dictionary<string, TableView> _tables = new(StringComparer.Ordinal);
    private BlackjackStateService _service;
    private ulong _localPlayerId;
    private bool _ready;
    private string _feedback;
    private bool _submitting;
    private BlackjackSeatViewState[] _unboundViews;

    public bool IsPending => _submitting || _service?.HasPendingCommand == true;

    public void Bind(BlackjackStateService service)
    {
        if (ReferenceEquals(_service, service)) return;
        if (_service != null)
        {
            _service.TableChanged -= OnTableChanged;
            _service.SessionReset -= Clear;
            _service.CommandCompleted -= OnCommandCompleted;
        }
        Clear();
        _service = service;
        if (_service == null) return;
        _service.TableChanged += OnTableChanged;
        _service.SessionReset += Clear;
        _service.CommandCompleted += OnCommandCompleted;
        foreach (var id in _service.GetTableIds()) OnTableChanged(id);
    }

    public void SetContext(ulong localPlayerId, bool ready, string feedback = null)
    {
        if (_localPlayerId == localPlayerId && _ready == ready && _feedback == feedback) return;
        _localPlayerId = localPlayerId;
        _ready = ready;
        _feedback = feedback;
        Invalidate();
    }

    public BlackjackSeatViewState GetView(string tableId, int seatIndex)
    {
        if (tableId == null || !_tables.TryGetValue(tableId, out var table))
        {
            if (_unboundViews == null)
            {
                _unboundViews = new BlackjackSeatViewState[BlackjackLimits.MaxSeats];
                for (var i = 0; i < _unboundViews.Length; i++)
                    _unboundViews[i] = BlackjackSeatViewState.Create(null, _localPlayerId, i, feedback: _feedback);
            }
            return _unboundViews[seatIndex];
        }
        var pending = IsPending;
        var ready = _ready && _service?.IsReady == true;
        if (table.Views == null || table.Pending != pending || table.Ready != ready)
        {
            table.Views = new BlackjackSeatViewState[BlackjackLimits.MaxSeats];
            table.Pending = pending;
            table.Ready = ready;
            for (var i = 0; i < table.Views.Length; i++)
            {
                table.Wagers[i] = BlackjackActionAvailability.ClampWager(table.Snapshot, i, table.Wagers[i]);
                table.Views[i] = BlackjackSeatViewState.Create(table.Snapshot, _localPlayerId, i,
                    table.Wagers[i], ready, pending, string.IsNullOrEmpty(_feedback) ? table.Feedback : _feedback);
            }
        }
        return table.Views[seatIndex];
    }

    public void AdjustWager(string tableId, int seatIndex, int direction)
    {
        var view = GetView(tableId, seatIndex);
        if (direction == 0 || (direction < 0 ? !view.CanDecrease : !view.CanIncrease)) return;
        var table = _tables[tableId];
        table.Wagers[seatIndex] = BlackjackActionAvailability.AdjustWager(table.Snapshot, seatIndex,
            table.Wagers[seatIndex], direction);
        table.Views = null;
    }

    public bool RequestAction(string tableId, int seatIndex, BlackjackAction action)
    {
        var view = GetView(tableId, seatIndex);
        if (!view.Can(action)) return false;
        var table = _tables[tableId];
        table.Feedback = null;
        _submitting = true;
        Invalidate();
        try
        {
            // The service owns the pending ID. Never install a UI pending ID after TrySubmit:
            // host (or reentrant transport) completion can already have happened inside it.
            var queued = _service.TrySubmit(tableId, action, seatIndex,
                action == BlackjackAction.Bet ? view.SelectedWager : 0m, out _);
            if (!queued) table.Feedback = "Not submitted; synchronize and try again.";
            return queued;
        }
        finally
        {
            _submitting = false;
            Invalidate();
        }
    }

    private void OnTableChanged(string tableId)
    {
        if (!_service.TryGetTable(tableId, out var replica))
        {
            _tables.Remove(tableId);
            return;
        }
        if (!_tables.TryGetValue(tableId, out var table))
            _tables.Add(tableId, table = new TableView());
        table.Snapshot = replica.State;
        table.Views = null;
    }

    private void OnCommandCompleted(CommandCompletion completion)
    {
        if (_tables.TryGetValue(completion.TableId, out var table))
            table.Feedback = completion.Accepted ? "Accepted" : completion.Error ?? "Command rejected.";
        Invalidate();
    }

    private void Invalidate()
    {
        _unboundViews = null;
        foreach (var table in _tables.Values) table.Views = null;
    }

    private void Clear()
    {
        _tables.Clear();
        _unboundViews = null;
    }
    public void Dispose() => Bind(null);

    private sealed class TableView
    {
        public BlackjackSnapshot Snapshot;
        public readonly decimal[] Wagers = { 10m, 10m, 10m, 10m, 10m };
        public BlackjackSeatViewState[] Views;
        public string Feedback;
        public bool Pending;
        public bool Ready;
    }
}