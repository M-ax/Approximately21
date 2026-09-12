namespace Approximately21;

public readonly struct InteractionVisualState
{
    public InteractionVisualState(string text, bool enabled)
    {
        Text = text ?? string.Empty;
        Enabled = enabled;
    }

    public string Text { get; }
    public bool Enabled { get; }
}