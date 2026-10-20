namespace OBSharp.Interop.Browser;

public class TitleChangedEventArgs(string? title) : EventArgs
{
    public string? Title { get; } = title;
}
