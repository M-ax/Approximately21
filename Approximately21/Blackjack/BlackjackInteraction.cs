using System.Collections.Generic;
using UnityEngine;

namespace Approximately21.Blackjack;

public sealed class BlackjackInteraction : InteractionBase
{
    // private readonly Dictionary<int, BlackjackGameState> _gameStates = new();

    private readonly BlackjackGameState GameState = new();

    public BlackjackInteraction(string prefabName)
        : base(prefabName)
    {
        var testButton = new InteractionButton("Test", new Vector2(0.3f, 0.1f), Vector3.zero);
        testButton.Clicked += HandleTestButtonClicked;
        RegisterButton(testButton);
    }

    private void HandleTestButtonClicked(int tableEntityIndex)
    {
        GameState.RecordTestButtonClick(tableEntityIndex);
        // if (!_gameStates.TryGetValue(tableEntityIndex, out var gameState))
        // {
        //     gameState = new BlackjackGameState();
        //     _gameStates.Add(tableEntityIndex, gameState);
        // }
        //
        // gameState.RecordTestButtonClick(tableEntityIndex);
    }
}
