namespace OBSharp.Interop.UI;

public class TitleChangedEventArgs(string? title) : EventArgs
{
    public string? Title { get; } = title;
}
