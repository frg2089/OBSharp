namespace OBSharp.Interop.UI;

public class MessageEventArgs(string? json) : EventArgs
{
    public string? Json { get; } = json;
}
