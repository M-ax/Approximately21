using System;
using System.Collections.Generic;
using Approximately21.Networking.Unity;
using UnityEngine;

namespace Approximately21.Blackjack;

public sealed class BlackjackInteraction : InteractionBase
{
    private readonly BlackjackTablePresentation _presentation = new();
    private BlackjackNetworkingRuntime _runtime;
    private BlackjackSeatInteraction[] _seats;
    private readonly List<Action> _unsubscribeSeats = new();

    public BlackjackInteraction(string prefabName)
        : base(prefabName)
    {
    }

    public void RegisterSeats(Bounds feltBounds)
    {
        if (_seats != null) return;
        _seats = BlackjackSeatInteraction.RegisterFive(feltBounds, RegisterButton, GetView);
        foreach (var seat in _seats)
        {
            var index = seat.SeatIndex;
            Action<InteractionContext, BlackjackAction> request = (context, action) =>
            {
                RefreshRuntime();
                if (TryResolve(context, out var tableId)) _presentation.RequestAction(tableId, index, action);
            };
            Action<InteractionContext, int> adjust = (context, direction) =>
            {
                RefreshRuntime();
                if (TryResolve(context, out var tableId)) _presentation.AdjustWager(tableId, index, direction);
            };
            seat.ActionRequested += request;
            seat.WagerAdjustmentRequested += adjust;
            _unsubscribeSeats.Add(() =>
            {
                seat.ActionRequested -= request;
                seat.WagerAdjustmentRequested -= adjust;
            });
        }
    }

    public void RefreshRuntime()
    {
        var runtime = BlackjackNetworkingRuntime.Instance;
        if (!ReferenceEquals(_runtime, runtime))
        {
            if (!ReferenceEquals(_runtime, null)) _runtime.ServiceChanged -= BindService;
            _runtime = runtime;
            if (_runtime != null) _runtime.ServiceChanged += BindService;
            BindService();
        }
        _presentation.SetContext(_runtime != null ? _runtime.LocalPlayerId : 0,
            _runtime != null && _runtime.LocalPlayerId != 0 && _runtime.Service?.IsReady == true,
            _runtime != null ? _runtime.Feedback : "Waiting for game session.");
    }

    private void BindService() => _presentation.Bind(_runtime != null ? _runtime.Service : null);

    private bool TryResolve(InteractionContext context, out string tableId)
    {
        tableId = null;
        return context.IsValid && _runtime != null && _runtime.Registry != null &&
            _runtime.Registry.TryResolve(context.World, context.Block, out tableId);
    }

    private BlackjackSeatViewState GetView(InteractionContext context, int seatIndex) =>
        _presentation.GetView(TryResolve(context, out var tableId) ? tableId : null, seatIndex);

    public override void Dispose()
    {
        foreach (var unsubscribe in _unsubscribeSeats) unsubscribe();
        _unsubscribeSeats.Clear();
        if (!ReferenceEquals(_runtime, null)) _runtime.ServiceChanged -= BindService;
        _runtime = null;
        _presentation.Dispose();
        base.Dispose();
    }
}
