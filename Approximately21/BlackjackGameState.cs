namespace Approximately21;

public sealed class BlackjackGameState
{
    public int TestButtonClickCount { get; private set; }

    internal void RecordTestButtonClick(int tableEntityIndex)
    {
        TestButtonClickCount++;
        Plugin.Log.LogInfo(
            $"Blackjack table entity {tableEntityIndex}'s test button was clicked " +
            $"({TestButtonClickCount} total clicks for this table).");
    }
}