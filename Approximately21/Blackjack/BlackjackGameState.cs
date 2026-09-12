namespace Approximately21.Blackjack;

public sealed class BlackjackGameState
{
    public int TestButtonClickCount { get; private set; }

    internal void RecordTestButtonClick(int tableEntityIndex)
    {
        TestButtonClickCount++;
        Plugin.LogInfo(
            $"Blackjack table entity {tableEntityIndex}'s test button was clicked " +
            $"({TestButtonClickCount} total clicks for this table).");
    }
}

public class Player
{
    
}

public class Dealer
{
    
}