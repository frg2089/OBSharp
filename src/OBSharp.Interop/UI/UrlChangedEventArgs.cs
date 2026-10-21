namespace OBSharp.Interop.UI;

public class UrlChangedEventArgs(string? url) : EventArgs
{
    public string? Url { get; } = url;
}
