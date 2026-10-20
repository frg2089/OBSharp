namespace OBSharp.Interop.Browser;

public class UrlChangedEventArgs(string? url) : EventArgs
{
    public string? Url { get; } = url;
}
