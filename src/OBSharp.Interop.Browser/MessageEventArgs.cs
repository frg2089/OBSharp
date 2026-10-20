namespace OBSharp.Interop.Browser;

public class MessageEventArgs(string? json) : EventArgs
{
    public string? Json { get; } = json;
}
