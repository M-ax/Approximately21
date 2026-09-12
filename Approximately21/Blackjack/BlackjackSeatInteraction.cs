using System;
using System.Collections.Generic;
using UnityEngine;

namespace Approximately21.Blackjack;

// Shared definitions only: the owner supplies cached, per-context presentation state.
public sealed class BlackjackSeatInteraction
{
    private readonly List<InteractionButton> _controls = new();
    private readonly Func<InteractionContext, BlackjackSeatViewState> _view;

    public BlackjackSeatInteraction(BlackjackSeatPose pose, Func<InteractionContext, BlackjackSeatViewState> view)
    {
        if (pose.SeatIndex < 0 || pose.SeatIndex >= BlackjackLimits.MaxSeats)
            throw new ArgumentOutOfRangeException(nameof(pose));
        SeatIndex = pose.SeatIndex;
        _view = view ?? throw new ArgumentNullException(nameof(view));
        Add(pose, "Header", BlackjackSeatLayout.Header, v => new InteractionVisualState(v.Header, false));
        Add(pose, "Amount", BlackjackSeatLayout.Wager, v => new InteractionVisualState(v.WagerText, false));
        Add(pose, "Status", BlackjackSeatLayout.SummaryRectangle(0), v => new InteractionVisualState(v.Status, false));
        for (var i = 0; i < BlackjackLimits.MaxHands; i++)
        {
            var hand = i;
            Add(pose, $"Hand{i}", BlackjackSeatLayout.SummaryRectangle(i + 1), v =>
                new InteractionVisualState(hand < v.HandSummaries.Count ? v.HandSummaries[hand] : string.Empty, false));
        }
        Add(pose, "Dealer", BlackjackSeatLayout.SummaryRectangle(5), v => new InteractionVisualState(v.DealerSummary, false));
        foreach (BlackjackAction action in Enum.GetValues(typeof(BlackjackAction)))
        {
            var label = action == BlackjackAction.DoubleDown ? "DOUBLE DOWN" : action.ToString().ToUpperInvariant();
            var button = Add(pose, action.ToString(), BlackjackSeatLayout.ActionRectangle((int)action),
                v => new InteractionVisualState(label, v.Can(action)));
            button.Clicked += context =>
            {
                if (_view(context).Can(action)) ActionRequested?.Invoke(context, action);
            };
        }
        var minus = Add(pose, "Minus", BlackjackSeatLayout.Minus, v => new InteractionVisualState("-", v.CanDecrease));
        var plus = Add(pose, "Plus", BlackjackSeatLayout.Plus, v => new InteractionVisualState("+", v.CanIncrease));
        minus.Clicked += context =>
        {
            if (_view(context).CanDecrease) WagerAdjustmentRequested?.Invoke(context, -1);
        };
        plus.Clicked += context =>
        {
            if (_view(context).CanIncrease) WagerAdjustmentRequested?.Invoke(context, 1);
        };
        Controls = _controls.AsReadOnly();
    }

    public int SeatIndex { get; }
    public IReadOnlyList<InteractionButton> Controls { get; }
    public event Action<InteractionContext, BlackjackAction> ActionRequested;
    public event Action<InteractionContext, int> WagerAdjustmentRequested;

    // Call from the interaction owner before TryConfigureAttachedComponents, passing RegisterButton.
    // Bounds must be the actual felt bounds returned by BlackjackTable, not the whole table mesh.
    public static BlackjackSeatInteraction[] RegisterFive(Bounds bounds, Action<InteractionButton> register,
        Func<InteractionContext, int, BlackjackSeatViewState> view)
    {
        if (register == null) throw new ArgumentNullException(nameof(register));
        if (view == null) throw new ArgumentNullException(nameof(view));
        var poses = BlackjackSeatLayout.Create(bounds.size.x, bounds.size.z);
        var seats = new BlackjackSeatInteraction[BlackjackLimits.MaxSeats];
        for (var i = 0; i < seats.Length; i++)
        {
            var index = i;
            seats[i] = new BlackjackSeatInteraction(poses[i], context => view(context, index));
            foreach (var control in seats[i].Controls) register(control);
        }
        return seats;
    }

    private InteractionButton Add(BlackjackSeatPose pose, string name, SeatRectangle rectangle,
        Func<BlackjackSeatViewState, InteractionVisualState> visual)
    {
        var center = pose.Transform(rectangle.X * pose.Width, rectangle.Z * pose.Depth);
        var button = new InteractionButton($"Seat{SeatIndex}_{name}",
            new Vector2((float)(rectangle.Width * pose.Width), (float)(rectangle.Depth * pose.Depth)),
            new Vector3((float)center.X, 0, (float)center.Z))
        {
            RotationRadians = (float)pose.RotationRadians,
            GetVisualState = context => visual(_view(context))
        };
        _controls.Add(button);
        return button;
    }
}